> ARCHIVED 2026-08-20: actioned; shipped as `src/Benzene.Clients.InProcess` (its CLAUDE.md cites this design).

# In-process transport — design + what shipped

**Status:** Implemented (`Benzene.Clients.InProcess`). This document started as a design proposal
before any code existed; it has been updated in place to describe what was actually built, and to
record where implementation diverged from the original proposal and why. Companion to
`work/archive/lightweight-non-http-transport-design-2026-07.md` (which solves the opposite problem — making
cross-container calls cheaper) and `work/archive/benzene-clients-redesign-plan-2026-07.md` (whose
`IBenzeneMessageSender`/`OutboundRoutingBuilder` shape this slots into).

## The ask

> A topic used to live in service A and was called over some transport (SQS, HTTP, …) by service B.
> Functionality moves around over time — that topic's handler ends up merged into the same service
> as its caller. Once caller and handler are in the same process, there's little point putting the
> call over a real transport. The requirement is an "internal transport" that never leaves the
> runtime — recognised as not an ideal way to build a system in general, but something that should
> be *available as an option* when a consolidation like this genuinely happens.

This shipped as a **fifth transport** with the same status as SQS/SNS/HTTP/gRPC — opt-in, per topic,
explicit — not a mode that changes behaviour automatically based on what happens to be co-located.

## Naming: `InProcess`, not `Internal`

The original proposal used `Internal`/`.UseInternal()`/`TransportNames.Internal` throughout, flagging
in its own open questions that "internal" could be misread as "internal to the company/VPC" rather
than "internal to this literal running process." That risk was judged real enough to act on:
everything shipped as **`InProcess`** — package `Benzene.Clients.InProcess`, `.UseInProcess()`,
`TransportNames.InProcess = "in-process"` (hyphenated, matching `service-bus`/`queue-storage`).

## What already existed that this built on

- **`BenzeneMessageApplication`** (`src/Benzene.Core.MessageHandlers/BenzeneMessage/BenzeneMessageApplication.cs`)
  is the transport-neutral, direct-invocation join point every other transport funnels into:
  `HandleAsync(IBenzeneMessageRequest, IServiceResolverFactory, CancellationToken)`.
- **`RabbitMqBenzeneTestHost`/`KafkaBenzeneTestHost<K,V>`** already prove the mechanics work outside
  of any real transport: `_application.HandleAsync(nativeEvent, _serviceResolverFactory)`, zero I/O
  beyond the pipeline itself — gated to tests only until now.
- **`OutboundRoutingBuilder`/`IBenzeneMessageSender`/`OutboundContext`** (shipped per
  `work/archive/benzene-clients-redesign-plan-2026-07.md`) is the outbound registration surface `.UseInProcess()`
  slots into as a sibling of `.UseSqs`/`.UseSns`/`.UseHttp`.
- **`TransportNames`** is an open `static class` of `public const string` fields — adding `InProcess`
  was a one-line, additive change.
- **`OutboundRouteInspector`** (`src/Benzene.Descriptor/`) infers a produced topic's outbound
  transport kind by convention (`XxxSendMessageContext` → naive-lowercase `"xxx"`), reflected off the
  outbound pipeline's context-converter middleware. See "Naming inconsistency" below for how this
  interacts with the hyphenated `TransportNames.InProcess`.

## What shipped, and where it diverges from the original proposal

### The raw-pipeline-reuse assumption was wrong — verified before writing any code

The original proposal's §2.1 assumed `BenzeneMessageApplication`'s inner, untagged
`IMiddlewarePipeline<BenzeneMessageContext>` could be resolved from DI independently of the endpoint
that builds it, so the in-process transport could reuse *the same* pipeline instance a service's
`.UseBenzeneMessage()` HTTP/Lambda endpoint already has, just re-tagged. It flagged this explicitly as
an "open implementation question... verify... during implementation."

That verification (reading `src/Benzene.Http/BenzeneMessage/Extensions.cs` and
`src/Benzene.Aws.Lambda.Core/BenzeneMessage/Extensions.cs` in full, and grepping for any
`AddSingleton`/`AddScoped` registration of `IMiddlewarePipeline<BenzeneMessageContext>`) found none:
the pipeline is built fresh per explicit `UseBenzeneMessage(...)` call and captured in a closure —
never registered as an independently-resolvable DI service. `.UseBenzeneMessage(...)` is also a
separate, security-sensitive, fully opt-in feature (it exposes every topic over HTTP/Lambda invoke,
explicitly documented as "restrict/authenticate before exposing in production") that most services
won't have enabled at all.

