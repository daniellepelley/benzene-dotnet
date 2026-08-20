variable "region" {
  description = "AWS region to deploy into."
  type        = string
  default     = "eu-west-1"
}

# ---------------------------------------------------------------------------------------------------
# Google OAuth login gate (Benzene.Mesh.Auth.Oidc) for the mesh Lambda's HTTP surface. See
# examples/AwsMesh/README.md's "Auth setup" section for how to create the Google OAuth Client these
# come from, and outputs.tf's mesh_oauth_callback_url for the redirect URI to register with it.
# ---------------------------------------------------------------------------------------------------

variable "google_oauth_client_id" {
  description = "The Google OAuth Client's Client ID (Google Cloud Console -> APIs & Services -> Credentials). Not sensitive - client IDs are public by design."
  type        = string
}

variable "google_oauth_client_secret" {
  description = "The Google OAuth Client's Client Secret. Sensitive - never commit a real value; set it via TF_VAR_google_oauth_client_secret or -var from a secret store (the GitHub Actions workflow sources it from the 'test' Environment's GOOGLE_OAUTH_CLIENT_SECRET Secret)."
  type        = string
  sensitive   = true
}

variable "mesh_allowed_emails" {
  description = "The Google account emails allowed to log into the mesh's HTTP surface (Mesh UI, catalog artifacts, /mesh/refresh) - case-insensitive exact match, no domain matching. An empty list locks everyone out."
  type        = list(string)
  default     = ["daniellepelley@gmail.com"]
}

variable "project" {
  description = "Name prefix for all resources."
  type        = string
  default     = "benzene-mesh"
}

variable "artifact_bucket_name" {
  description = "S3 bucket for the mesh registry + catalog artifacts. Must be globally unique; defaults to <project>-<account-id>."
  type        = string
  default     = ""
}

variable "claim_check_bucket_name" {
  description = "S3 bucket Benzene.ClaimCheck.Aws.S3 offloads/hydrates oversized payments:capture payloads to/from (work/archive/claim-check-plan-2026-08.md Phase 6). DEDICATED — kept separate from artifact_bucket_name: the mesh catalog is a durable registry read by the mesh's IAM role/audience, claim-checked payloads are expiring transients read by orders-api/payments-api's own role, and the two should never share a bucket policy. Must be globally unique; defaults to <project>-claim-checks-<account-id>."
  type        = string
  default     = ""
}

variable "discovery_tag_key" {
  description = "The resource tag key discovery filters on. Services carry this tag; the mesh Lambda does not (so it never discovers itself)."
  type        = string
  default     = "benzene"
}

variable "lambda_architecture" {
  description = "Lambda architecture. Must match the self-contained publish RID in CI (x86_64 -> linux-x64, arm64 -> linux-arm64)."
  type        = string
  default     = "x86_64"
}

variable "trace_sample_rate" {
  description = "Fraction of traces to record (0..1), passed to the apps as the standard OTEL_TRACES_SAMPLER_ARG and applied with a parent-based sampler so a whole transaction is sampled or dropped together. Defaults to 0.2 to keep a standing demo inside the X-Ray free tier: X-Ray free-tiers 100k traces recorded AND 1,000,000 traces retrieved/scanned per month (the Global-XRay-TracesAccessed line), and the Mesh UI's live queries scan every trace in the picked window — so sampling at the source cuts both dimensions at once. Set to 1 to record every trace (the pre-2026-07-25 behavior); lower it further for a long-lived demo."
  type        = number
  default     = 0.2

  validation {
    condition     = var.trace_sample_rate > 0 && var.trace_sample_rate <= 1
    error_message = "trace_sample_rate must be greater than 0 and at most 1 (0 would record nothing, leaving the mesh blind)."
  }
}

