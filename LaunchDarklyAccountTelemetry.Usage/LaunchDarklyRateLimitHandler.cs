using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LaunchDarklyAccountTelemetry.Usage;

/// <summary>
/// Retries 429 responses following LaunchDarkly's rate-limit spec: wait until X-Ratelimit-Reset
/// (epoch milliseconds when the current window resets). Falls back to exponential backoff when the
/// header is missing or unusable. Transient 5xx responses are retried with the same backoff.
/// </summary>
internal sealed class LaunchDarklyRateLimitHandler : DelegatingHandler
{
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(1);

    private readonly LaunchDarklyUsageOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<LaunchDarklyRateLimitHandler> _logger;

    public LaunchDarklyRateLimitHandler(
        IOptions<LaunchDarklyUsageOptions> options,
        TimeProvider time,
        ILogger<LaunchDarklyRateLimitHandler> logger)
    {
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>Replaceable for tests.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (d, ct) => Task.Delay(d, ct);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var attemptRequest = attempt == 0 ? null : await CloneAsync(request, cancellationToken);
            var response = await base.SendAsync(attemptRequest ?? request, cancellationToken);

            var retryable = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                            || (int)response.StatusCode is 502 or 503 or 504;
            if (!retryable || attempt >= _options.MaxRateLimitRetries)
            {
                return response;
            }

            var wait = ComputeWait(response, attempt);
            _logger.LogWarning(
                "LaunchDarkly returned {Status} for {Uri}; retry {Attempt}/{Max} in {Wait}",
                (int)response.StatusCode, request.RequestUri, attempt + 1, _options.MaxRateLimitRetries, wait);
            response.Dispose();
            await Delay(wait, cancellationToken);
        }
    }

    internal TimeSpan ComputeWait(HttpResponseMessage response, int attempt)
    {
        TimeSpan wait;
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
            && response.Headers.TryGetValues("X-Ratelimit-Reset", out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var resetMs))
        {
            wait = DateTimeOffset.FromUnixTimeMilliseconds(resetMs) - _time.GetUtcNow();
            // Small pad so the window has definitely rolled over.
            wait = (wait < TimeSpan.Zero ? TimeSpan.Zero : wait) + TimeSpan.FromMilliseconds(Random.Shared.Next(50, 250));
        }
        else
        {
            wait = TimeSpan.FromTicks(BaseBackoff.Ticks << Math.Min(attempt, 10))
                   + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
        }

        return wait > _options.MaxRateLimitWait ? _options.MaxRateLimitWait : wait;
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage original, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri) { Version = original.Version };
        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (original.Content is not null)
        {
            var bytes = await original.Content.ReadAsByteArrayAsync(ct);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in original.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }
}
