using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LaunchDarklyAccountTelemetry.Usage;

public interface IUsageIngestionService
{
    /// <summary>Ingests hours whose start is in [from, to), capped at the latest complete hour. Idempotent.</summary>
    Task<IngestionResult> IngestAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    /// <summary>Ingests everything since the watermark (re-scraping a small overlap), or the initial lookback.</summary>
    Task<IngestionResult> IngestPendingAsync(CancellationToken cancellationToken);
}

internal sealed class UsageIngestionService(
    IServiceConnectionUsageClient client,
    IUsageMetricPublisher publisher,
    IUsageWatermarkStore watermark,
    IOptions<LaunchDarklyUsageOptions> options,
    TimeProvider time,
    ILogger<UsageIngestionService> logger) : IUsageIngestionService
{
    private readonly IServiceConnectionUsageClient _client = client;
    private readonly IUsageMetricPublisher _publisher = publisher;
    private readonly IUsageWatermarkStore _watermark = watermark;
    private readonly LaunchDarklyUsageOptions _options = options.Value;
    private readonly TimeProvider _time = time;
    private readonly ILogger<UsageIngestionService> _logger = logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IngestionResult> IngestPendingAsync(CancellationToken cancellationToken)
    {
        var latest = HourTime.LatestCompleteHour(_time.GetUtcNow(), _options.PublicationDelay);
        var mark = await _watermark.GetAsync(cancellationToken);

        var start = mark is { } m
            ? m + HourTime.Hour - TimeSpan.FromHours(_options.RescrapeHours)
            : latest - TimeSpan.FromHours(_options.InitialLookbackHours - 1);

        var earliest = latest - _options.MaxBackfillRange;
        if (start < earliest)
        {
            _logger.LogWarning("Pending range starts {Start:u}, beyond MaxBackfillRange; clamping to {Earliest:u}", start, earliest);
            start = earliest;
        }

        return await IngestAsync(start, latest + HourTime.Hour, cancellationToken);
    }

    public async Task<IngestionResult> IngestAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var latest = HourTime.LatestCompleteHour(_time.GetUtcNow(), _options.PublicationDelay);
            var first = HourTime.FloorToHour(from);
            var last = HourTime.FloorToHour(to - TimeSpan.FromMilliseconds(1));
            if (last > latest)
            {
                last = latest;
            }

            if (first > last)
            {
                return IngestionResult.Empty(first, last);
            }

            var chunkHours = Math.Max(1, (int)_options.MaxChunk.TotalHours);
            var groupBy = new[] { "projectId", "environmentId" }
                .Concat(_options.ExtraGroupBy)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            int requests = 0, published = 0;
            for (var chunkStart = first; chunkStart <= last; chunkStart += TimeSpan.FromHours(chunkHours))
            {
                var chunkLast = chunkStart + TimeSpan.FromHours(chunkHours - 1);
                if (chunkLast > last)
                {
                    chunkLast = last;
                }

                var raw = await _client.GetHourlyAsync(chunkStart, chunkLast + HourTime.Hour, groupBy, cancellationToken);
                requests++;

                var points = UsageMapper.Map(raw)
                    .Where(p => p.HourUtc >= chunkStart && p.HourUtc <= chunkLast)
                    .ToList();

                if (points.Count > 0)
                {
                    await _publisher.PublishAsync(points, cancellationToken);
                }

                published += points.Count;
                await AdvanceWatermarkAsync(chunkLast, cancellationToken);
                _logger.LogInformation(
                    "Published {Count} usage points for {From:u} to {To:u}", points.Count, chunkStart, chunkLast);
            }

            UsageTelemetry.PointsPublished.Add(published);
            return new IngestionResult(first, last, requests, published);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task AdvanceWatermarkAsync(DateTimeOffset hour, CancellationToken ct)
    {
        var current = await _watermark.GetAsync(ct);
        if (current is null || hour > current)
        {
            await _watermark.SetAsync(hour, ct);
            UsageTelemetry.RecordLastHour(hour);
        }
    }
}
