# In-process transport — remaining scope for the modular-monolith pattern

**Status: Gaps 1 and 2 shipped; Gap 3 is a design proposal, not yet built; Gap 4 remains open.**
Companion to `work/internal-transport-design.md` (the original `Benzene.Clients.InProcess`) and to
the cross-language pattern page
[`docs/patterns/modular-monolith.md`](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/modular-monolith.md)
in the benzene repo, which is the consumer of this scope: that pattern describes building a system
as one deliverable whose modules talk by topic through in-process routes, then extracting modules to
services by repointing routes.

## What the shipped package already covers

`Benzene.Clients.InProcess` covers the pattern's core mechanic end-to-end:

- Typed request/response works through the ordinary `IBenzeneMessageSender.SendAsync<TReq, TRes>` —
  the same call site as SQS/SNS/HTTP — via the generic `BenzeneMessageClientResponse` fallback in
  `DefaultBenzeneMessageSender`.
- Each dispatch runs in a fresh DI scope (`IServiceResolverFactory`), matching cross-process
  isolation semantics; payloads serialize by default, so nothing crosses by live reference.
- An in-process route to a registered pipeline with no handler for the specific topic degrades to
  `MessageRouter`'s honest NotFound.
- **Named pipelines** (Gap 1, below): `AddInProcessMessaging(registry => registry.Add("billing",
  ...).Add("shipping", ...))`, each with its own handler assembly and middleware stack, routed to
  independently via `.UseInProcess("billing")` / `.UseInProcess("shipping")`.
- **Boot-time pipeline-name validation** (Gap 2, below): a `.UseInProcess(name)` route naming a
  pipeline nothing registered fails at start-up, not first send.

So the headline claim of the pattern — *extraction is a routing-table edit, call sites unchanged* —
is true today for a service with several in-process modules, not just the single-pipeline case.

## Gap 1 — one in-process pipeline per runtime (silent last-wins on a second) — SHIPPED

**Problem (as originally found).** `AddInProcessMessaging` registered its dispatcher as a plain
(unkeyed) `AddSingleton<IMiddlewareApplication<...>>`, and `.UseInProcess()` resolved it with
`GetService<...>()`. Calling `AddInProcessMessaging` twice did not error: the second registration
shadowed the first, and every `.UseInProcess()` route dispatched to whichever pipeline registered
last. Modules quietly disappeared.

