# Round 17 — gRPC + HealthChecks deep pass (2026-08)

Scope: `Benzene.Grpc` / `Benzene.Grpc.AspNet` / `Benzene.Grpc.Client`, `Benzene.HealthChecks` /
`Benzene.HealthChecks.Core` / `Benzene.HealthChecks.Http` / `Benzene.HealthChecks.EntityFramework`, W3C
trace-context nested-span linkage, and (time-permitting) the mesh UI client JS. Read-only investigation
against `main` @ `4389bfb` — no source changes, no commits. Every finding below was proven (or
disproven) with a scoped `dotnet test` run against a real host/pipeline, not by inspection alone; all
throwaway test files were reverted (`git checkout --`) after use and the repo is clean.

Round 16's mesh tracing/dispatch/collector pass (#252-256) is not re-litigated here. Rounds 1/8/9/10's
gRPC/health fixes (#8, #23, #48, #54, #109, #110-114) are treated as settled baselines to build on, not
re-opened.

## Summary of findings

| # | Area | Severity | Status |
|---|------|----------|--------|
| 1 | `GrpcMethodHandler.ServerStreamingAsync`/`DuplexStreamingAsync` — handler exception mid-stream | **Bug** | Proven with a red test |
| 2 | `BenzeneHealthCheckBridge` + `HealthCheckProcessor` — `IsNonCritical` honoured in one aggregation path but not the other | **Bug** | Proven with a red test |
| 3 | `BenzeneHealthCheckBridge.CheckHealthAsync` — an underlying check that throws | No bug | Proven correct with a green test (ASP.NET Core's own `HealthCheckService` catches it) |
| 4 | `HealthCheckProcessor.RunTimedAsync`'s "no ambient `CancellationToken`" caveat | Documentation gap, not a bug | Confirmed undiscoverable outside the private implementation |
| 5 | W3C trace context — fire-and-forget background span linkage | No bug | Proven correct with a green test |
| 6 | Mesh UI client JS | Not reviewable in this checkout | Only the vendored minified bundle is present, no source |

---

## 1. BUG — a handler exception thrown mid-stream (server-streaming or bidirectional) bypasses every
   layer of Benzene's error classification, and leaves a **misleading** `benzene-status: ok` trailer

### Where
- `src/Benzene.Grpc/GrpcMethodHandler.cs` — `ServerStreamingAsync` (lines 48-59) and
  `DuplexStreamingAsync` (lines 81-93).
- `src/Benzene.Grpc/Streaming/GrpcStreamAdapter.cs` — `WriteAll` (lines 28-34).
- Contrast with `src/Benzene.Core.MessageHandlers/MessageHandler.cs` (lines 87-135), which is what
  gives a *unary* (or client-streaming) handler's thrown exception its classification (`ArgumentException`
  → `ValidationError`, `TimeoutException` → `Timeout`, anything else → `ServiceUnavailable`), which then
  flows into `GrpcMethodHandler.RunPipelineAsync`'s `DefaultGrpcStatusCodeMapper` mapping + the
  `benzene-status` trailer + `AddRichErrorDetails`.

### The gap
For a server-streaming or duplex handler, `IMessageHandler<TRequest, IAsyncEnumerable<TResponse>>.HandleAsync`
typically returns an **async-iterator method** (`yield return ...`) synchronously, without doing any of
the actual work yet — C#'s `IAsyncEnumerable` semantics mean the method body doesn't run until something
enumerates it. `MessageHandler<TRequest,TResponse>.HandleAsync`'s try/catch (the ONE place that
classifies handler exceptions) only wraps the initial call that hands back this not-yet-iterated
enumerable — it has already returned successfully by the time anything inside the iterator body runs.

The iterator is actually drained later, by `GrpcMethodHandler.ServerStreamingAsync`/`DuplexStreamingAsync`
calling `GrpcStreamAdapter.WriteAll`, which happens **after** `RunPipelineAsync` has already:
1. Run the pipeline to completion (successfully, since nothing has thrown yet),
2. Written `grpcContext.ResponseTrailers.Add("benzene-status", status ?? "Unknown")` with the *success*
   status,
3. Mapped that status to `StatusCode.OK` and returned normally from `RunPipelineAsync`.

`WriteAll` (and the `ResolveResponseStream`/`WriteAll` call sequence around it) has **no try/catch of its
own**. So when the handler's iterator throws partway through producing items:
- The exception is not routed through `MessageHandler`'s classification (no `ValidationError`/`Timeout`/
  `ServiceUnavailable` translation).
