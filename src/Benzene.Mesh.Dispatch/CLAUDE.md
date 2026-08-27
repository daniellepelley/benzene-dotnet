# Benzene.Mesh.Dispatch

## What this package does
The **opt-in, production-gated live dispatch** capability for the mesh — F3b-revised **case (1)**, the
direct-to-consumer path. It serves a `mesh:dispatch` handler that invokes **ONE** registered service's
**real** handler with a caller-supplied payload and returns the response, so the mesh UI's payload
composer can *send* (not just copy) a test message to a service.

This is the write-side counterpart of `Benzene.Mesh.Aggregator`'s read-only `IMeshServiceSource`
(which only fetches spec/health). It reuses the **same access the aggregator already has** to reach a
service — HTTP POST, or an AWS Lambda `Invoke` — changing the *payload*, not the *permission*, and is
bounded to a single declared service (never a shared queue). That is why it clears §10.7's bar the
rejected queue-injection version didn't; see `work/mesh-ui-product-vision.md` F3b-revised.

## Why it's gated (this is the load-bearing part)
Dispatch fires a **real handler with real side-effects** (DB writes, downstream calls, the handler's
own publishes). So it is off by two independent gates:
1. **Opt-in registration** — nothing is exposed unless `UseMeshDispatch()` is called. The handler
   carries **no `[Message]` attribute**, so a plain `.UseMessageHandlers()` assembly-scan does **not**
   auto-discover it (unlike `mesh:report`); only the explicit `UseMeshDispatch()` registration routes it.
2. **Runtime environment gate** — `MeshDispatchGate` refuses dispatch in a **Production** environment
   unless `MeshDispatchOptions.AllowInProduction` is set. An **unset** `ASPNETCORE_ENVIRONMENT` /
   `DOTNET_ENVIRONMENT` counts as Production (the safe default), so dispatch is off unless the host is
   explicitly a non-production environment (or the override is set). A blocked dispatch returns
   `Forbidden` with the reason — it never silently runs.

## Key types
- `MeshDispatchOptions { bool AllowInProduction }` — the second opt-in (default false).
- `IMeshDispatchEnvironment` / `EnvironmentVariableMeshDispatchEnvironment` — "is this Production?",
  defaulting to the env-var reading above (overridable in DI, e.g. for tests).
- `MeshDispatchGate` — `IsAllowed` (= non-Production **or** AllowInProduction) + `BlockedReason`.
- `IMeshServiceDispatcher { string Key; DispatchAsync(entry, envelope, ct) }` — reach ONE service over
  one transport, keyed by `MeshServiceRegistryEntry.Source` (the same keying `IMeshServiceSource` uses).
  - `HttpMeshServiceDispatcher` (`Key = "Http"`) — POSTs the `{ topic, headers, body }` envelope to the
    service's invoke URL (`SourceOptions["invokeUrl"]`, else `<specUrl origin>/benzene-message`).
  - `AwsLambdaMeshServiceDispatcher` (`Key = "AwsLambdaInvoke"`) lives in **`Benzene.Mesh.Aws.Lambda`**
    (`AddMeshLambdaDispatcher()`), reusing that package's `IAwsLambdaClient` / `lambda:InvokeFunction`
    grant — the AWS SDK stays out of this core package.