**What shipped instead:** the in-process transport has its **own, independent** inbound registration,
`AddInProcessMessaging(services, configure)`, which builds and registers its own
`BenzeneMessageContext` pipeline and its own tagged dispatcher — completely independent of whatever
`.UseBenzeneMessage()` might also be doing in the same service. A topic reachable both in-process and
over the wire needs its handler registered on both pipelines (documented in the package's `CLAUDE.md`
and in the `AddInProcessMessaging` doc comment).

### Two separate registration calls, not one

- **Inbound:** `services.AddInProcessMessaging(pipeline => pipeline.UseMessageHandlers(...))` — builds
  the dispatch pipeline, tags it `TransportNames.InProcess` via `TransportMiddlewarePipeline`, and
  registers the resulting `IMiddlewareApplication<IBenzeneMessageRequest, IBenzeneMessageResponse>`.
- **Outbound:** `services.AddOutboundRouting(routing => routing.Route(topic, p => p.UseInProcess()))`
  — converts that route's `OutboundContext` pipeline to `InProcessSendMessageContext` and terminates
  it with `InProcessClientMiddleware`, which resolves the inbound dispatcher from DI and calls it with
  its own fresh `IServiceResolverFactory`-created scope.

This mirrors every other transport's inbound/outbound split (e.g. SQS's `AddSqsConsumer`-side
registration vs. `.UseSqs(queueUrl)` on the outbound side) rather than inventing a new shape.

### `AddBenzeneMessageHandling()`: a small, deliberate upstream split

`AddBenzeneMessage()` (the existing registration `.UseBenzeneMessage()` calls) bundles the
`BenzeneMessageContext` request/response plumbing together with `services.AddSingleton<ITransportInfo>(_
=> new TransportInfo(TransportNames.Benzene))` — an announcement that the service exposes a "benzene"
wire endpoint. `AddInProcessMessaging` needs the plumbing but must **not** make that announcement: a
service that only ever calls `AddInProcessMessaging()` has no such wire endpoint, and advertising one
anyway would misrepresent its transport surface to the mesh/descriptor.

Rather than hand-copy `AddBenzeneMessage()`'s registration list into the new package (a drift risk —
the two lists would silently diverge if the shared list changes later), `AddBenzeneMessage()` was
split in place (`src/Benzene.Core.MessageHandlers/DI/Extensions.cs`): the plumbing moved into a new
public `AddBenzeneMessageHandling()`, and `AddBenzeneMessage()` now just calls that plus its
`ITransportInfo` line. `AddInProcessMessaging` calls `AddBenzeneMessageHandling()` and registers its
own `ITransportInfo(TransportNames.InProcess)` instead. Behaviour of `AddBenzeneMessage()` itself is
unchanged — this is a pure extract-method refactor, not a behavior change to any existing caller.

### The `DefaultBenzeneMessageSender` type-erasure gap — fixed generically, not just for InProcess

`DefaultBenzeneMessageSender.SendAsync<TRequest,TResponse>` hard-casts `OutboundContext.Response` to
`IBenzeneResult<TResponse>` and throws if it isn't already that type. Every existing transport avoids
this by only ever producing `IBenzeneResult<Void>` (SQS/SNS are fire-and-forget) — none had needed
real typed-response deserialization through the type-erased `OutboundContext.Response` before.

The in-process transport's `MapResponseAsync` can't produce an already-typed `IBenzeneResult<TResponse>`
either — it doesn't know the caller's `TResponse` at that point, only `DefaultBenzeneMessageSender`
does, once its own generic type parameter is bound. So `InProcessContextConverter.MapResponseAsync`
sets `OutboundContext.Response` to a raw `BenzeneMessageClientResponse` — the same untyped envelope
`AwsLambdaBenzeneMessageClient`/`HttpBenzeneMessageClient` already hand back — and
`DefaultBenzeneMessageSender.SendAsync` gained a fallback branch: if the response isn't already typed
but *is* a `BenzeneMessageClientResponse`, deserialize it via the existing
`BenzeneResultExtensions.AsBenzeneResult<TResponse>(ISerializer)` extension (the same one those two
message clients already use), resolving `ISerializer` from the same `IServiceResolver` the sender
already holds. This is a small, generically-useful fix, not an InProcess-specific hack — any future
transport that wants real typed responses benefits from it too.

