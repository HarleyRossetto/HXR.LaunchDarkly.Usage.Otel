namespace LaunchDarklyAccountTelemetry.Usage;

/// <summary>Optional record of the latest hour (start instant) published. Implement for durable state.</summary>
public interface IUsageWatermarkStore
{
    Task<DateTimeOffset?> GetAsync(CancellationToken cancellationToken);

    Task SetAsync(DateTimeOffset lastPublishedHour, CancellationToken cancellationToken);
}

public sealed class InMemoryUsageWatermarkStore : IUsageWatermarkStore
{
    private long _ticks;

    public Task<DateTimeOffset?> GetAsync(CancellationToken cancellationToken)
    {
        var ticks = Interlocked.Read(ref _ticks);
        return Task.FromResult<DateTimeOffset?>(ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero));
    }

    public Task SetAsync(DateTimeOffset lastPublishedHour, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _ticks, lastPublishedHour.UtcTicks);
        return Task.CompletedTask;
    }
}
