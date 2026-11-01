using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LaunchDarklyAccountTelemetry.Usage;

public interface IUsageMetricPublisher
{
    /// <summary>Publishes points as OTLP gauge data points stamped with each point's hour.</summary>
    Task PublishAsync(IReadOnlyList<HourlyUsagePoint> points, CancellationToken cancellationToken);
}

/// <summary>
/// Pushes OTLP/HTTP JSON metrics with explicit timestamps. The .NET Metrics SDK always stamps
/// "now", so it cannot be used for historical hours / backfill.
/// </summary>
internal sealed class OtlpHttpMetricPublisher(HttpClient http, IOptions<LaunchDarklyUsageOptions> options) : IUsageMetricPublisher
{
    private readonly HttpClient _http = http;
    private readonly LaunchDarklyUsageOptions _options = options.Value;

    public async Task PublishAsync(IReadOnlyList<HourlyUsagePoint> points, CancellationToken cancellationToken)
    {
        var endpoint = ResolveEndpoint(_options.Otlp.Endpoint);
        var batchSize = Math.Max(1, _options.Otlp.BatchSize);

        foreach (var batch in points.Chunk(batchSize))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(BuildPayload(batch)),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            foreach (var (name, value) in _options.Otlp.Headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(
                    $"OTLP export to {endpoint} failed with {(int)response.StatusCode}: {body}", null, response.StatusCode);
            }
        }
    }

    internal static Uri ResolveEndpoint(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException("LaunchDarklyUsage:Otlp:Endpoint is not configured.");
        }

        var trimmed = configured.TrimEnd('/');
        return new Uri(trimmed.EndsWith("/v1/metrics", StringComparison.Ordinal) ? trimmed : trimmed + "/v1/metrics");
    }

    internal byte[] BuildPayload(IEnumerable<HourlyUsagePoint> points)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteStartArray("resourceMetrics");
            w.WriteStartObject();

            w.WriteStartObject("resource");
            w.WriteStartArray("attributes");
            WriteAttribute(w, "service.name", _options.Otlp.ServiceName);
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartArray("scopeMetrics");
            w.WriteStartObject();
            w.WriteStartObject("scope");
            w.WriteString("name", "LaunchDarklyAccountTelemetry.Usage");
            w.WriteEndObject();

            w.WriteStartArray("metrics");
            w.WriteStartObject();
            w.WriteString("name", _options.Otlp.MetricName);
            w.WriteString("description", "Service connections accrued per usage hour (connection-minutes / MinutesPerServiceConnection). Summed over a month this is the billed amount.");
            w.WriteString("unit", "{service_connection}");
            w.WriteStartObject("gauge");
            w.WriteStartArray("dataPoints");
            foreach (var p in points)
            {
                w.WriteStartObject();
                w.WriteStartArray("attributes");
                foreach (var (k, v) in p.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal))
                {
                    WriteAttribute(w, k, v);
                }

                w.WriteEndArray();
                // 64-bit ints are strings in OTLP JSON.
                w.WriteString("timeUnixNano", (p.HourUtc.ToUnixTimeMilliseconds() * 1_000_000L).ToString(System.Globalization.CultureInfo.InvariantCulture));
                w.WriteNumber("asDouble", p.Minutes / _options.MinutesPerServiceConnection);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteAttribute(Utf8JsonWriter w, string key, string value)
    {
        w.WriteStartObject();
        w.WriteString("key", key);
        w.WriteStartObject("value");
        w.WriteString("stringValue", value);
        w.WriteEndObject();
        w.WriteEndObject();
    }
}
