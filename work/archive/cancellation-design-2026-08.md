> ARCHIVED 2026-08-20: actioned; all phases shipped (`ICancellationTokenAccessor` plumbing + SQS/Azure Functions/GCP wiring; regression tests in `test/Benzene.Core.Test`).

# Cancellation for Message Handlers — Design

**Status:** **SHIPPED** (verified against source 2026-08-20) — all five phases below are implemented: the token overloads on
`IMiddlewareApplication`/`MiddlewareApplication` and `ICancellationTokenAccessor`, the SQS consumer, the
Google Cloud Functions and Azure Functions non-HTTP triggers, `UseTimeout` (`Benzene.Resilience`'s
`TimeoutMiddleware`), and the documentation/example adoption. Regression tests:
`SqsConsumerCancellationTest`, `AzureFunctionCancellationTest`, `PubSubCancellationTest`,
`TimeoutMiddlewareTest`. Kept here rather than archived because shipped source cites this path as the
design of record.
**Date:** 2026-08-13
**Source:** the pending task "Design: CancellationToken for message handlers"
(`work/1.0-release-plan.md`, Tier-1 list — *"(Not go-live critical) `CancellationToken` design …
additive, can follow the tag"*), plus the scoping discovery recorded under Tier 3.1: the middleware
framework threads no `CancellationToken` through any signature, and full end-to-end threading would
be a breaking change to `IMiddleware<TContext>`.
**Audience:** implementation agents. Each phase is a self-contained task; do them in order unless
its "Depends on" says otherwise. This is an S/M-sized initiative — most of the machinery already
exists (§1); the phases below **complete** it, they do not invent it.

**Decisions already made (do not re-litigate):**
1. **Owner ruling:** cancellation is exposed through **something you can inject** — available to
   handlers *and* middleware by resolving an abstraction from the per-message scope. It is
   explicitly **not** threaded through every signature: *"I don't think we need to pass it down
   everywhere."* Therefore `IMessageHandler<TRequest, TResponse>.HandleAsync(TRequest request)`
   **does not change**, and neither does `IMiddleware<TContext>.HandleAsync(TContext, Func<Task>)`.
2. The abstraction is the **already-shipped** `ICancellationTokenAccessor`
   (`src/Benzene.Abstractions/DI/ICancellationTokenAccessor.cs`) — do not rename it, do not add a
   parallel type. This design finishes its rollout.
3. The spec already codifies this model (`Benzene/docs/specification/core-concepts.md` §4: *"The
   pipeline carries no cancellation parameter … invocation-scoped facts ride on the context (or an
   accessor resolved from the invocation's scope)"*). **Spec impact of this work: none** (§6).

---

## 1. Current state — what already exists (verified 2026-08-13)

This is not greenfield. The injectable-accessor design is implemented and partially wired:

**The abstraction.**
- `ICancellationTokenAccessor` — read side, one property `CancellationToken CancellationToken`
  (`src/Benzene.Abstractions/DI/ICancellationTokenAccessor.cs`). Modelled on ASP.NET Core's
  `IHttpContextAccessor` (its own XML docs say so).
- `CancellationTokenAccessor` — the scoped mutable holder / write side
  (`src/Benzene.Core/CancellationTokenAccessor.cs`), defaulting to `CancellationToken.None`.
- `SeedCancellationToken(this IServiceResolver, CancellationToken)`
  (`src/Benzene.Core/CancellationTokenAccessorExtensions.cs`) — the one seeding helper. No-op when
  the token `!CanBeCanceled` and no-op when no accessor is registered, so it is safe from any host.
- Registered **scoped** unconditionally in `AddBenzene()`
  (`src/Benzene.Core.MessageHandlers/DI/Extensions.cs:112–113`): concrete class + interface
  forwarding, `TryAddScoped` so a user override wins.

**Per-message scope + seeding point.** Both `MiddlewareApplication<…>` overloads create one DI
scope per event and have a `HandleAsync(TEvent, IServiceResolverFactory, CancellationToken)`
overload that calls `SeedCancellationToken` right after `CreateScope()`
(`src/Benzene.Core.Middleware/MiddlewareApplication.cs:47–54, 92–98`); the no-token overload
delegates with `None`.

**Exception semantics (the cancellation contract).** `ExceptionHandlerMiddleware<TContext>`
(`src/Benzene.Core.Middleware/ExceptionHandlerMiddleware.cs:35–42`) and `MessageHandler`
(`src/Benzene.Core.MessageHandlers/MessageHandler.cs:66–72, 99–105`) **rethrow** an
`OperationCanceledException` whose token has fired, so settle/ack/checkpoint transports redeliver
interrupted work instead of treating it as done. Everything else becomes a failure result. This
contract is load-bearing for Phase 4 and must not be weakened.

**Hosts wired today vs. not** — the precise map:

| Host / entry point | Platform signal | Status | File |
|---|---|---|---|
| ASP.NET Core | `HttpContext.RequestAborted` | **Seeded** (first pipeline middleware) | `src/Benzene.AspNet.Core/BenzeneExtensions.cs` (`BuildHttpPipeline`, ~line 55) |
| Azure Functions HTTP (AspNet) | `HttpContext.RequestAborted` | **Seeded** | `src/Benzene.Azure.Function.AspNet/DependencyInjectionExtensions.cs:40–43` |
| gRPC | `ServerCallContext.CancellationToken` | **Seeded** | `src/Benzene.Grpc/GrpcMethodHandler.cs:102–103` |
| RabbitMQ worker | `BasicDeliverEventArgs.CancellationToken` | **Seeded** | `src/Benzene.RabbitMq/Extensions.cs:46–49` |
| Kafka worker | worker's linked run token (shutdown) | **Seeded** (token overload) | `src/Benzene.Kafka.Core/BenzeneKafkaWorker.cs:207,210,225` |
| Azure Service Bus worker | `ProcessMessageEventArgs.CancellationToken` | **Seeded** | `src/Benzene.Azure.ServiceBus/ServiceBusConsumerApplication.cs:52` |
| Azure Event Hub worker | `ProcessEventArgs.CancellationToken` | **Seeded** (token overload) | `src/Benzene.Azure.EventHub/BenzeneEventHubWorker.cs:100` |
| **SQS consumer (self-hosted)** | `StartAsync(CancellationToken)` run token | **Dropped** — token never reaches the per-message scope | `src/Benzene.Aws.Sqs/Consumer/SqsConsumer.cs:92` → `SqsConsumerApplication.cs` (scope at `CreateScope()`, no token param) |
| **Google Cloud Functions Pub/Sub** | handler's `CancellationToken` param | **Dropped** — `_app.SendAsync(data)` discards it | `src/Benzene.GoogleCloud.Functions.PubSub/GooglePubSubFunctionHost.cs:45` |
| **Azure Functions non-HTTP triggers** (ServiceBus/EventHub/Kafka/QueueStorage/BlobStorage/EventGrid/CosmosDb/Timer) | `FunctionContext.CancellationToken` (isolated worker binds it as a function parameter) | **Never enters Benzene** — generated triggers don't request it | generated by `src/Benzene.Azure.Function.SourceGenerators/Transports/MessagingTransports.cs` → `IAzureFunctionApp.HandleAsync` (`src/Benzene.Azure.Function.Core/AzureFunctionApp.cs`) |
| AWS Lambda (all `Benzene.Aws.Lambda.*`) | **none** — `ILambdaContext` has no token (only `RemainingTime`) | `None` by design | `src/Benzene.Aws.Lambda.Core/AwsLambdaEntryPoint.cs:41–46` |
| HostedService / SelfHost shell | host stopping token reaches each worker's `StartAsync` | in place (workers above are the seeding points) | `src/Benzene.HostedService/BenzeneHostedServiceStartup.cs:36` |

**Existing consumers** (proof the read side works): `HttpClientMiddleware`, `HttpBenzeneMessageClient`
and the HTTP health checks (`src/Benzene.Clients.Http/*`, `src/Benzene.HealthChecks.Http/*`).

**Existing tests:** `test/Benzene.Core.Test/Core/Middleware/CancellationTokenSeedingTest.cs`.

**The gaps this plan closes:** the three bold rows above; the token overload existing only on the
*concrete* `MiddlewareApplication` (not on `IMiddlewareApplication` / `IEntryPointMiddlewareApplication`
/ `MiddlewareMultiApplication`, which is exactly why GCP had to defer); no middleware that *creates*
cancellation (timeout); no user-facing documentation at all (`docs/**` never mentions the accessor);
no example handler using it.

---

## 2. Design

### 2.1 The abstraction (unchanged) and the guarantee

Keep `ICancellationTokenAccessor` exactly as shipped. Handlers and middleware that care resolve it
(constructor injection — it is scoped, like everything else they inject); everything else stays
untouched.

**Read/write split.** The repo's precedent for scoped contextual state is a read interface + a
write interface over one scoped holder (`ICurrentTransport` / `ISetCurrentTransport` over
`CurrentTransportInfo`, `src/Benzene.Abstractions.MessageHandlers/Info/`). Cancellation already has
an equivalent split with a deliberate asymmetry: the read side is the *interface*; the write side is
the *concrete class* (`CancellationTokenAccessor`, settable property) plus the
`SeedCancellationToken` helper. **Decision: keep it; do not add an `ISetCancellationToken`-style
interface.** Rationale: user code injects the interface and physically cannot set through it; the
write handle (the concrete class) is only reachable by code that deliberately asks for it — the
transports' seeding shims and Phase 4's timeout middleware. A setter interface would advertise the
write side in `Benzene.Abstractions` to every consumer, which is the opposite of "user code can't
stomp it accidentally". (If a user *does* resolve the concrete class and sets it, that is
intentional, and the save/restore rule in §2.2 still contains the blast radius to their own scope.)

**The guarantee (goes verbatim into the XML docs and `docs/`):**

> The accessor's token defaults to `CancellationToken.None`. A handler, middleware, or component
> that never resolves `ICancellationTokenAccessor` behaves byte-for-byte as before — no new
> exceptions, no new statuses, no timing changes. A component that does resolve it must treat the
> token as *advisory and possibly `None`*: on transports with no cancellation concept it simply
> never fires, and code written as `await client.DoAsync(x, cancellation.CancellationToken)` is
> correct everywhere without checking which host it runs on.

### 2.2 Linked-CTS composition and disposal

The effective token for a scope is a composition: **host/platform token** (seeded once at scope
creation) **wrapped by zero or more middleware-added sources** (timeout, custom deadline). Rules:

1. **The accessor stores only `CancellationToken`s, never a `CancellationTokenSource`.** Whoever
   creates a CTS owns and disposes it. The accessor never disposes anything (tokens are structs;
   nothing to leak in the holder itself).
2. **Seeding (hosts):** hosts pass their platform token into the token overload / call
   `SeedCancellationToken` exactly once, before user middleware runs. Host tokens' CTSes belong to
   the platform (Kestrel, the SDK's processor, the worker loop) — Benzene never disposes them.
3. **Wrapping (middleware):** a middleware that adds a source follows the save/restore pattern —
   this is the only sanctioned way to write the accessor after seeding:

   ```csharp
   var original = accessor.CancellationToken;
   using var cts = CancellationTokenSource.CreateLinkedTokenSource(original);
   cts.CancelAfter(timeout);                       // or link any other source
   accessor.CancellationToken = cts.Token;
   try { await next(); }
   finally { accessor.CancellationToken = original; }
   ```

   The `using` guarantees the linked CTS (and its timer, and its registration on the host token —
   the actual leak vector) is disposed when the middleware unwinds, on every path. Restoring
   `original` in `finally` means on-response halves of *outer* middleware observe the host token,
   not a by-then-disposed linked token, and nested wrappers compose naturally (innermost wins while
   inside it). Because the accessor is scoped and each message gets a fresh scope, no state crosses
   messages even if a middleware forgets to restore.
4. **Reading:** always read `accessor.CancellationToken` at the moment of use (property access, not
   a captured copy from construction time) — wrapping middleware may have replaced it since the
   component was constructed. Document this in the accessor's XML docs; the existing consumers in
   `Benzene.Clients.Http` already read per-call.

### 2.3 Pipeline-runner token honoring — recommendation: **do not add it**

Should `MiddlewarePipeline<TContext>.HandleAsync` (`src/Benzene.Core.Middleware/MiddlewarePipeline.cs`)
check the ambient token between links? **No.** Firm recommendation, three reasons:

1. **It interrupts nothing that matters.** The gaps between links are nanoseconds of delegate
   dispatch; the time a cancellable unit of work actually spends is *inside* a link (handler I/O,
   an outbound call, a retry sleep) — exactly where a runner-level check cannot reach and where the
   accessor-at-the-point-of-use model already works.
2. **It is a real behaviour change with the worst possible failure mode.** Kafka seeds the
   *worker-shutdown* token for every in-flight message. A between-links check would make every
   graceful deploy abort messages that were milliseconds from completing — before their
   result-setting and settlement middleware ran — converting clean drains into mass redelivery.
   The current contract (cancellation surfaces only where code cooperatively observes it, and
   propagates as an OCE that the exception middleware deliberately rethrows for redelivery) is
   carefully balanced; a runner check would sit on top of it as a blunt instrument.
3. **The spec forbids the signature and codifies the alternative.** Core-concepts §4: the pipeline
   carries no cancellation parameter, precisely so the middleware shape is identical across
   transports with no cancellation concept.

Cooperative observation points that *are* worth having: the handler's own I/O (Phase 5 example),
outbound HTTP clients (done), and — noted as an optional follow-up, not a phase —
`RetryMiddleware`'s inter-attempt delay (`src/Benzene.Resilience/RetryMiddleware.cs`) observing the
ambient token so shutdown doesn't wait out a backoff sleep.

### 2.4 Timeout vs. cancellation — the semantic line Phase 4 must hold

A **host cancellation** (shutdown, client disconnect) must propagate as an OCE-with-fired-token so
transports redeliver (§1's exception contract). A **timeout** the *service operator* configured is
a service-side failure and must become a **failure result** (`BenzeneResultStatus.Timeout` =
`"timeout"`, `src/Benzene.Results/BenzeneResultStatus.cs:29`), not masquerade as host cancellation.
Phase 4's middleware is exactly the component that can tell them apart, because it owns the linked
CTS: if `original.IsCancellationRequested` → genuine cancellation, rethrow; else it was the timer →
translate. Under safe-by-default settlement a timeout *failure result* is still redelivered on queue
transports, so nothing is lost — but request/response transports return a proper `timeout` status
instead of an opaque aborted call.

---

## Phase 1 — Complete the core plumbing (token overloads on the interfaces)

**Goal:** every application shape can receive a host token, so no host has to defer wiring the way
GCP did. **Depends on:** nothing. **Effort:** S.

Steps:
1. Add `Task SendAsync(TEvent @event, CancellationToken cancellationToken)` (and the `TResult`
   twin) to `IEntryPointMiddlewareApplication<…>`
   (`src/Benzene.Abstractions.Middleware/IEntryPointMiddlewareApplication.cs`) as **default
   interface methods** delegating to the tokenless `SendAsync` (TFM is net10.0; a DIM keeps every
   existing implementor — including user-written ones — source- and binary-compatible).
   Override them properly in `EntryPointMiddlewareApplication<…>`
   (`src/Benzene.Core.Middleware/EntryPointMiddlewareApplication.cs`) to call the concrete
   `MiddlewareApplication` token overload.
2. Same treatment for `IMiddlewareApplication<…>`
   (`src/Benzene.Abstractions.Middleware/IMiddlewareApplication.cs`): DIM
   `HandleAsync(TEvent, IServiceResolverFactory, CancellationToken)` delegating to the tokenless
   overload; `MiddlewareApplication<…>` already implements it, so its overload becomes the
   interface implementation.
3. Add the token overload to `MiddlewareMultiApplication<…>`
   (`src/Benzene.Core.Middleware/MiddlewareMultiApplication.cs`), seeding **each per-record scope**
   (batch = one invocation per message per the spec's scope rule).
4. Put the §2.1 guarantee statement into the XML docs of `ICancellationTokenAccessor` and both new
   overload sets; put the §2.2 read-at-point-of-use rule into the accessor's docs.
5. Tests (extend `test/Benzene.Core.Test/Core/Middleware/CancellationTokenSeedingTest.cs`): DIM
   default delegates to tokenless; multi-application seeds every record's scope; a legacy
   implementor of `IEntryPointMiddlewareApplication<TEvent>` (no override) still compiles and runs.

**Acceptance:** `Benzene.sln` builds with zero changes to any existing implementor; new tests
green; `PipelineResolutionStartUpCheck` unaffected (no middleware signature touched).

## Phase 2 — Broker/self-hosted wiring: the SQS consumer (the named adopter)

**Goal:** the self-hosted SQS consumer's run token reaches each message's scope, so in-flight
handlers observe shutdown. **Depends on:** Phase 1 (uses the multi/seed pattern; no interface
change strictly required, but do it after so the shapes match). **Effort:** S.

Steps:
1. `SqsConsumerApplication` (`src/Benzene.Aws.Sqs/Consumer/SqsConsumerApplication.cs`): add a
   `HandleAsync(ReceiveMessageResponse, IServiceResolverFactory, CancellationToken)` overload;
   inside the per-message fan-out, call `scope.SeedCancellationToken(cancellationToken)` right
   after `CreateScope()` (mirror `MiddlewareApplication`). Tokenless overload delegates with
   `None`.
2. `SqsConsumer.StartAsync` (`src/Benzene.Aws.Sqs/Consumer/SqsConsumer.cs:92`): pass its
   `cancellationToken` into that overload.
3. Verify the settlement interaction deliberately (and assert it in a test): a message whose
   handler throws OCE-with-fired-token at shutdown is **not** deleted from the queue (under
   `PerMessage` it must land in the failed set — check the catch in `SqsConsumerApplication`
   treats the rethrown OCE like any throw: reported failed / not deleted).
4. Record in the table in §1 (and `src/Benzene.Aws.Sqs/CLAUDE.md`) that SQS is now a seeding host.
   Note explicitly: the RabbitMQ/Kafka/ServiceBus/EventHub rows need **no work** (already seeded);
   `BoundedConcurrentDispatcher`'s `CancellationToken.None` lane argument
   (`src/Benzene.SelfHost/BoundedConcurrentDispatcher.cs:202`) stays as-is — RabbitMQ seeds
   per-delivery from `BasicDeliverEventArgs`, not from the lane.

**Acceptance:** a test drives `SqsConsumer` with a cancellable token, cancels mid-handler, and
observes (a) the handler's ambient token fired, (b) the message not deleted, (c) the loop exits
cleanly.

## Phase 3 — Serverless wiring: Google Cloud Functions + Azure Functions non-HTTP triggers

**Goal:** the two platforms that hand us a real token but currently drop it, stop dropping it.
AWS Lambda is *recorded* here as "no platform token — `None` by design" (its `ILambdaContext` has
no token; `RemainingTime` deadlines are served by Phase 4's `UseTimeout`, see the docs phase).
**Depends on:** Phase 1 (the `SendAsync`/`HandleAsync` token overloads). **Effort:** M (the
source-generator edit is the M).

Steps:
1. **GCP:** `GooglePubSubFunctionHost.HandleAsync`
   (`src/Benzene.GoogleCloud.Functions.PubSub/GooglePubSubFunctionHost.cs:45`) forwards its
   `cancellationToken` via the new `SendAsync(data, cancellationToken)`. This closes the item
   explicitly deferred in `src/Benzene.Core.Middleware/CLAUDE.md` ("needs a token overload on
   `IEntryPointMiddlewareApplication` — deferred").
2. **Azure Functions (isolated) non-HTTP:** add an optional `CancellationToken` parameter
   (default `default`) to `IAzureFunctionApp.HandleAsync<…>` and `AzureFunctionApp`
   (`src/Benzene.Azure.Function.Core/`), forwarding to `SendAsync(…, token)`; same optional
   parameter on each transport's `Handle*` extension (`HandleServiceBusMessages`,
   `HandleEventHub`, `HandleKafkaEvents`, `HandleQueueMessage`, `HandleBlob`,
   `HandleEventGridEvent`, `HandleCosmosDbChanges`, `HandleTimer` — one file per
   `src/Benzene.Azure.Function.{ServiceBus,EventHub,Kafka,QueueStorage,BlobStorage,EventGrid,CosmosDb,Timer}/Extensions.cs`).
3. **Source generator:** `src/Benzene.Azure.Function.SourceGenerators/Transports/MessagingTransports.cs`
   — generated trigger methods gain a `CancellationToken cancellationToken` parameter (the
   Functions isolated worker binds it automatically) and pass it to the `Handle*` call. Regenerate
   the snapshot expectations in the generator's tests.
4. Optional parameters keep hand-written user functions compiling unchanged; note in the changelog
   that regenerating (rebuilding) picks up the wiring automatically.
5. Tests: GCP — token flows to the accessor inside a handler
   (`Benzene.GoogleCloud.Functions.PubSub.TestHelpers`); Azure — one trigger-level test per shape
   family (single-message + batch) asserting the ambient token in the handler equals the one passed
   to the generated function (use the existing `*.TestHelpers` packages).

**Acceptance:** GCP and Azure Functions handlers observe their platform token via
`ICancellationTokenAccessor`; the `CLAUDE.md` "no signal / deferred" notes in
`src/Benzene.Core.Middleware/CLAUDE.md` are updated to match reality; Lambda's row documented as
by-design `None`.

## Phase 4 — `UseTimeout` (the proof-consumer middleware)

**Goal:** a middleware that *creates* cancellation, demonstrating the whole feature with zero
handler changes: `.UseTimeout(TimeSpan.FromSeconds(5))`. **Depends on:** none (works today on any
seeded or unseeded host), but land after Phase 1 for coherent docs. **Effort:** M.

Home: **`Benzene.Resilience`** next to `RetryMiddleware` (`src/Benzene.Resilience/`) — it is a
resilience policy, and that package already owns the `Use*` idiom (`Extensions.cs`).

Steps:
1. `TimeoutMiddleware<TContext> : IMiddleware<TContext>` (`Name => "Timeout"`), constructor takes
   `(CancellationTokenAccessor accessor, TimeSpan timeout)`. `HandleAsync` implements §2.2's
   save/restore pattern exactly, plus §2.4's translation:

   ```csharp
   public async Task HandleAsync(TContext context, Func<Task> next)
   {
       var original = _accessor.CancellationToken;
       using var cts = CancellationTokenSource.CreateLinkedTokenSource(original);
       cts.CancelAfter(_timeout);
       _accessor.CancellationToken = cts.Token;
       try
       {
           await next();
       }
       catch (OperationCanceledException ex)
           when (ex.CancellationToken == cts.Token && !original.IsCancellationRequested)
       {
           // The timer fired, not the host: a service-side timeout, not a genuine cancellation.
           throw new TimeoutException($"Pipeline exceeded the configured timeout of {_timeout}.", ex);
       }
       finally
       {
           _accessor.CancellationToken = original;
       }
   }
   ```

   Host-fired cancellation falls through the `when` filter and propagates untouched (redelivery
   contract intact). The thrown `TimeoutException` does **not** carry a fired token, so
   `ExceptionHandlerMiddleware` converts it into the transport's failure result — by design, per
   the "known edge" note in `src/Benzene.Core.Middleware/CLAUDE.md`, which this deliberately
   exploits rather than fights.
2. `.UseTimeout<TContext>(this IMiddlewarePipelineBuilder<TContext>, TimeSpan)` in
   `src/Benzene.Resilience/Extensions.cs`:
   `app.Use(resolver => new TimeoutMiddleware<TContext>(resolver.GetService<CancellationTokenAccessor>(), timeout))`.
   The factory lambda runs per invocation inside the message scope
   (`MiddlewarePipeline.CreateChain` resolves lazily), so the middleware gets *that message's*
   accessor.
3. Map `TimeoutException` → `BenzeneResult.Timeout(...)` (`"timeout"` status) with a
   `catch (TimeoutException)` in `MessageHandler`
   (`src/Benzene.Core.MessageHandlers/MessageHandler.cs`, before the generic `catch`) so a timeout
   raised *inside* the handler wrapper yields the precise status; a timeout surfacing above the
   router still becomes the transport's generic failure via `ExceptionHandlerMiddleware` — document
   the difference in the middleware's XML docs. Note the retry caveat that already exists on the
   status: `timeout` is transient but not retry-*safe* (`BenzeneResultStatus.cs` remarks).
4. **Startup-check compatibility (must verify with a test):** `PipelineResolutionStartUpCheck`
   (`src/Benzene.Core.MessageHandlers/StartUpChecks/PipelineResolutionStartUpCheck.cs`) constructs
   every middleware factory at startup, outside a real message scope. This works because
   `CancellationTokenAccessor` is registered unconditionally in `AddBenzene()` (§1) and resolving
   it from the startup resolver just yields a default holder (`None`). Add a startup-check test
   with `.UseTimeout(...)` in the pipeline to pin this.
5. Tests (`test/Benzene.Core.Test/`, alongside `CancellationTokenSeedingTest`): (a) handler
   observing the ambient token gets cancelled at the deadline and the pipeline yields a `timeout`
   failure result; (b) handler ignoring the token but finishing in time — untouched result,
   no exception; (c) host token fires first — OCE propagates (not translated), accessor restored;
   (d) nested `UseTimeout` — inner deadline wins inside, outer restored after; (e) no CTS leak —
   the linked CTS is disposed on the success path too (assert via a fake/registration);
   (f) unseeded host (`original == None`) — works, timer is the only source.
6. Docs stub: add a `## UseTimeout` section to `docs/common-middleware.md` (place it next to
   `UseRetry`), completed in Phase 5.

**Acceptance:** all six test cases green; `.UseTimeout` demonstrably cancels a long-running example
handler on any host with no change to the handler's signature; startup check passes with the
middleware registered.

## Phase 5 — Documentation + example adoption

**Goal:** the feature is discoverable and has one canonical usage example. **Depends on:**
Phases 1–4 (documents what shipped). **Effort:** S.

Steps:
1. **`docs/message-handlers.md`** — new section "Cancellation" after the handler-interface
   sections: the owner model in one paragraph (inject `ICancellationTokenAccessor`; signatures
   never change), the guarantee statement (§2.1), a handler snippet passing
   `_cancellation.CancellationToken` into its own I/O, and the per-host table from §1 (which hosts
   seed what; Lambda = `None`, use `UseTimeout` for deadline-style limits, e.g. derived from your
   function's configured timeout).
2. **`docs/common-middleware.md`** — complete the `UseTimeout` section (behaviour, the
   timeout-vs-cancellation semantic line from §2.4, the redelivery note for queue transports, the
   `timeout`-status retry-safety caveat).
3. **`docs/middleware.md`** — short "Middleware and cancellation" note: read the accessor at point
   of use; the save/restore pattern (§2.2) for middleware that wraps the token; pointer to
   `TimeoutMiddleware` as the reference implementation.
4. **Example:** one long-running handler in an existing example app (e.g.
   `examples/Asp/Benzene.Example.Asp` — its host already seeds `RequestAborted`) that injects the
   accessor and forwards the token into a delay/HTTP call, with `.UseTimeout(...)` on the pipeline.
   No new example project.
5. Update `src/Benzene.Core.Middleware/CLAUDE.md`, `src/Benzene.Aws.Sqs/CLAUDE.md`,
   `src/Benzene.Resilience/CLAUDE.md` to match shipped reality; tick the item off
   `work/1.0-release-plan.md`'s pending list with a pointer to this document.

**Acceptance:** docs build/link-check clean; the example compiles and demonstrates cancellation
end-to-end; no `CLAUDE.md` claims drift.

---

## 6. Wire contract and cross-language ports — spec impact: none (verified)

- **No wire change.** No header, no payload field, no status addition (`timeout` already exists in
  the status vocabulary and in `wire-contracts.md` §4 mappings), no fixture change. Cancellation is
  a host-runtime signal that never crosses the wire.
- **The spec already records the concept for ports**, so nothing needs adding there either:
  `core-concepts.md` §4 mandates the ambient-accessor model ("the pipeline carries no cancellation
  parameter"), §6 lists the cancellation token among invocation-scoped context facts, and
  `transport-bindings.md` §1 requires each binding to state its cancellation/deadline source "(or
  that the transport has none)". Other language ports implement this with their runtime's idiom
  (Go `context.Context`, TS `AbortSignal`, Python task cancellation) — host-runtime-specific, not a
  wire contract. If anything, the .NET port is *catching up to* the spec here.
- The only conceivable spec follow-up — naming `UseTimeout` in a common-middleware profile — is
  explicitly not proposed; it is a .NET convenience, not a conformance surface.

## 7. Out of scope

- **Changing `IMessageHandler<…>` or `IMiddleware<…>` signatures** — owner ruling; permanently out.
- **Cooperative cancellation *inside* serializers/transport SDK internals** beyond passing the
  ambient token where an SDK call already accepts one. Follow-up candidates (not this initiative):
  `RetryMiddleware` delay observation (§2.3), idempotency/cache/event-store implementations
  threading the ambient token into their own store calls.
- **Pipeline-runner between-links token checks** — considered and rejected with reasons (§2.3), not
  deferred.
- **A Lambda `RemainingTime`-derived automatic deadline** — would change behaviour for every Lambda
  user; the composable answer is `UseTimeout` with an operator-chosen value (documented in
  Phase 5). Revisit only on user demand.
- **Cross-language port implementations** — each port's own repo; the spec already states the
  contract (§6).
- **Client-side request cancellation** (`IBenzeneClient` call sites taking a caller token) — the
  outbound clients already read the ambient accessor where wired (`Benzene.Clients.Http`);
  a caller-supplied-token API is a separate clients-redesign concern
  (`work/archive/benzene-clients-redesign-plan-2026-07.md`).
