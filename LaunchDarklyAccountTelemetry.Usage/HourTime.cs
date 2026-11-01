namespace LaunchDarklyAccountTelemetry.Usage;

internal static class HourTime
{
    public static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    public static DateTimeOffset FloorToHour(DateTimeOffset t)
    {
        var utc = t.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Start of the most recent hour whose data should be final.</summary>
    public static DateTimeOffset LatestCompleteHour(DateTimeOffset now, TimeSpan publicationDelay) =>
        FloorToHour(now - publicationDelay - Hour);
}