**What shipped.** Not the originally-sketched two-step plan (a bare guard, then a separate
keyed-registry retrofit) — a single combined design, once the container abstraction's actual
constraints were worked through (see `InProcessMessagingAlreadyRegisteredException`'s remarks for
why "just fetch the existing singleton and merge into it" isn't available here):

- **`InProcessMessagingBuilder`** accumulates one named pipeline per module *within a single
  `AddInProcessMessaging(...)` call*, mirroring `OutboundRoutingBuilder.Route`'s existing
  accumulate-in-one-call shape (`AddOutboundRouting(routing => routing.Route(...).Route(...))`).
  `Add(name, configure)` per module; `Add(configure)` (no name) is sugar for the single-pipeline
  case, registered under `InProcessMessagingBuilder.DefaultName`.
- **One call is now the enforced contract, not a convention.** A *second top-level*
  `AddInProcessMessaging(...)` call (either overload) throws
  `InProcessMessagingAlreadyRegisteredException` immediately (checked via
  `IsTypeRegistered<InProcessDispatcherRegistry>()`) — the fail-at-boot-not-at-3am posture the
  outbound router already has for missing routes, applied to the "silently shadowed pipeline"
  mistake instead.
- **`InProcessDispatcherRegistry`** is the built name → dispatcher map, one singleton *instance*
  (not multiple competing registrations), with `Resolve(name)` throwing
  `InProcessPipelineNotFoundException` (listing every registered name) for an unknown name.
- Adding the same name twice *within* one call throws `DuplicateInProcessPipelineException`,
  mirroring `DuplicateOutboundRouteException`.

Tests: `test/Benzene.Core.Test/Clients/InProcess/InProcessNamedPipelinesTest.cs`.

## Gap 2 — no boot-time "pipeline exists" validation for in-process routes — SHIPPED (narrower than originally scoped)

**Problem (as originally found).** The routing table validates *route* existence at startup, but an
in-process route whose topic has no handler was only discovered at first send.

**What actually shipped, and why it's narrower.** The original proposal ("record `(topic,
pipelineName)` pairs... assert the named pipeline's handler registry can route it") turned out not
to be buildable without a real cost that wasn't visible until reading `OutboundRoutingBuilder.Route`
closely: **`configure` never receives the topic** —
`Route(string topic, Action<IMiddlewarePipelineBuilder<OutboundContext>> configure)` calls
`configure(builder)` with no reference to `topic`. So `.UseInProcess(name)`, running inside that
lambda, structurally cannot know which topic it's routing for without a signature change to
`Route` itself — a change used by every outbound route in every transport, out of scope for this
package. (This is also why the older `OutboundRouteInspector` in `Benzene.Descriptor` resorts to
reflecting into the *built* routing table after the fact, and is explicitly marked SPIKE-GRADE,
best-effort, degrade-to-"unknown" — the wrong foundation for a check that's supposed to throw.)

What **is** knowable without any of that: which **pipeline names** a route referenced. Shipped:

- **`InProcessRouteReference(name)`** — one registered per `.UseInProcess(name)` call, via the same
  multi-registration idiom `MessageHandlerCandidateTypes` already uses (`AddSingleton` +
  `GetServices<T>()`, not `GetService<T>()`).
- **`InProcessRouteStartUpCheck`** (`IStartUpCheck`, `"in-process-routes"`) — cross-references every
  referenced name against `InProcessDispatcherRegistry.Names` and throws
  `MissingInProcessPipelineException` (naming every missing name and every registered one) if a
  route names a pipeline nothing registered. Registered idempotently by every `.UseInProcess(...)`
  call, so it fires regardless of call order.

**What this does not do:** validate that the named pipeline, once confirmed to exist, actually
handles the *specific topic* the route is for. That remains the honest `NotFound` at first send,
exactly as before. Full per-topic validation is still possible in principle, but needs
`OutboundRoutingBuilder.Route`'s signature changed to thread the topic through — a larger, more
invasive change than this package should make unilaterally.

Tests: same file as Gap 1, covering both the missing-name-throws and every-name-registered-passes
cases, plus that the check registers itself alongside the others.

## Gap 3 — no in-process event fan-out — SHIPPED (signature corrected from the original design)

**Problem.** A modular monolith also choreographs: one module raises `order:created`, several
modules react. Over the wire that's SNS fan-out; in process there is no equivalent —
`.UseInProcess(name)` reaches exactly one named pipeline. Today the pattern's choreography story
only starts *after* extraction.

**What shipped, and the correction found while implementing.** A full design proposal existed
before any code (`work/inprocess-fanout-design.md`), the same discipline
`internal-transport-design.md` followed for the single-target transport. Most of it shipped
unchanged: `Task.WhenAll` concurrent dispatch, per-consumer try/catch + `Warning`-level logging,
unconditional `Void` success response, no in-process DLQ (documented, not solved). One part of the
proposal was wrong and had to be corrected during implementation, caught by the *first* test written
against it: the proposed `.UseInProcessFanOut("billing", "shipping", "analytics")` (bare pipeline
names, all dispatched under the route's own topic) assumed each named pipeline could independently
register a handler for that same topic. It cannot — `MessageHandlerDefinitionIndex` is one singleton
per `IBenzeneServiceContainer`, shared by every named pipeline (they're built against the same outer
container so they can share the app's cross-cutting services), so Benzene's (topic, version) → at
most one handler invariant applies **process-wide**, not per pipeline. Two pipelines both trying to
own `"order:created"` either fails a start-up check that might not be running, or - what actually
happened in the failing test - silently resolves to whichever handler won an internal `GroupBy(...)
.First()`, so *both* "subscribers" silently invoke the *same* handler.

**Shipped instead:** `.UseInProcessFanOut(params InProcessFanOutTarget[] targets)` where each
`InProcessFanOutTarget(PipelineName, Topic)` carries its *own* topic — e.g.
`UseInProcessFanOut(new("billing", "billing:order-created"), new("shipping", "shipping:order-created"))`.
Two targets sharing a topic now throws `DuplicateInProcessFanOutTargetException` immediately, at
the `.UseInProcessFanOut(...)` call itself — tighter than relying on the framework's own
`DuplicateTopicStartUpCheck`, which only catches it if start-up checks are actually run (they
weren't, in the test that found this). The proposal's other open question — how to enforce
Void-only responses — turned out to already be solved: `OutboundResponseTypeMismatchException`,
the same mismatch check `DefaultBenzeneMessageSender` already applies to SQS/SNS routes, needed no
new mechanism at all.

Tests: `test/Benzene.Core.Test/Clients/InProcess/InProcessFanOutTest.cs`. Full detail (including the
"what this does not solve" list, unchanged from the proposal) in `work/inprocess-fanout-design.md`'s
"What shipped, and where it diverges" section and `src/Benzene.Clients.InProcess/CLAUDE.md`.

## Gap 4 — .NET-only; the pattern is cross-language — investigated, in progress

`docs/patterns/modular-monolith.md` is language-neutral, but only the .NET port has an in-process
transport. Architecture investigation across all three sibling ports found a consistent, important
asymmetry: **Go and Python have no topic→destination outbound routing table at all** — every sender
is bound to one destination at construction (`SqsMessageSender(queueUrl)`,
`awssqs.Client`), so ".NET's `.UseInProcess(name)` sugar over a routing table" has no routing table
to sit inside for those two ports. Both already have the harder half of the feature, though: a
direct in-process invocation path (Go's `benzenetest.Invoke`, Python's
`BenzeneMessageApplication.handle`) with per-dispatch scoping already built and used by their own
test hosts — it just isn't exposed as an addressable `Sender`/`MessageSender` today. TypeScript is
the closest match to .NET: it already has a real outbound routing table
(`OutboundRoutingBuilder`/`addOutboundRouting`) and the same `.convert()` context-conversion
extension point .NET's `InProcessContextConverter` uses, so a structurally-equivalent port is
mechanical there. Its one gap is the *boot-time validation* half: no `IStartUpCheck`-equivalent
runner exists in TS at all.

- *(per-port, scoped individually below)* Go/TypeScript/Python equivalents of
  `AddInProcessMessaging` / `.UseInProcess()` / (TS only) `.UseInProcessFanOut()`.
- *(small, benzene repo)* An informative note in the spec's porting guide naming the in-process
  transport as a recommended port capability with its required semantics (explicit per-topic opt-in,
  fresh scope per dispatch, serialize by default, honest NotFound degradation, one registration call
  with named pipelines, boot-time pipeline-name validation, and fan-out's per-target-topic
  requirement) — so ports converge on the same shape instead of re-deriving it.

## Housekeeping — DONE

- `internal-transport-design.md`'s stale "no `ITransportInfo`... held as originally proposed"
  paragraph (the shipped code registers one) has been corrected, with a note on why the original
  reasoning didn't actually hold once checked against what `ITransportInfo` documents itself as.
- Its "Startup-time fail-fast validation... dropped" section has a follow-up note explaining what
  became buildable once pipelines gained names (Gap 2) and what's still genuinely out of reach.
- Its "What this does not solve" list and "Migration shape" section are updated for named pipelines
  and the fan-out gap.

## Suggested order (updated)

1. ~~Gap 1 (named pipelines, loud failure on double registration)~~ — done.
2. ~~Gap 2 (boot-time pipeline-name validation)~~ — done, narrower than originally scoped; the
   full per-topic version stays open pending an `OutboundRoutingBuilder.Route` signature change.
3. ~~Gap 3 (fan-out)~~ — done; signature corrected from the original design (per-target topics,
   not bare pipeline names) after the first test against it found the process-wide topic-uniqueness
   constraint.
4. Gap 4 per port — architecture investigated for all three; TypeScript ports the .NET shape most
   directly (it already has a routing table); Go and Python need a minimal sender + named-pipeline
   registry composed with whatever destination-binding convention each already uses, since neither
   has a routing table to hook into yet. Port the *named* shape, not the original single-pipeline
   one, in every case.
   shape, not the original single-pipeline one.