variable "aggregate_schedule" {
  description = "EventBridge schedule expression for the mesh aggregation pass. Defaults to every 15 minutes to keep a standing demo cheap: each pass invokes the mesh Lambda AND fans out spec + healthcheck HTTP calls to every discovered service, all of it X-Ray-traced and EMF-metered — at rate(1 minute) that's roughly 20k Lambda invocations and 35k X-Ray traces per day sitting idle (observed ~1.5k traces/hour on the demo estate). Lower it (e.g. rate(1 minute), matching the AzureFunctionsMesh timer) when you want near-live catalog freshness. Note: the Mesh UI explorer loads artifacts once per page load, so a browser reload still shows the latest — this only bounds how stale that reload can be; the live traffic plane (X-Ray/CloudWatch) is queried directly by the UI and is unaffected by this schedule."
  type        = string
  default     = "rate(15 minutes)"
}

variable "orders_outbox_sweep_schedule" {
  description = "EventBridge schedule expression for orders-api's outbox sweep (work/archive/outbox-plan-2026-08.md §2.5) — the backstop that retries/parks/cleans up whatever the DynamoDB-Streams dispatch path (near-real-time) missed. Defaults to every 5 minutes: frequent enough that a parked envelope is discovered promptly in a demo, infrequent enough to stay cheap alongside the mesh's own aggregate schedule."
  type        = string
  default     = "rate(5 minutes)"
}

