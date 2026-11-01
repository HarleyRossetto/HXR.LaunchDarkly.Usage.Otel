using System.Globalization;
using System.Text;

namespace LaunchDarklyAccountTelemetry.Usage;

/// <summary>
/// Turns a LaunchDarkly series response into hourly points. The beta response shape is loosely typed:
/// each series item is { "time": ms, "0": n, "1": n ... } and metadata[i] describes key "i".
/// All scalar metadata entries become attributes; well-known dimensions are renamed.
/// </summary>
internal static class UsageMapper
{
    private static readonly Dictionary<string, string> KnownAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["projectId"] = "launchdarkly.project.id",
        ["projectName"] = "launchdarkly.project.name",
        ["environmentId"] = "launchdarkly.environment.id",
        ["environmentKey"] = "launchdarkly.environment.key",
        ["environmentName"] = "launchdarkly.environment.name",
        ["connectionType"] = "launchdarkly.connection_type",
        ["relayVersion"] = "launchdarkly.relay.version",
        ["sdkName"] = "launchdarkly.sdk.name",
        ["sdkVersion"] = "launchdarkly.sdk.version",
        ["sdkAppId"] = "launchdarkly.sdk.app_id",
    };

    public static IReadOnlyList<HourlyUsagePoint> Map(RawUsageSeries raw)
    {
        var groups = raw.Metadata.Select(BuildAttributes).ToList();
        var points = new List<HourlyUsagePoint>();

        foreach (var item in raw.Series)
        {
            if (!item.TryGetValue("time", out var timeValue) || ToDouble(timeValue) is not { } timeMs)
            {
                continue;
            }

            var hour = DateTimeOffset.FromUnixTimeMilliseconds((long)timeMs);
            foreach (var (key, value) in item)
            {
                if (key == "time" || !int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    continue;
                }

                if (ToDouble(value) is not { } usage)
                {
                    continue;
                }

                var attributes = index < groups.Count ? groups[index] : new Dictionary<string, string>();
                points.Add(new HourlyUsagePoint(hour, usage, attributes));
            }
        }

        return points;
    }

    private static Dictionary<string, string> BuildAttributes(IReadOnlyDictionary<string, object?> metadata)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            var text = value switch
            {
                null => null,
                string s => s,
                bool or sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal
                    => Convert.ToString(value, CultureInfo.InvariantCulture),
                _ => null,
            };

            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            attributes[KnownAttributes.TryGetValue(key, out var known) ? known : "launchdarkly.usage." + ToSnake(key)] = text;
        }

        return attributes;
    }

    private static double? ToDouble(object? value) => value switch
    {
        null => null,
        double d => d,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        IConvertible c and not string => c.ToDouble(CultureInfo.InvariantCulture),
        _ => null,
    };

    private static string ToSnake(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                sb.Append('_');
            }

            sb.Append(char.ToLowerInvariant(name[i]));
        }

        return sb.ToString();
    }
}
