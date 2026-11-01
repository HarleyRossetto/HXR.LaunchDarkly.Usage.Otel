using System.Text.Json;

namespace LaunchDarklyAccountTelemetry.Usage;

public interface IServiceConnectionUsageClient
{
    /// <summary>Fetches hourly incremental service-connection minutes for [from, to).</summary>
    Task<RawUsageSeries> GetHourlyAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<string> groupBy,
        CancellationToken cancellationToken);
}

/// <summary>
/// Calls GET /api/v2/usage/service-connections directly. The generated Kiota client models the
/// repeatable groupBy parameter as a single string, so it cannot express multi-dimension grouping.
/// </summary>
internal sealed class ServiceConnectionUsageClient(HttpClient http) : IServiceConnectionUsageClient
{
    private readonly HttpClient _http = http;

    public async Task<RawUsageSeries> GetHourlyAsync(
        DateTimeOffset from, DateTimeOffset to, IReadOnlyList<string> groupBy, CancellationToken cancellationToken)
    {
        var query = new List<string>
        {
            $"from={from.ToUnixTimeMilliseconds()}",
            $"to={to.ToUnixTimeMilliseconds()}",
            "granularity=hourly",
            "aggregationType=incremental",
        };
        query.AddRange(groupBy.Select(g => "groupBy=" + Uri.EscapeDataString(g)));

        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v2/usage/service-connections?" + string.Join('&', query));
        // This endpoint is beta and rejects requests without the header; set per request, not on the shared client.
        request.Headers.TryAddWithoutValidation("LD-API-Version", "beta");

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"LaunchDarkly service-connections usage request failed with {(int)response.StatusCode}: {body}", null, response.StatusCode);
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return Parse(doc.RootElement);
    }

    internal static RawUsageSeries Parse(JsonElement root) =>
        new(ReadObjects(root, "metadata"), ReadObjects(root, "series"));

    private static List<IReadOnlyDictionary<string, object?>> ReadObjects(JsonElement root, string property)
    {
        var result = new List<IReadOnlyDictionary<string, object?>>();
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in array.EnumerateArray())
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (item.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in item.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number => prop.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => null,
                    };
                }
            }

            result.Add(dict);
        }

        return result;
    }
}