- It is not routed through `GrpcMethodHandler.RunPipelineAsync`'s `DefaultGrpcStatusCodeMapper` mapping,
  `AddRichErrorDetails`, or the "OperationCanceledException → Cancelled/DeadlineExceeded" translation.
- It propagates straight out of `BenzeneInterceptor.ServerStreamingServerHandler`/`DuplexStreamingServerHandler`
  (which also has no exception handling), to grpc-dotnet's own generic fallback:
  **`StatusCode.Unknown`, Detail = `"Exception was thrown by handler."`**
- Worse: the `benzene-status` trailer set in step 2 above (`ok`) is **already attached to the call** and
  stays there — it was set before the exception happened. A client reading the trailer for business-status
  classification (the mechanism `AddRichErrorDetails`/the trailer itself exists for) sees `benzene-status:
  ok` on a call that actually failed with `RpcException`. That's not just "opaque," it's actively
  contradictory.

### Proof (reverted after use; not committed)
Added `SubscribeThrowingMidStreamMessageHandler`/`ChatThrowingMidStreamMessageHandler` test handlers
(same shape as the existing `SubscribeMessageHandler`/`ChatMessageHandler` used by
`GrpcStreamingHostingTest`, but yielding one item then throwing `InvalidOperationException` on the
second) and drove them through the real `TestServer` + generated `GrpcChannel` client (the same technique
`GrpcNullResponseHostingTest`/`GrpcStreamingHostingTest` already use). Both the server-streaming
(`Subscribe`) and bidirectional (`Chat`) shapes reproduce:

```
DIAG StatusCode=Unknown Detail='Exception was thrown by handler.' trailers=[benzene-status=ok]
```

i.e. the client received the first item successfully, then an opaque `RpcException(Unknown)` on the
second `MoveNext()`, with the call's own `benzene-status` trailer still claiming `ok`.

### Why this clears the bar
This is exactly the "same treatment" question the assignment asked to check: unary and client-streaming
handler exceptions get Benzene's full classification pipeline (round 5-6 WP-4, round 7-10 WP-I); a
mid-stream exception in the other two RPC shapes gets none of it, plus an actively misleading trailer.
It's also a silent-data-corruption-adjacent issue for observability specifically (this reviewer's remit):
any consumer built around the `benzene-status` trailer (dashboards, the mesh's trace-backed status
reconstruction, alerting) would read a failed call as a success.

### Note on scope
Round 5-6's WP-4 ruling explicitly and correctly left server-streaming/duplex's **null-stream** case
alone (a stream response that never materializes is a wiring error, correctly reported via
`ResolveResponseStream`'s controlled `RpcException(Internal, ...)` — not a bug). This finding is a
different case: a stream that **starts** materializing correctly, then the handler throws partway
through. That path was never exercised by the WP-4/WP-I fixes or their regression tests (`GrpcNullResponseHostingTest`,
`GrpcMethodHandlerStreamingTest`, `GrpcStreamingHostingTest` all only cover the happy path and the
"no handler registered"/"cancelled" cases for streaming).

### Suggested fix shape (not implemented — read-only review)
Wrap the enumeration in `GrpcStreamAdapter.WriteAll` (or the call sites in `GrpcMethodHandler`) in a
try/catch that mirrors `MessageHandler`'s classification (or at minimum reruns the
`DefaultGrpcStatusCodeMapper`/`AddRichErrorDetails`/trailer logic against the caught exception), and -
critically - either don't write the `benzene-status: ok` trailer until the stream is fully drained, or
overwrite it once a mid-stream failure is known. Given gRPC trailers can only be sent once (at the end of
the call), the write of `benzene-status` in `RunPipelineAsync` needs to move to after stream draining for
the two streaming shapes, not before.

---

## 2. BUG — `BenzeneHealthCheckBridge` ignores `IHealthCheck.IsNonCritical`; `HealthCheckProcessor` honours it

### Where
- `src/Benzene.Grpc.AspNet/BenzeneHealthCheckBridge.cs`, `CheckHealthAsync` (lines 61-96).
- `src/Benzene.HealthChecks/HealthCheckProcessor.cs`, `RunTimedAsync` (lines 66-111), specifically the
  downgrade at lines 100-104.
