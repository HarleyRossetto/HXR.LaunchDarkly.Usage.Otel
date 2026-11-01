namespace LaunchDarklyAccountTelemetry.Usage;

public sealed class LaunchDarklyUsageOptions
{
    public const string SectionName = "LaunchDarklyUsage";

    /// <summary>LaunchDarkly personal/service access token (sent raw in the Authorization header).</summary>
    public string? ApiKey { get; set; }

    public string BaseUrl { get; set; } = "https://app.launchdarkly.com";

    /// <summary>How often the background worker runs.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Hour H is considered final once now >= H + 1h + PublicationDelay.</summary>
    public TimeSpan PublicationDelay { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>Hours to look back when no watermark is known (first run / restart with in-memory store).</summary>
    public int InitialLookbackHours { get; set; } = 24;

    /// <summary>Hours before the watermark that are re-scraped each run to pick up late corrections.</summary>
    public int RescrapeHours { get; set; } = 2;

    /// <summary>Maximum span of a single API request. LaunchDarkly caps hourly granularity at 7 days.</summary>
    public TimeSpan MaxChunk { get; set; } = TimeSpan.FromDays(6);

    /// <summary>Maximum range accepted by a backfill request or automatic catch-up.</summary>
    public TimeSpan MaxBackfillRange { get; set; } = TimeSpan.FromDays(90);

    /// <summary>If set, the worker backfills from this instant on startup.</summary>
    public DateTimeOffset? StartupBackfillFrom { get; set; }

    /// <summary>Extra groupBy dimensions on top of projectId and environmentId (e.g. sdkName, connectionType).</summary>
    public List<string> ExtraGroupBy { get; set; } = new();

    public int MaxRateLimitRetries { get; set; } = 5;

    public TimeSpan MaxRateLimitWait { get; set; } = TimeSpan.FromSeconds(30);

    public OtlpOptions Otlp { get; set; } = new();
}

public sealed class OtlpOptions
{
    /// <summary>OTLP/HTTP base endpoint, e.g. http://localhost:4318 (/v1/metrics is appended if missing).</summary>
    public string? Endpoint { get; set; }

    public Dictionary<string, string> Headers { get; set; } = new();

    public string MetricName { get; set; } = "launchdarkly.service_connections.minutes";

    public string ServiceName { get; set; } = "launchdarkly-account-usage";

    public int BatchSize { get; set; } = 1000;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}