variable "adot_collector_layer_arn" {
  description = <<-EOT
    ARN of the AWS Distro for OpenTelemetry (ADOT) collector Lambda layer. When set, it is attached to
    every function and Benzene's OTLP exporter is pointed at the layer's in-process collector
    (http://localhost:4317) automatically — the collector's default config forwards OTLP traces to X-Ray
    (awsxray), so the per-middleware spans land as subsegments in the same X-Ray trace as the AWS-level
    segments. No AWS_LAMBDA_EXEC_WRAPPER is set — these are custom-runtime functions that already emit
    their own spans, so only the collector half of the layer is used. Set empty ("") to not attach it.

    The default is the amd64 collector layer in eu-west-1, matching this config's default region + x86_64
    architecture. A Lambda layer is regional and arch-specific, so if you change var.region or
    var.lambda_architecture you MUST change this ARN to match (swap the region substring, and
    amd64<->arm64). Bump the ver-X-Y-Z from https://github.com/aws-observability/aws-otel-lambda/releases.
  EOT
  type        = string
  default     = "arn:aws:lambda:eu-west-1:901920570463:layer:aws-otel-collector-amd64-ver-0-117-0:1"
}

variable "otlp_endpoint" {
  description = <<-EOT
    Explicit override for OTEL_EXPORTER_OTLP_ENDPOINT on every function. Usually leave empty and set
    adot_collector_layer_arn instead (which points the exporter at the layer's localhost collector).
    Set this only to target an out-of-process / external collector. Empty AND no ADOT layer = spans are
    recorded but exported nowhere.
  EOT
  type        = string
  default     = ""
}

# Paths to the built Lambda zip files (each contains a `bootstrap` executable). Produced by CI
# (dotnet publish self-contained -> add bootstrap -> zip). Defaults assume the CI layout.
variable "orders_zip" {
  type    = string
  default = "../artifacts/orders.zip"
}
variable "payments_zip" {
  type    = string
  default = "../artifacts/payments.zip"
}
variable "shipping_zip" {
  type    = string
  default = "../artifacts/shipping.zip"
}
variable "inventory_zip" {
  type    = string
  default = "../artifacts/inventory.zip"
}
variable "notifications_zip" {
  type    = string
  default = "../artifacts/notifications.zip"
}
variable "analytics_zip" {
  type    = string
  default = "../artifacts/analytics.zip"
}
variable "mesh_zip" {
  type    = string
  default = "../artifacts/mesh.zip"
}

# ---------------------------------------------------------------------------------------------------
# Abuse / cost protection for the mesh's HTTP API. Two independent layers, deliberately: the API
# Gateway limits below cap request RATE at the edge, before a Lambda invocation is billed at all; the
# app-level throttle (refresh_min_interval_seconds) caps how often the expensive discovery+aggregation
# pass actually RUNS, which it can only do once you are already paying for the invoke. Neither
# substitutes for the other.
# ---------------------------------------------------------------------------------------------------

variable "refresh_min_interval_seconds" {
  description = "Minimum gap between two mesh discovery/aggregation passes triggered via POST /mesh/refresh. A request inside the window is answered 429 without running the pass (Benzene.Mesh.Artifacts' UseMeshRefreshGuard, wired in Mesh/Startup.cs, which compares now against the last pass's generatedAtUtc in manifest.json). Bounds sustained abuse — a held-down Refresh button, a stuck retry loop, a stolen session cookie — to roughly one pass per window; it is a rate limiter, NOT a distributed lock, so two simultaneous requests can still both proceed. 0 disables it. The default of 30s is long enough to stop hammering and short enough that a human who just changed something isn't made to wait."
  type        = number
  default     = 30

  validation {
    condition     = var.refresh_min_interval_seconds >= 0
    error_message = "refresh_min_interval_seconds must be zero (throttle off) or positive."
  }
}

variable "mesh_api_throttling_rate_limit" {
  description = "Steady-state requests/second API Gateway allows across the whole mesh HTTP API before returning 429 itself. This is the layer that actually protects the bill: it refuses excess requests AT THE EDGE, so they never become billed Lambda invocations, X-Ray traces, or S3 reads — unlike the app-level refresh throttle, which can only decline work after the invoke has already been paid for. The default of 10 rps is generous for a mesh UI driven by one or two operators (page load, then a 15s fleet poll) while capping a runaway loop or a scripted flood at a knowable ceiling. Raise it if you genuinely have many concurrent viewers."
  type        = number
  default     = 10
}

variable "mesh_api_throttling_burst_limit" {
  description = "Maximum concurrent/bucket-burst requests API Gateway allows on the mesh HTTP API. A page load legitimately fires several requests at once (the page, manifest.json, topics.json, the first fleet poll), so the burst must comfortably exceed the steady rate or normal use trips it. The default of 20 covers that with headroom while still bounding a flood."
  type        = number
  default     = 20
}

variable "mesh_environment" {
  description = "The environment label the mesh Lambda runs as (DOTNET_ENVIRONMENT), and the one thing that decides whether dispatch works at all. MeshDispatchGate treats an UNSET environment as Production and refuses to dispatch, which is the safe default and is why this must be set explicitly for a demo estate. Leave it non-production unless you have read work/mesh-environments-and-access.md: opening dispatch in production needs roles on the session and a way to tell a read-shaped topic from a write-shaped one, neither of which exists yet — and the mesh will refuse regardless until AllowInProduction is deliberately set in code."
  type        = string
  default     = "Development"
}

variable "mesh_dispatch_throttling_rate_limit" {
  description = "Steady-state requests/second API Gateway allows on the dispatch route specifically, before returning 429 at the edge. Much tighter than the API-wide limit because this is the one route that fires a payload into a real service handler, and because nobody legitimately sustains more than a couple of sends a second from a test console. This is the HARD guarantee: the app-level per-identity limiter in the mesh counts in memory, so on a multi-instance Lambda host it bounds one warm instance rather than the fleet — only the edge counts atomically across all of them."
  type        = number
  default     = 2
}

variable "mesh_dispatch_throttling_burst_limit" {
  description = "Maximum burst API Gateway allows on the dispatch route. A send is one request, so this needs far less headroom than the API-wide burst (which has to absorb a page load firing several artifacts at once)."
  type        = number
  default     = 5
}

variable "mesh_dispatch_max_per_minute" {
  description = "App-level dispatches per minute per signed-in identity (MeshDispatchGuardOptions.MaxPerMinutePerIdentity). Bounds a stuck retry loop or one compromised session; 0 disables it, which is an explicit operator choice and not a default."
  type        = number
  default     = 10
}

variable "mesh_dispatch_max_per_target_per_minute" {
  description = "App-level dispatches per minute aimed at any ONE target service, summed across every identity. Ten people each dispatching politely still add up at the service, and the service is what this protects."
  type        = number
  default     = 30
}

variable "usage_window_hours" {
  description = "Lookback window (hours) the mesh's CloudWatch usage source counts topic requests over, and the window the Mesh UI shows. Coarse usage only — fine-grained analysis belongs in CloudWatch/Grafana."
  type        = number
  default     = 24
}
