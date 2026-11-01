using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace LaunchDarklyAccountTelemetry.Usage;

public static class LaunchDarklyUsageExtensions
{
    internal const string LaunchDarklyClientName = "LaunchDarklyUsage";
    internal const string OtlpClientName = "LaunchDarklyUsageOtlp";

    /// <summary>Registers the usage ingestion pipeline and the scheduled background worker.</summary>
    public static IServiceCollection AddLaunchDarklyUsageTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<LaunchDarklyUsageOptions>? configure = null) =>
        services.AddLaunchDarklyUsageTelemetry(
            options =>
            {
                configuration.GetSection(LaunchDarklyUsageOptions.SectionName).Bind(options);
                configure?.Invoke(options);
            });

    public static IServiceCollection AddLaunchDarklyUsageTelemetry(
        this IServiceCollection services, Action<LaunchDarklyUsageOptions> configure)
    {
        services.AddOptions<LaunchDarklyUsageOptions>()
            .Configure(configure)
            .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "LaunchDarklyUsage:ApiKey is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Otlp.Endpoint), "LaunchDarklyUsage:Otlp:Endpoint is required.")
            .Validate(o => o.Interval > TimeSpan.Zero && o.MaxChunk >= TimeSpan.FromHours(1) && o.MaxChunk <= TimeSpan.FromDays(7),
                "Interval must be positive and MaxChunk must be between 1 hour and 7 days.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IUsageWatermarkStore, InMemoryUsageWatermarkStore>();

        services.AddTransient<LaunchDarklyRateLimitHandler>();
        services.AddHttpClient(LaunchDarklyClientName, (sp, client) =>
            {
                var o = sp.GetRequiredService<IOptions<LaunchDarklyUsageOptions>>().Value;
                client.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", o.ApiKey);
            })
            .AddHttpMessageHandler<LaunchDarklyRateLimitHandler>();

        services.AddHttpClient(OtlpClientName, (sp, client) =>
            client.Timeout = sp.GetRequiredService<IOptions<LaunchDarklyUsageOptions>>().Value.Otlp.Timeout);

        services.TryAddSingleton<IServiceConnectionUsageClient>(sp => new ServiceConnectionUsageClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(LaunchDarklyClientName)));
        services.TryAddSingleton<IUsageMetricPublisher>(sp => new OtlpHttpMetricPublisher(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(OtlpClientName),
            sp.GetRequiredService<IOptions<LaunchDarklyUsageOptions>>()));
        services.TryAddSingleton<IUsageIngestionService, UsageIngestionService>();
        services.AddHostedService<UsageIngestionWorker>();

        return services;
    }

    /// <summary>
    /// Maps POST {pattern}?from=&amp;to= (ISO-8601, UTC) to backfill a range. Apply authorization to the returned builder.
    /// </summary>
    public static RouteHandlerBuilder MapLaunchDarklyUsageBackfill(
        this IEndpointRouteBuilder endpoints, string pattern = "/launchdarkly/usage/backfill") =>
        endpoints.MapPost(pattern, async (
            DateTimeOffset from,
            DateTimeOffset? to,
            IUsageIngestionService ingestion,
            IOptions<LaunchDarklyUsageOptions> options,
            TimeProvider time,
            CancellationToken ct) =>
        {
            var end = to ?? time.GetUtcNow();
            if (end <= from)
            {
                return Results.BadRequest(new { error = "'to' must be after 'from'." });
            }

            if (end - from > options.Value.MaxBackfillRange)
            {
                return Results.BadRequest(new { error = $"Range exceeds {options.Value.MaxBackfillRange}." });
            }

            return Results.Ok(await ingestion.IngestAsync(from, end, ct));
        });
}