### Startup-time fail-fast validation (§2.4 of the original proposal) — dropped

The original proposal called for a dedicated `IStartUpCheck`-style pass asserting every
`.UseInProcess()` topic has a matching in-process handler, failing loudly at startup with every
missing topic named. This was reconsidered and dropped:

- There is no direct signal, at the point `AddInProcessMessaging`/`.UseInProcess()` extensions run, of
  "which topics use `.UseInProcess()`" without disproportionate reflection-based plumbing to discover
  it after the fact (the same kind of best-effort reflection `OutboundRouteInspector` already uses,
  and is explicitly marked SPIKE-GRADE there for exactly this reason).
- `MessageRouter<TContext>`'s *existing* behavior for an unregistered topic already produces a clean,
  honest `NotFound`-shaped response rather than crashing or hanging — matching the exact "honest
  degradation, never a crash" philosophy this codebase already established elsewhere (e.g. the
  `FleetTopicQueryClient`/`MeshArtifactClient` 404-vs-5xx fixes in the mesh tooling review). An
  unrouted-or-unhandled in-process topic behaves exactly as an unrouted-or-unhandled topic on any
  other transport already does.

This trade-off is a deliberate simplification, not an oversight — see
`test/Benzene.Core.Test/Clients/InProcess/InProcessTransportTest.cs`'s
`..._UnhandledTopic_ReturnsNotFoundInsteadOfThrowing` test for the behavior this relies on.

**Update, following named pipelines (`work/inprocess-modular-monolith-scope.md`, Gap 1):** a
narrower, non-reflective version of this check *did* become buildable once pipelines gained names.
The blocker above was specifically "which **topics** use `.UseInProcess()`" — that remains
unknowable without threading the topic through `OutboundRoutingBuilder.Route`'s `configure`
parameter, out of scope here as before. But "which **pipeline names** are referenced by
`.UseInProcess(name)`" needs no reflection: each call now explicitly records an
`InProcessRouteReference(name)` (the same multi-registration idiom `MessageHandlerCandidateTypes`
already uses for discovery diagnostics), and `InProcessRouteStartUpCheck` cross-references those
names against `InProcessDispatcherRegistry`. This catches a typo'd or forgotten pipeline name at
start-up; it does not (and does not claim to) catch a topic with no handler *within* a correctly
named pipeline — that remains the honest `NotFound` at first send, exactly as described above.

### `InProcessSendMessageContext` — mirrors `SqsSendMessageContext`'s shape exactly

Wraps `IBenzeneMessageRequest Request` in, exposes a settable `IBenzeneMessageResponse Response` —
the same shape every other `XxxSendMessageContext` uses, satisfying `OutboundRouteInspector`'s naming
convention with no special-casing needed.

### Naming inconsistency between the two "transport name" reporting surfaces — pre-existing, not new

