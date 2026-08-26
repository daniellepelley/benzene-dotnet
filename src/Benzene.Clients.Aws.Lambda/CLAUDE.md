# Benzene.Clients.Aws.Lambda

## What this package does
Outbound AWS Lambda client for a Benzene app: invoke a Benzene Lambda function (or any Lambda), plus
a Lambda health check. Pins **only** `AWSSDK.Lambda` — it no longer depends on the SQS client (the
old `nameof(SqsClientMiddleware)` cross-reference in `AwsLambdaClientMiddleware` was a copy-paste bug,
fixed during the Tier 2.1 split).

## Key types
- `AwsLambdaBenzeneMessageClient` — `IBenzeneMessageClient`; invokes a Benzene Lambda, embedding
  request headers in its own `BenzeneMessageClientRequest` envelope.
- `AwsLambdaClient` / `IAwsLambdaClient` — lower-level invoke wrapper. Classifies the real outcome
  instead of assuming success: throws `AwsLambdaFunctionErrorException` when a `RequestResponse`
  invoke's `InvokeResponse.FunctionError` is set (AWS returns HTTP 200 even when the target function
  threw), and `AwsLambdaEventInvokeFailedException` when an `Event` (fire-and-forget) invoke's
  `InvokeResponse.StatusCode` isn't 2xx (e.g. a throttling/validation error surfaced synchronously by
  the Invoke API). Both flow through `AwsLambdaBenzeneMessageClient`'s catch as `ServiceUnavailable`.
- `AwsLambdaClientMiddleware` / `LambdaSendMessageContext` — terminal invoke middleware and context.
- `LambdaContextConverter<T>` (in `SqsContextConverter.cs` — historically misnamed file) —
  `IBenzeneClientContext<T, Void>` → invoke context, used by `UseAwsLambda()`.
- `AwsLambdaHealthCheck` — verifies a function; reports `HealthCheckDependency` (`Kind = "Lambda"`).
  Default `HealthCheckMode.Reachability` is a **non-destructive** read-only `GetFunctionConfiguration`
  call (`Type = "Lambda"`); `HealthCheckMode.Active` really invokes the function with a `ping`
  (`Type = "Lambda.Active"`, side-effecting). See `HealthCheckMode` in `Benzene.HealthChecks.Core`.
  Failures are classified via `HealthCheckError.Classify` (§3.9, reversed): an authorization/permission
  failure (403) is a **persistent `Failed`**, surfacing as unhealthy rather than being softened to a
  Warning (a deterministic misconfiguration that won't self-heal); the SDK `ErrorCode`/`StatusCode` are
  surfaced in `Data`, never the message. **No internal timeout guard** — the reachability path forwards
  the ambient `CancellationToken` straight into `GetFunctionConfigurationAsync` and relies purely on the
  processor's uniform per-check timeout wrap (`HealthCheckProcessor`/`TimeOutHealthCheck`), same shape as
  `SqsHealthCheck`. The Active-mode ping (via `AwsLambdaBenzeneMessageClient`, which has no
  `CancellationToken` overload) is the one path here that still can't forward the token into its own SDK
  call.
  - **No auto-wiring — explicit-only (by design).** Unlike SQS/SNS/EventBridge, the Lambda client is a
    **dynamic-target invoker**: `.UseAwsLambda<T>()` carries no function name (the target is supplied
    per-invocation), so there is no fixed dependency to auto-register a check for at config time. Register
    it yourself with `AddLambdaHealthCheck(name)` where you know the function. If a fixed-target Lambda
    client is ever introduced, auto-wire it there. See `work/archive/client-health-checks-remaining-designs-2026-08.md` §5.
- `LocalAwsLambdaClientFactory` — builds an `IAmazonLambda` from a local AWS profile for dev/test.
- `Extensions` — `UseAwsLambdaClient`, `UseAwsLambda<T>`, and **`AddLambdaHealthCheck`**.

## Conventions
- `AwsLambdaBenzeneMessageClient` forwards headers correctly (they go in the
  `BenzeneMessageClientRequest` envelope it invokes with).
- `LambdaContextConverter` (used by the lower-level `UseAwsLambda()` composition, not the message
  client) does **not** forward headers — a raw `InvokeRequest` has no header concept, so a decorator
  like `WithW3CTraceContext()` has no effect on a pipeline built that way.
- **No `OutboundContext` overload of `.UseAwsLambda(...)` yet** — deliberately deferred, not
  forgotten; it would follow the same `Outbound…ContextConverter` recipe as SQS/SNS when picked up.

## Dependencies
`AWSSDK.Lambda`; Benzene `Clients`, `Core.Middleware`, `HealthChecks.Core`, `Results`.
