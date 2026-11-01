namespace LaunchDarklyAccountTelemetry.Usage;

/// <summary>Neutral view of the LaunchDarkly series response (metadata[i] describes series key "i").</summary>
public sealed record RawUsageSeries(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Metadata,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Series);

/// <summary>Service-connection minutes for one group during the hour starting at <see cref="HourUtc"/>.</summary>
public sealed record HourlyUsagePoint(
    DateTimeOffset HourUtc,
    double Minutes,
    IReadOnlyDictionary<string, string> Attributes);

public sealed record IngestionResult(
    DateTimeOffset From,
    DateTimeOffset To,
    int Requests,
    int PointsPublished)
{
    public static IngestionResult Empty(DateTimeOffset from, DateTimeOffset to) => new(from, to, 0, 0);
}