`OutboundRouteInspector.ToTransportName` naively lowercases the context-converter type name prefix
with no hyphen insertion: `InProcessSendMessageContext` → `"inprocess"`. `TransportNames.InProcess` is
`"in-process"` (hyphenated). These differ. This was flagged as an open question in the original
proposal, but checking `OutboundRouteInspector` shows it is **not a new problem this package
introduces**: `ServiceBusSendMessageContext`/`EventHubSendMessageContext` already produce
`"servicebus"`/`"eventhub"` there, against the hyphenated `TransportNames.ServiceBus =
"service-bus"`/`TransportNames.EventHub = "event-hub"` constants used everywhere else. Two different
reporting surfaces (per-message diagnostics tag vs. descriptor's convention-inferred outbound kind)
already use two different spellings for the same transport, for multiple existing transports. This
package follows the existing, already-accepted precedent rather than inventing new special-casing to
"fix" a pre-existing inconsistency that was out of scope here.

### `IServiceResolverFactory`-based scope isolation

`InProcessClientMiddleware` dispatches via `IServiceResolverFactory`, not the sending call's own
`IServiceResolver` — the dispatched handler gets its own fresh DI scope, the same isolation it would
get if the call really did cross a process boundary. Verified by
`SendAsync_RoutedThroughInProcess_HandlerRunsInItsOwnFreshDiScope`.

### Serialize by default — unchanged from the original proposal

`context.Request` is still serialized to the same JSON `Body` string every other transport uses, and
the handler still deserializes it back out. Kept for the same reason the original proposal gave:
semantic parity with every other transport (same validation/casting/versioning middleware, same
error shapes, no risk of caller and handler sharing a mutable object by reference) is worth more than
the last few microseconds a zero-copy passthrough would save. No passthrough fast path shipped; still
a plausible, explicitly out-of-scope future variant if a concrete measured need shows up.

### `ITransportInfo` is registered after all — the original proposal's caveat didn't survive contact with the interface's actual meaning; no health check

The original proposal held that no `ITransportInfo` should be registered, reasoning that nothing
outside the process can reach this transport, so declaring inbound reachability would be false.
That reasoning doesn't hold up against what `ITransportInfo` actually documents itself as: "a
transport the application can receive messages over" — not a claim of external reachability. The
in-process transport genuinely is one such transport (the service really does receive and dispatch
messages over it, just never across a process boundary), so `AddInProcessMessaging` registers
`ITransportInfo(TransportNames.InProcess)` — its own, distinct from `AddBenzeneMessage()`'s
`ITransportInfo(TransportNames.Benzene)`, so a service that only calls `AddInProcessMessaging()`
never misrepresents itself as exposing that wire endpoint (see "A small, deliberate upstream split"
above). This is accurate, not a gap: the descriptor/mesh should see "in-process" in a service's
transport surface, the same as it sees "sqs" or "http".

No auto-wired health check remains as originally proposed: there is no external dependency to probe.

## What this does not solve

- **Not for cross-process consolidation.** A live `IServiceResolverFactory` reference cannot cross a
  process boundary — two Lambda functions bundled in the same CDK stack are still two processes.
- **No automatic detection.** Moving a handler into the same process does not by itself activate
  `.UseInProcess()` — the outbound route must be explicitly repointed. Auto-detecting "a local handler
  now exists for this topic" and silently short-circuiting would be dangerous for a topic that
  legitimately fans out to multiple consumers, some local and some still remote.
- **No cross-language story.** Single-runtime, in-process only; says nothing about a mixed
  .NET/TypeScript/Go mesh.
- **No per-topic handler-existence startup validation** — see "Startup-time fail-fast
  validation... dropped" above and its follow-up note. Pipeline-*name* validation (a `.UseInProcess`
  route naming a pipeline nothing registered) is now checked at start-up; a route naming a real
  pipeline that lacks a handler for the specific topic is still an honest `NotFound` at first send.
- **No in-process event fan-out.** One event, many in-process reactions (the choreography a real
  SNS topic gives you) has no equivalent here — `.UseInProcess(name)` dispatches to exactly one
  named pipeline. See `work/inprocess-modular-monolith-scope.md` Gap 3.

## Migration shape

1. Move the handler's registration into the caller's own `AddInProcessMessaging(...)` call — named
   (`registry => registry.Add("billing", ...)`) if the caller already hosts other in-process
   modules, unnamed otherwise.
2. Change that one topic's outbound route from `.UseSqs(queueUrl)` (or whatever it was) to
   `.UseInProcess()` (or `.UseInProcess("billing")` for the named case).

No change to the handler's code, no change to the calling code's `SendAsync` call site.

## Testing

`test/Benzene.Core.Test/Clients/InProcess/InProcessTransportTest.cs` covers: end-to-end typed
request/response round-trip, the unhandled-topic-degrades-honestly case, DI scope isolation between
caller and dispatched handler, and that `AddInProcessMessaging` registers its own `ITransportInfo`
rather than `AddBenzeneMessage`'s `"benzene"` one. `test/Benzene.Core.Test/Clients/DefaultBenzeneMessageSenderTest.cs`
gained a test for the new `BenzeneMessageClientResponse` fallback-deserialization branch, independent
of InProcess (it's a generic fix any transport can rely on).

`test/Benzene.Core.Test/Clients/InProcess/InProcessNamedPipelinesTest.cs` (added alongside named
pipelines) covers: two named pipelines in one call dispatching independently, a second top-level
`AddInProcessMessaging` call throwing rather than silently shadowing the first (both when the first
call used the named and the unnamed overload), the same name added twice within one call throwing
and naming the duplicate, `InProcessDispatcherRegistry.Resolve` throwing on an unregistered name,
and `InProcessRouteStartUpCheck`'s three cases (a route naming an unregistered pipeline throws at
start-up; every route naming a registered pipeline passes; no in-process routes at all passes) plus
that the check registers itself alongside the others.
