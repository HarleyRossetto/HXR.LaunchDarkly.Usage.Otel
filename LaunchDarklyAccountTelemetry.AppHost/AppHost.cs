var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("compose");

var ldApiToken = builder.AddParameter("ld-api-token", secret: true);

var deploy = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "deploy"));

// All-in-one Grafana + Prometheus + Tempo + Loki + OTLP receiver. The Prometheus config is overridden
// so that backfilled (old) hourly samples are accepted.
var lgtm = builder.AddContainer("lgtm", "grafana/otel-lgtm")
    .WithHttpEndpoint(port: 3000, targetPort: 3000, name: "grafana", isProxied: false)
    .WithHttpEndpoint(port: 4318, targetPort: 4318, name: "otlp-http", isProxied: false)
    .WithExternalHttpEndpoints()
    .WithBindMount(Path.Combine(deploy, "lgtm", "prometheus.yaml"), "/otel-lgtm/prometheus.yaml", isReadOnly: true)
    .WithBindMount(Path.Combine(deploy, "grafana", "dashboards.yaml"), "/otel-lgtm/grafana/conf/provisioning/dashboards/launchdarkly.yaml", isReadOnly: true)
    .WithBindMount(Path.Combine(deploy, "grafana", "dashboards"), "/otel-lgtm/launchdarkly-dashboards", isReadOnly: true)
    .WithVolume("lgtm-data", "/data");

builder.AddProject<Projects.LaunchDarklyAccountTelemetry>("usage")
    .WithEnvironment("LaunchDarklyUsage__ApiKey", ldApiToken)
    .WithEnvironment("LaunchDarklyUsage__Otlp__Endpoint", lgtm.GetEndpoint("otlp-http"))
    .WaitFor(lgtm);

builder.Build().Run();