- `src/Benzene.HealthChecks.Core/IHealthCheck.cs` (`IsNonCritical` default interface member, line 53) and
  `src/Benzene.HealthChecks.Core/DependencyHealthCheck.cs` (forces `IsNonCritical => true` for every
  auto-wired dependency check, line 52 - "a non-critical dependency being down degrades the instance
  rather than taking it out of service", §3.4).

### The gap
`Benzene.HealthChecks`' own aggregation path (`HealthCheckProcessor.PerformHealthChecksAsync`, used by
the HTTP/message-handler `UseHealthCheck`/`UseLivenessCheck`/`UseReadinessCheck` topics) has a documented,
deliberate contract: a check marked `IsNonCritical == true` that reports `Failed` is downgraded to
`Warning` before the aggregate health decision is made, specifically so a non-critical dependency being
down never flips the probe unhealthy (`HealthCheckProcessor`'s class doc, lines 16-20).

`BenzeneHealthCheckBridge` (`Benzene.Grpc.AspNet`, the grpc.health.v1 bridge) is a **second, independent**
aggregation path over the exact same `Benzene.HealthChecks.Core.IHealthCheck` contract - it bridges the
raw registered checks directly (`sp.GetServices<IBenzeneHealthCheck>()`, no `HealthCheckProcessor`
involved at all, by this package's own deliberate design not to reference the full `Benzene.HealthChecks`
package - see its `CLAUDE.md`). Its aggregation logic:

```csharp
if (results.Any(x => x.Status == BenzeneHealthCheckStatus.Failed))
{
    return HealthCheckResult.Unhealthy(...);
}
```

reads `result.Status` straight off `ExecuteAsync`'s raw output, with **no reference to
`healthCheck.IsNonCritical` anywhere in the class**. A check that is `IsNonCritical == true` and reports
`Failed` flips the gRPC health bridge's aggregate to `Unhealthy` (→ grpc.health.v1 `NOT_SERVING`), even
though the identical check/state, probed through the HTTP/message-handler path, correctly stays healthy
(`Warning`, not `Failed`).

### Proof (reverted after use; not committed)
Added a `NonCriticalFailingCheck : IHealthCheck` (`IsNonCritical => true`, always returns `Failed`) and
ran it through both aggregation paths:
- `HealthCheckProcessor.PerformHealthChecksAsync(new[] { check })` → `result.IsSuccessful == true` (passes).
- `new BenzeneHealthCheckBridge(new[] { check }).CheckHealthAsync(...)` → `HealthStatus.Unhealthy` (fails
  the assertion that it should not report Unhealthy).

```
Assert.NotEqual() Failure
Expected: Not Unhealthy
Actual:   Unhealthy
```

### Why this clears the bar
This is a genuine spec-contract violation across two probes of the same underlying state: the same
health-check configuration answers "serving" over HTTP/message-handler probes and "NOT_SERVING" over
grpc.health.v1, for a condition Benzene's own `IsNonCritical` contract calls non-blocking. Any service
that (a) marks a dependency check non-critical (or relies on the auto-wired `DependencyHealthCheck`
category, which is *always* non-critical) and (b) exposes both an HTTP health endpoint and the gRPC health
bridge would see Kubernetes (or any grpc.health.v1-aware load balancer) pull the pod out of rotation for a
condition the HTTP probe - and the framework's own documented policy - says shouldn't be blocking.

### Suggested fix shape (not implemented — read-only review)
`BenzeneHealthCheckBridge.CheckHealthAsync` should apply the same downgrade
`HealthCheckProcessor.RunTimedAsync` does (`Failed && IsNonCritical && !IsPersistent` → treat as
`Warning`/`Degraded` for the aggregate decision) before deciding `Unhealthy` vs `Degraded` vs `Healthy`,
rather than reading `result.Status` unconditionally. Since this package deliberately doesn't take a
`Benzene.HealthChecks` project reference, the downgrade rule itself would need to be duplicated here (the
same way `DuplicateTypeSuffixer` already duplicates `HealthCheckNamer`'s convention, per the existing
comment at lines 73-77) rather than shared.

---

## 3. No bug — `BenzeneHealthCheckBridge.CheckHealthAsync` when an underlying check *throws*

### What was checked
`BenzeneHealthCheckBridge.CheckHealthAsync` (line 71: `await Task.WhenAll(checks.Select(x =>
x.ExecuteAsync(cancellationToken)))`) has no try/catch of its own, unlike
`Benzene.HealthChecks.ExceptionHandlingHealthCheck` (which the bridge doesn't use - see finding #2's
context on why). So a raw `IHealthCheck` registered directly via `services.AddScoped<IHealthCheck,
MyCheck>()` (bypassing `Benzene.HealthChecks`' own `HealthCheckBuilder`/`ExceptionHandlingHealthCheck`
wrapping entirely) that **throws** instead of returning a `Failed` result would propagate an exception out
of `BenzeneHealthCheckBridge.CheckHealthAsync` as a whole.

### What actually happens
ASP.NET Core's own `HealthCheckService` wraps every registration's `IHealthCheck.CheckHealthAsync` call in
its own try/catch and reports the registration's `FailureStatus` (defaulting to `Unhealthy`) on an
uncaught exception. `Grpc.AspNetCore.HealthChecks` then maps `HealthStatus.Unhealthy` → grpc.health.v1
`NOT_SERVING` as normal. So the outer framework already provides the safety net `BenzeneHealthCheckBridge`
itself doesn't - the exception does **not** escape the grpc.health.v1 protocol.

### Proof (reverted after use; not committed)
Added a `ThrowingHealthCheck : IHealthCheck` (`ExecuteAsync` throws `InvalidOperationException`
unconditionally) registered the same way `GrpcHealthAndReflectionTest`'s existing `FailingHealthCheck` is,
driven through a real `TestServer` + `Health.HealthClient` (grpc.health.v1) end-to-end:

```
Health_WhenABenzeneHealthCheckThrows_ReportsNotServingRatherThanEscaping → PASSED
(response.Status == ServingStatus.NotServing)
```

### Verdict
Correct behaviour, confirmed empirically rather than assumed. No fix needed. (Contrast with finding #1,
where the equivalent gRPC-server-side path genuinely has no outer safety net - `Grpc.AspNetCore`'s "wrap
the whole call" behaviour there produces `StatusCode.Unknown` with no classification and, worse, a stale
success trailer, which *is* the bug.)

---

## 4. Documentation gap (not filed as a bug) — `HealthCheckProcessor`'s "no ambient CancellationToken" caveat

### What was checked
`HealthCheckProcessor.RunTimedAsync` passes `CancellationToken.None` into every check's own
`ExecuteAsync`, with only `TimeOutHealthCheck`'s internally-derived timeout CTS providing any
cancellation signal - **not** the real ambient request/host-shutdown token. This is called out precisely
and correctly in an inline `//` comment (`HealthCheckProcessor.cs` lines 88-90: *"PerformHealthChecksAsync
takes no ambient CancellationToken (out of scope for this change)"*), flagged in round 16's performance
review as known/deliberate and not filed.

### Discoverability
Checked every place a reader would plausibly look for this:
- `IHealthCheckProcessor.PerformHealthChecksAsync`'s XML doc (`Benzene.HealthChecks.Core/IHealthCheckProcessor.cs`)
  - no mention.
- `HealthCheckProcessor`'s class-level XML doc - describes the timeout/`ExceptionHandlingHealthCheck`/
  `IsNonCritical` behaviour in detail, but not this.
- `docs/health-checks.md`'s `TimeOutHealthCheck`/`ExceptionHandlingHealthCheck` section (the one place
  that documents the internal safety net publicly) - no mention of the missing ambient token.
- `grep -rn "cancellationtoken\|ambient\|cancellation" docs/health-checks.md` - the two hits are both
  about `ShutdownReadinessHealthCheck`'s own, unrelated `ShutdownState.LinkTo(CancellationToken)`.

The caveat is real, accurate, and deliberate, but it is **only** discoverable by reading the private
implementation of `RunTimedAsync` - it doesn't surface in IntelliSense, generated API docs, or the
health-checks guide. Practical consequence: a slow custom `IHealthCheck` won't observe a client
disconnect or host-shutdown signal early - it runs to its own (or the processor's default 10s) timeout
regardless, even during a graceful drain. Below this reviewer's bug bar (deliberate, bounded, no
crash/corruption), but flagged per the assignment's explicit ask about discoverability; recommend a `///`
remark on `IHealthCheckProcessor.PerformHealthChecksAsync` and a line in `docs/health-checks.md`'s
`TimeOutHealthCheck`/`ExceptionHandlingHealthCheck` section next time that section is touched.

---

## 5. No bug — W3C trace context nested-span linkage under a fire-and-forget background task

### What was checked
A handler that spawns fire-and-forget background work (`_ = Task.Run(...)`, not awaited) which itself
starts a new `Activity` via `BenzeneDiagnostics.ActivitySource`, where the background work doesn't even
begin running until *after* the request has completed and `UseW3CTraceContext`'s `using (activity)` block
has already disposed the root span.

### Proof (reverted after use; not committed)
Built a minimal pipeline (`UseW3CTraceContext()` + an inline middleware that fires an unawaited
`Task.Run` doing `await Task.Delay(50)` then starting `"Background.Child"`), with a process-global
`ActivityListener` recording every started `Activity`. Awaited the background task separately (purely for
the test to observe it) after the request pipeline had already returned. Result:

```
W3CTraceContext:      span=c9e4... parent=0000000000000000  (AddDiagnostics' own wrapper around the
                                                               UseW3CTraceContext stage itself - see its
                                                               doc comment)
W3CTraceContext.Root: span=b7ff... parent=c9e4...            (the actual root span)
<unnamed>:            span=f74b... parent=b7ff...            (the inline downstream middleware, itself
                                                               auto-wrapped by ActivityMiddlewareDecorator)
Background.Child:     span=ab1e... parent=f74b...             (started ~50ms after the request completed)
```

Same `TraceId` throughout, and `Background.Child`'s ancestor chain (walked by `SpanId`, not by object
reference) correctly reaches back to `W3CTraceContext.Root`. This is standard, correct .NET `AsyncLocal`/
`ExecutionContext` behaviour: `Task.Run` captures the ambient `Activity.Current` at fork time as an
independent snapshot, so disposing the parent `Activity` object on the original flow afterward doesn't
retroactively sever or corrupt the already-forked child's view of it - the child's `TraceId`/`ParentSpanId`
are fixed at the moment it's started, from whatever `Activity` object it captured, regardless of that
object's later disposal.

