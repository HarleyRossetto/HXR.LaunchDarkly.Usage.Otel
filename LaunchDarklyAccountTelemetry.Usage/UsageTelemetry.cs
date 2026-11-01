using System.Diagnostics.Metrics;

namespace LaunchDarklyAccountTelemetry.Usage;

/// <summary>Self-observability for the ingestion pipeline. Register with AddMeter(UsageTelemetry.MeterName).</summary>
public static class UsageTelemetry
{
    public const string MeterName = "LaunchDarklyAccountTelemetry.Usage";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static long _lastHourUnixSeconds;

    internal static readonly Counter<long> PointsPublished =
        Meter.CreateCounter<long>("launchdarkly.usage.ingest.points", "{point}", "Hourly usage points published.");

    internal static readonly Counter<long> Failures =
        Meter.CreateCounter<long>("launchdarkly.usage.ingest.failures", "{run}", "Failed ingestion runs.");

    static UsageTelemetry()
    {
        Meter.CreateObservableGauge(
            "launchdarkly.usage.ingest.last_hour",
            () => Interlocked.Read(ref _lastHourUnixSeconds),
            "s",
            "Start of the latest hour successfully ingested (unix seconds).");
    }

    internal static void RecordLastHour(DateTimeOffset hour) =>
        Interlocked.Exchange(ref _lastHourUnixSeconds, hour.ToUnixTimeSeconds());
}