- `MeshDispatchRequest { Service, Topic, Headers?, Body? }` — the `mesh:dispatch` body.
- `MeshDispatchMessageHandler` — gate → **resolve the target from the registry before charging the
  per-target rate limit** (#187a) → charge the limit → pick the dispatcher by `entry.Source` → dispatch
  → return the service's `{ statusCode, headers, body }`. Distinct statuses per failure: `Forbidden`
  (gated off), `BadRequest` (no service/topic), `NotFound` (unknown service — costs the limiter
  nothing), `TooManyRequests` (per-target limit), `NotImplemented` (no dispatcher for that source).
  - **Cancellation (#185):** the dispatch call passes the ambient token from an optional
    `ICancellationTokenAccessor` constructor parameter — the same idiom `HttpBenzeneMessageClient`
    uses (`src/Benzene.Clients.Http`) — falling back to `CancellationToken.None` only when nothing is
    registered/seeded. Wrap the pipeline in `.UseTimeout(...)` (`Benzene.Resilience`) to bound a stuck
    dispatch; without it, dispatch behaves exactly as before (no accessor resolved → no cancellation).
  - **Audit on throw (#186):** the dispatch call is wrapped in try/catch. On exception, `Audit(
    "dispatch-failed", …, exceptionType: ex.GetType().Name)` runs and the exception is **rethrown
    unchanged** — the audit record is the fix, propagation semantics do not change. Every other exit
    path already audited before this fix; this closes the one path that didn't, so the package's
    "leaves a record" claim holds even when the dispatch itself throws (see
    `MeshDispatchMessageHandlerTest.DispatchThrows_AuditsDispatchFailedWithExceptionType_ThenRethrows`).
  - **Rate-limit ordering (#187a):** the not-found check runs before `MeshDispatchRateLimiter.TryAcquire`
    (it used to run after), so a caller cannot pin a rate-limit window against a service name that was
    never registered.
- `MeshDispatchRateLimiter` — besides `TryAcquire`/`Prune()`, `TryAcquire` now **self-prunes
  opportunistically** (#187b) once `_windows.Count` exceeds a small threshold (512), so a shared
  singleton stays bounded even in a configuration with no guard middleware calling `Prune()` on its
  own schedule (only `Benzene.Mesh.Artifacts`'s guard middleware calls it directly today).
- `HttpMeshServiceDispatcher` also caps the target's response (`MaxResponseBytes`, noted gap promoted
  into WP-1): defaults to `MeshDispatchGuardOptions.DefaultMaxRequestBytes` (the same bound the
  request side has always had), enforced while reading the response stream. An oversized response is
  **truncated with an audit-visible `TruncatedMarker` appended to the body, not thrown** — the target
  DID respond, and that response (truncated) is still the record of what happened.
- `Extensions.UseMeshDispatch<TContext>(options?)` — opt-in registration (registers the handler on
  `mesh:dispatch`, the options/gate, and the HTTP dispatcher). Requires a `MeshServiceRegistry` in DI
  (the dispatchable set) and, for AWS-Lambda services, `AddMeshLambdaDispatcher()`. `ICancellationTokenAccessor`
  is resolved from DI like the handler's other optional collaborators — nothing extra to wire for #185.

## When to use
Only when you deliberately want the mesh to *send* live test messages to services (a dev/staging
convenience). Wire it (gated) into a mesh host — see `deploy/Mesh/Benzene.Mesh.Host` (`dispatch.enabled`
/ `dispatch.allowInProduction` config, off by default). For copy-only payloads, or queue/stream
transports (which stay compose+copy only), you don't need this package at all — that's `UseTestPayloads()`
+ the mesh UI's F3a composer.

## Dependencies
- **Benzene.Mesh.Contracts** — `MeshServiceRegistry`/`MeshServiceRegistryEntry`/`MeshServiceSource`.
- **Benzene.Abstractions.MessageHandlers** / **Benzene.Core.MessageHandlers** — the handler + its
  registration; `RawStringMessage`/`BenzeneResult` transitively. No AWS dependency (the AWS dispatcher
  is in `Benzene.Mesh.Aws.Lambda`).

## Tests
`test/Benzene.Mesh.Test/MeshDispatchTest.cs` — the gate truth table, the handler's gate/not-found/
bad-request/no-dispatcher/happy paths (with a recording fake dispatcher, asserting a blocked dispatch
never reaches the dispatcher), and the AWS dispatcher's invoke mapping (mocked `IAwsLambdaClient`).
Also, one test class each for the four round-12/13 (WP-1) behaviours:
- `MeshDispatchMessageHandlerTest.UnknownService_RepeatedCalls_NeverChargeTheRateLimiter` (#187a).
- `MeshDispatchMessageHandlerTest.DispatchThrows_AuditsDispatchFailedWithExceptionType_ThenRethrows`
  (#186) — the test that makes the package's "a scoped, attributable call that leaves a record" claim
  provably true under a failing dispatcher, not just an assertion in a comment: a thrown dispatch is
  asserted to both audit AND still propagate the same exception instance.
- `MeshDispatchMessageHandlerTest.WrappedInUseTimeout_PassesTheAmbientCancellationToken_NotHardcodedNone`
  (#185) — wraps the handler in a real `Benzene.Resilience.TimeoutMiddleware<TContext>` (the type
  `.UseTimeout(...)` wires up) and asserts the dispatcher's received token is the wrapped/cancellable
  one, not `CancellationToken.None`.
- `MeshDispatchRateLimiterTest.TryAcquire_SelfPrunesPastThreshold_KeepsTheWindowMapBounded` (#187b) —
  pushes the limiter's internal map past the self-prune threshold and asserts it collapses back down
  on the next `TryAcquire`, with nothing calling `Prune()` directly.
- `HttpMeshServiceDispatcherTest` (response cap noted gap) — within-cap passthrough, over-cap
  truncation + marker, and the default matching `MeshDispatchGuardOptions.DefaultMaxRequestBytes`.

## Follow-ups (not in this package yet)
- The mesh UI **send leg**: wiring the F3a composer's existing envelope + a "Send" button to POST
  `mesh:dispatch`, feature-detected like the annotations/fleet endpoints and compose-toggle gated.
- Discovery-driven meshes (e.g. `examples/AwsMesh`, whose registry is replaced at runtime and persisted
  to S3) need the live registry surfaced to the handler; the static-config `Benzene.Mesh.Host` is the
  wired first target.
