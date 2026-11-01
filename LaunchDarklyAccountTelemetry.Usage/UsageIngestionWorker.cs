using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LaunchDarklyAccountTelemetry.Usage;

internal sealed class UsageIngestionWorker(
    IUsageIngestionService ingestion,
    IOptions<LaunchDarklyUsageOptions> options,
    TimeProvider time,
    ILogger<UsageIngestionWorker> logger) : BackgroundService
{
    private readonly IUsageIngestionService _ingestion = ingestion;
    private readonly LaunchDarklyUsageOptions _options = options.Value;
    private readonly TimeProvider _time = time;
    private readonly ILogger<UsageIngestionWorker> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.StartupBackfillFrom is { } backfillFrom)
        {
            await RunAsync(ct => _ingestion.IngestAsync(backfillFrom, _time.GetUtcNow(), ct), "startup backfill", stoppingToken);
        }

        using var timer = new PeriodicTimer(_options.Interval, _time);
        do
        {
            await RunAsync(ct => _ingestion.IngestPendingAsync(ct), "scheduled ingestion", stoppingToken);
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task<IngestionResult>> run, string what, CancellationToken ct)
    {
        try
        {
            await run(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // The watermark only advances on success, so the next tick retries the same hours.
            UsageTelemetry.Failures.Add(1);
            _logger.LogError(ex, "LaunchDarkly usage {Operation} failed", what);
        }
    }
}
