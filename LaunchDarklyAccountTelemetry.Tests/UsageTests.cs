using System.Net;
using System.Text.Json;
using LaunchDarklyAccountTelemetry.Usage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LaunchDarklyAccountTelemetry.Tests;

public class RateLimitHandlerTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _i;
        public int Calls => _i;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(responses[Math.Min(_i++, responses.Length - 1)]);
    }

    private static (HttpClient Client, List<TimeSpan> Waits, SequenceHandler Inner) Create(
        DateTimeOffset now, int maxRetries, params HttpResponseMessage[] responses)
    {
        var options = Options.Create(new LaunchDarklyUsageOptions { MaxRateLimitRetries = maxRetries, MaxRateLimitWait = TimeSpan.FromSeconds(30) });
        var waits = new List<TimeSpan>();
        var inner = new SequenceHandler(responses);
        var handler = new LaunchDarklyRateLimitHandler(options, new FixedTime(now), NullLogger<LaunchDarklyRateLimitHandler>.Instance)
        {
            InnerHandler = inner,
            Delay = (d, _) => { waits.Add(d); return Task.CompletedTask; },
        };
        return (new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") }, waits, inner);
    }

    private static HttpResponseMessage TooMany(long? resetMs)
    {
        var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (resetMs is { } ms)
        {
            r.Headers.TryAddWithoutValidation("X-Ratelimit-Reset", ms.ToString());
        }

        return r;
    }

    [Fact]
    public async Task Waits_until_X_Ratelimit_Reset_then_retries()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var (client, waits, inner) = Create(now, 3, TooMany(now.AddSeconds(4).ToUnixTimeMilliseconds()), new HttpResponseMessage(HttpStatusCode.OK));

        var response = await client.GetAsync("x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls);
        var wait = Assert.Single(waits);
        Assert.InRange(wait, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4.3));
    }

    [Fact]
    public async Task Clamps_wait_to_MaxRateLimitWait()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var (client, waits, _) = Create(now, 1, TooMany(now.AddMinutes(10).ToUnixTimeMilliseconds()), new HttpResponseMessage(HttpStatusCode.OK));

        await client.GetAsync("x");

        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(waits));
    }

    [Fact]
    public async Task Missing_header_uses_exponential_backoff_and_gives_up_after_max_retries()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var (client, waits, inner) = Create(now, 2, TooMany(null));

        var response = await client.GetAsync("x");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(3, inner.Calls);
        Assert.Equal(2, waits.Count);
        Assert.InRange(waits[0], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.3));
        Assert.InRange(waits[1], TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.3));
    }
}

public class UsageMapperTests
{
    [Fact]
    public void Maps_series_keys_to_metadata_attributes()
    {
        var raw = new RawUsageSeries(
            [
                new Dictionary<string, object?> { ["projectId"] = "p1", ["projectName"] = "proj", ["environmentId"] = "e1", ["environmentKey"] = "staging", ["environmentName"] = "Staging" },
                new Dictionary<string, object?> { ["projectId"] = "p1", ["environmentId"] = "e2" },
            ],
            [
                new Dictionary<string, object?> { ["time"] = 1_767_225_600_000d, ["0"] = 60d, ["1"] = 12.5 },
                new Dictionary<string, object?> { ["time"] = 1_767_229_200_000d, ["1"] = 3d },
            ]);

        var points = UsageMapper.Map(raw);

        Assert.Equal(3, points.Count);
        var first = points[0];
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), first.HourUtc);
        Assert.Equal(60, first.Value);
        Assert.Equal("e1", first.Attributes["launchdarkly.environment.id"]);
        Assert.Equal("staging", first.Attributes["launchdarkly.environment.key"]);
        Assert.Equal("Staging", first.Attributes["launchdarkly.environment.name"]);
        Assert.Equal("proj", first.Attributes["launchdarkly.project.name"]);
        Assert.Equal("e2", points[2].Attributes["launchdarkly.environment.id"]);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T01:00:00Z"), points[2].HourUtc);
    }
}

public class OtlpPublisherTests
{
    [Fact]
    public void Payload_carries_explicit_hour_timestamp_as_string_nanos()
    {
        var options = Options.Create(new LaunchDarklyUsageOptions { Otlp = { Endpoint = "http://x" } });
        var publisher = new OtlpHttpMetricPublisher(new HttpClient(), options);
        var hour = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        var json = publisher.BuildPayload([new HourlyUsagePoint(hour, 42, new Dictionary<string, string> { ["launchdarkly.environment.key"] = "staging" })]);

        using var doc = JsonDocument.Parse(json);
        var metric = doc.RootElement.GetProperty("resourceMetrics")[0].GetProperty("scopeMetrics")[0].GetProperty("metrics")[0];
        var dp = metric.GetProperty("gauge").GetProperty("dataPoints")[0];
        Assert.Equal("launchdarkly.service_connections", metric.GetProperty("name").GetString());
        Assert.Equal("1767225600000000000", dp.GetProperty("timeUnixNano").GetString());
        Assert.Equal(42, dp.GetProperty("asDouble").GetDouble());
    }

