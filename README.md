# LaunchDarkly account usage → OpenTelemetry

Ingests hourly **server-side service connection minutes** from the LaunchDarkly
[service connections usage API](https://launchdarkly.com/docs/api/account-usage-beta/get-service-connections-usage)
and publishes them as OTLP metrics, per project and environment.

The pipeline lives in `LaunchDarklyAccountTelemetry.Usage` and plugs into any ASP.NET Core host:

```csharp
builder.Services.AddLaunchDarklyUsageTelemetry(builder.Configuration);   // binds "LaunchDarklyUsage"
app.MapLaunchDarklyUsageBackfill().RequireAuthorization();                // optional backfill endpoint
```

## Configuration (`LaunchDarklyUsage`)

| Key | Default | Notes |
|---|---|---|
| `ApiKey` | – | Required. Use user-secrets / `LaunchDarklyUsage__ApiKey`. |
| `Otlp:Endpoint` | – | Required. OTLP/HTTP base, e.g. `http://localhost:4318` (`/v1/metrics` appended). |
| `Otlp:Headers` | – | e.g. auth headers for your backend. |
| `Interval` | 15 min | Worker schedule. |
| `PublicationDelay` | 20 min | Hour H is ingested only after H + 1h + delay (LD data lands ~15 min past the hour). |
| `InitialLookbackHours` | 24 | Used when no watermark is known. |
| `RescrapeHours` | 2 | Overlap re-scraped each run to pick up late corrections. |
| `ExtraGroupBy` | none | Adds `connectionType`, `sdkName`, `sdkVersion`, `sdkAppId`, `relayVersion` (raises cardinality). |
| `StartupBackfillFrom` | – | Backfill from this instant on startup. |
| `MaxChunk` / `MaxBackfillRange` | 6 d / 90 d | LD limits hourly requests to 7 days. |
| `MaxRateLimitRetries` / `MaxRateLimitWait` | 5 / 30 s | 429s wait until `X-Ratelimit-Reset` (epoch ms), else exponential backoff. |

## Behaviour

- Metric `launchdarkly.service_connections.minutes` (gauge, unit `min`), timestamped at the **start of each usage hour**, so re-publishing an hour is idempotent.
- The .NET Metrics SDK can't emit historical timestamps, so metrics are pushed directly as OTLP/HTTP JSON.
- State is optional: `IUsageWatermarkStore` (in-memory by default) tracks the last published hour. After a restart the lookback window is re-scraped. Register your own store to persist it.
- Failed runs don't advance the watermark; the next tick retries.
- Backfill: `POST /launchdarkly/usage/backfill?from=2026-10-01T00:00:00Z&to=...` (`to` optional, hours capped at the latest complete hour).
- Self-metrics under meter `LaunchDarklyAccountTelemetry.Usage` (add with `AddMeter(UsageTelemetry.MeterName)`).

## Caveats

- Your backend must accept out-of-order/old samples for backfill (e.g. Prometheus/Mimir `out_of_order_time_window`).
- The endpoint is beta (the `LD-API-Version: beta` header is sent automatically). Hours with no usage produce no point.
- Series metadata supplies `environmentId/Key/Name` and `projectId/Name` (mapped to `launchdarkly.environment.*` / `launchdarkly.project.*`); the response has no project key. Other scalar metadata becomes `launchdarkly.usage.*` attributes.

## Local stack (Aspire + Grafana)

`LaunchDarklyAccountTelemetry.AppHost` runs the host together with `grafana/otel-lgtm` (Grafana, Prometheus, OTLP receiver). The host pushes usage metrics straight to the container's OTLP/HTTP endpoint; a "LaunchDarkly service connections" dashboard is provisioned from `deploy/grafana/`.

```bash
cd LaunchDarklyAccountTelemetry.AppHost
dotnet user-secrets set "Parameters:ld-api-token" "<LaunchDarkly API token>"
aspire run                      # dev loop; Grafana at http://localhost:3000 (admin/admin)
aspire publish -o ../aspire-output   # generates docker-compose.yaml + .env (fill in LD_API_TOKEN)
aspire deploy                   # publish + build images + docker compose up (aspire destroy to tear down)
```

Trigger a backfill: `curl -X POST "http://localhost:5248/launchdarkly/usage/backfill?from=2026-10-25T00:00:00Z"` (use the `usage` URL shown by `aspire describe`).

Notes:
- `deploy/lgtm/prometheus.yaml` overrides the image's Prometheus config with `out_of_order_time_window: 7d` so backfilled hours are accepted (older than 7 days will be rejected; a larger window costs Prometheus memory). Increase it if you backfill further.
- Compose output references bind mounts via `.env` variables (`LGTM_BINDMOUNT_*`) and the token via `LD_API_TOKEN`; `aspire-output/` and `.env*` are git-ignored.
- `aspire deploy` image builds depend on the .NET 11 preview container base images being available; `aspire run` and `aspire publish` were verified.
- Host self-metrics go to LGTM (`LaunchDarklyUsage:Otlp:Endpoint`); traces/logs follow `OTEL_EXPORTER_OTLP_*` (the Aspire / compose dashboard).
