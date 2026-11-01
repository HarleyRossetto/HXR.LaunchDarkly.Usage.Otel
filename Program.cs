using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using LaunchDarklyAccountTelemetry.Usage;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

var builder = WebApplication.CreateBuilder(args);

var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(serviceName: "LaunchDarklyAccountTelemetry", serviceVersion: "1.0.0"))
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(UsageTelemetry.MeterName);
        metrics.AddPrometheusExporter();
    });

// Self-metrics go to the usage OTLP backend (e.g. Grafana LGTM) when configured; traces and logs follow the
// standard OTEL_EXPORTER_OTLP_* variables (e.g. the Aspire dashboard).
var otlpConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
var metricsBackend = builder.Configuration["LaunchDarklyUsage:Otlp:Endpoint"];
if (!string.IsNullOrWhiteSpace(metricsBackend))
{
    otel.WithMetrics(metrics => metrics.AddOtlpExporter(o =>
    {
        o.Endpoint = new Uri(metricsBackend.TrimEnd('/') + "/v1/metrics");
        o.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
    }));
}
else if (otlpConfigured)
{
    otel.WithMetrics(metrics => metrics.AddOtlpExporter());
}

if (otlpConfigured)
{
    otel.WithTracing(tracing => tracing.AddOtlpExporter());
}

builder.Logging.AddOpenTelemetry(options =>
{
    options.IncludeScopes = true;
    options.IncludeFormattedMessage = true;
    if (otlpConfigured)
    {
        options.AddOtlpExporter();
    }
});

builder.Services.AddOpenApi();

// Binds the "LaunchDarklyUsage" section; keep the API key in user-secrets / environment
// (LaunchDarklyUsage__ApiKey), not in appsettings.
builder.Services.AddLaunchDarklyUsageTelemetry(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapPrometheusScrapingEndpoint();

// Protect this endpoint (e.g. .RequireAuthorization()) before exposing the host.
app.MapLaunchDarklyUsageBackfill();

app.Run();