### Verdict
No bug in either direction the assignment asked about: the link is not silently lost, and it's not
inappropriately fabricated either - it's exactly the context that was ambient when the background work was
kicked off, which is the correct, intended W3C/OTel semantic for "background work caused by this
request." (Whether unbounded fire-and-forget work "leaking" into a request's trace long after the request
ends is desirable *product* behaviour is a separate, judgement-call question - not a defect in Benzene's
trace-context propagation, which is working as designed.)

---

## 6. Mesh UI client JS — not reviewable in this checkout

`src/Benzene.Mesh.Ui/mesh-ui.html` is a single vendored, minified production bundle (a "re-vendor" from
an external build, per its own commit history - e.g. "Re-vendor the mesh UI: Wave E, the third state at
every grain"). No `.tsx`/`.jsx`/unminified source exists anywhere in this repo or the surrounding
filesystem checkouts. Reverse-engineering minified bundle code for a double-click/race condition is not a
reliable way to find a genuine, provable bug, and isn't a good use of remaining scope given three
confirmed findings above. Recommend this item is picked up against the mesh UI's actual source repo (not
this vendored artifact) if it needs a fresh pass beyond round 14's #204-207.

---

## Files touched during investigation (all reverted, none committed)
- `test/Benzene.Grpc.Test/GrpcStreamingHostingTest.cs` (temp: mid-stream-exception red tests for
  Subscribe/Chat) - reverted via `git checkout --`.
- `test/Benzene.Grpc.Test/Handlers/SubscribeThrowingMidStreamMessageHandler.cs`,
  `ChatThrowingMidStreamMessageHandler.cs` (temp handlers) - deleted.
- `test/Benzene.Grpc.Test/GrpcHealthAndReflectionTest.cs` (temp: throwing-health-check green test) -
  reverted via `git checkout --`.
- `test/Benzene.Grpc.Test/BenzeneHealthCheckBridgeTest.cs` (temp:
  `BenzeneHealthCheckBridgeIsNonCriticalTest`) - reverted via `git checkout --`.
- `test/Benzene.Core.Test/Diagnostics/W3CTraceContextFireAndForgetTest.cs` (temp: fire-and-forget span
  linkage green test) - deleted.

`git status`/`git diff` confirmed clean against `main` @ `4389bfb` after cleanup (aside from other
review agents' own untracked files elsewhere in `work/` and `test/`, left untouched).