    [Theory]
    [InlineData("http://c:4318", "http://c:4318/v1/metrics")]
    [InlineData("http://c:4318/", "http://c:4318/v1/metrics")]
    [InlineData("http://c:4318/v1/metrics", "http://c:4318/v1/metrics")]
    public void Resolves_endpoint(string configured, string expected) =>
        Assert.Equal(expected, OtlpHttpMetricPublisher.ResolveEndpoint(configured).ToString());
}

public class IngestionServiceTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeClient : IServiceConnectionUsageClient
    {
        public List<(DateTimeOffset From, DateTimeOffset To)> Requests { get; } = new();

        public Task<RawUsageSeries> GetHourlyAsync(DateTimeOffset from, DateTimeOffset to, IReadOnlyList<string> groupBy, CancellationToken ct)
        {
            Requests.Add((from, to));
            // One group with a row for every hour in [from, to], including hours past the requested end.
            var series = new List<IReadOnlyDictionary<string, object?>>();
            for (var h = from; h <= to; h += TimeSpan.FromHours(1))
            {
                series.Add(new Dictionary<string, object?> { ["time"] = (double)h.ToUnixTimeMilliseconds(), ["0"] = 60d });
            }

            return Task.FromResult(new RawUsageSeries([new Dictionary<string, object?> { ["projectId"] = "p" }], series));
        }
    }

    private sealed class CapturingPublisher : IUsageMetricPublisher
    {
        public List<HourlyUsagePoint> Points { get; } = new();

        public Task PublishAsync(IReadOnlyList<HourlyUsagePoint> points, CancellationToken ct)
        {
            Points.AddRange(points);
            return Task.CompletedTask;
        }
    }

    private static (UsageIngestionService Service, FakeClient Client, CapturingPublisher Publisher, InMemoryUsageWatermarkStore Store) Create(
        string now, Action<LaunchDarklyUsageOptions>? configure = null)
    {
        var options = new LaunchDarklyUsageOptions();
        configure?.Invoke(options);
        var client = new FakeClient();
        var publisher = new CapturingPublisher();
        var store = new InMemoryUsageWatermarkStore();
        var service = new UsageIngestionService(
            client, publisher, store, Options.Create(options),
            new FixedTime(DateTimeOffset.Parse(now)), NullLogger<UsageIngestionService>.Instance);
        return (service, client, publisher, store);
    }

    [Fact]
    public async Task Does_not_publish_hours_that_are_not_yet_final()
    {
        // 10:10 -> hour 09:00 is final only at 10:20, so latest complete hour is 08:00.
        var (service, _, publisher, store) = Create("2026-01-01T10:10:00Z");

        var result = await service.IngestAsync(DateTimeOffset.Parse("2026-01-01T06:00:00Z"), DateTimeOffset.Parse("2026-01-01T10:00:00Z"), default);

        Assert.Equal(DateTimeOffset.Parse("2026-01-01T08:00:00Z"), result.To);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T08:00:00Z"), publisher.Points.Max(p => p.HourUtc));
        Assert.Equal(3, publisher.Points.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T08:00:00Z"), await store.GetAsync(default));
    }

    [Fact]
    public async Task Hour_becomes_available_after_publication_delay()
    {
        var (service, _, publisher, _) = Create("2026-01-01T10:21:00Z");

        await service.IngestAsync(DateTimeOffset.Parse("2026-01-01T09:00:00Z"), DateTimeOffset.Parse("2026-01-01T10:00:00Z"), default);

        Assert.Equal(DateTimeOffset.Parse("2026-01-01T09:00:00Z"), Assert.Single(publisher.Points).HourUtc);
    }

    [Fact]
    public async Task Splits_large_backfill_into_chunks()
    {
        var (service, client, publisher, _) = Create("2026-01-20T00:00:00Z", o => o.MaxChunk = TimeSpan.FromDays(2));

        await service.IngestAsync(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.Parse("2026-01-06T00:00:00Z"), default);

        Assert.Equal(3, client.Requests.Count);
        Assert.All(client.Requests, r => Assert.True(r.To - r.From <= TimeSpan.FromDays(2)));
        Assert.Equal(5 * 24, publisher.Points.Count);
        Assert.Equal(publisher.Points.Count, publisher.Points.Select(p => p.HourUtc).Distinct().Count());
    }

    [Fact]
    public async Task Pending_run_resumes_from_watermark_with_rescrape_overlap()
    {
        var (service, client, _, store) = Create("2026-01-01T12:30:00Z");
        await store.SetAsync(DateTimeOffset.Parse("2026-01-01T09:00:00Z"), default);

        await service.IngestPendingAsync(default);

        // latest complete = 10:00; start = watermark + 1h - 2h rescrape = 08:00
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T08:00:00Z"), client.Requests.Single().From);
    }

    [Fact]
    public async Task Pending_run_without_watermark_uses_initial_lookback()
    {
        var (service, client, _, _) = Create("2026-01-02T00:30:00Z", o => o.InitialLookbackHours = 6);

        await service.IngestPendingAsync(default);

        // latest complete = 23:00 on the 1st; 6 hours inclusive => start 18:00
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T18:00:00Z"), client.Requests.Single().From);
    }
}
