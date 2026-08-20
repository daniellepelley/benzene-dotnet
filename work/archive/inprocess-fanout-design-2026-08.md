> ARCHIVED 2026-08-20: actioned; shipped as `InProcessFanOutClientMiddleware` in `src/Benzene.Clients.InProcess`.

# In-process fan-out — design + what shipped

**Status: Implemented (`InProcessFanOutClientMiddleware`, `.UseInProcessFanOut`).** This document
started as a design proposal before any code existed - the same way `internal-transport-design-2026-08.md`
itself did for the single-target transport, because fan-out introduces genuinely new semantics
(partial failure across multiple targets, no redelivery) that deserved scrutiny before code. It has
been updated in place to describe what was actually built, and to record the one place
implementation diverged from the proposal below - not a refinement, but a **correction**: the
original proposal's `.UseInProcessFanOut("billing", "shipping", "analytics")` signature (pipeline
names only) turned out to be unbuildable as specified. See "What shipped, and where it diverges"
after the original proposal.

## The ask

A modular monolith also choreographs, not just calls: one module raises `order:created`, several
modules react - the in-monolith equivalent of one SNS topic fanning out to several SQS-subscribed
consumers (see the [modular monolith pattern](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/modular-monolith.md)
this is written toward). Today `.UseInProcess(name)` dispatches to exactly **one** named pipeline; there
is no way to route one outbound topic to several in-process modules at once without leaving the
process (a real SNS topic, even when every subscriber happens to be co-located).

## Why this needs its own design pass, not just an extension method

Every route conversion shipped so far (`UseSqs`, `UseSns`, `UseInProcess`) is fundamentally a
**single-destination** delivery: one route, one target, one response mapped back to the caller.
Fan-out introduces three semantic questions none of those had to answer:

1. What does the caller's response mean when there are N deliveries, not one?
2. What happens to a consumer that throws or returns a failure status?
3. What happens to a message a failed consumer never successfully processed - is it retried? Lost?
   (Real SNS+SQS answers this with a DLQ and redelivery policy; in-process has no such
   infrastructure standing behind it.)

The goal below is to answer all three **by analogy to what SNS→SQS actually does**, not by
inventing new semantics - consistent with the wider transport's own design principle (serialize by
default, fresh scope per dispatch: "the same shape a real transport would give you, not a shortcut
around it").

## Proposed shape

A new route conversion, sibling to `.UseInProcess(name)`:

```csharp
services.AddOutboundRouting(routing => routing
    .Route("order:created", pipeline => pipeline.UseInProcessFanOut("billing", "shipping", "analytics")));
```

Each name is resolved from the **same** `InProcessDispatcherRegistry` that `.UseInProcess(name)`
already uses - no new registration mechanism, just a route that targets several already-registered
pipelines instead of one. Removing "analytics" from that list is the entire code change to stop
analytics reacting to `order:created`; extracting "analytics" to a real service later is the entire
code change to make it one (drop it from the fan-out list, add a real `UseSns` route for it
alongside the trimmed in-process fan-out) - the same one-route-at-a-time extraction symmetry the
rest of this package is built around.

## Semantics, answered by analogy to SNS→SQS

1. **The response is fire-and-forget, unconditionally successful once accepted.** Real
   `SnsClientMiddleware` returns success once SNS accepts the publish call - it has no visibility
   into subscriber outcomes, and neither should this. `.UseInProcessFanOut(...)` is Void-response-only
   (matching "a `Void` response ⇒ fire-and-forget" in `docs/patterns/service-communication.md`); a
   route declared against a non-`Void` response type is a configuration error (see "Open question"
   below for how to catch it).

2. **Each consumer's failure is isolated from the others' and from the caller.** Dispatch to all N
   named pipelines concurrently (`Task.WhenAll` over N independent, `IServiceResolverFactory`-scoped
   calls - the same per-dispatch isolation `InProcessClientMiddleware` already gives a single
   target), each wrapped in its own try/catch. One consumer throwing or returning a non-success
   status does not affect the others' delivery or the caller's response.

3. **Failures are logged, not silently dropped - and this is the one place with no SNS+SQS
   analogy to lean on.** There is no in-process DLQ. A message a failing consumer never
   successfully processed is genuinely lost, unless the consumer's own handler is written to
   retry internally, or the topic is one where losing an occasional reactive delivery is
   acceptable (the classic fan-out use case: warm a cache, send a non-critical notification -
   nothing the process depends on for correctness). `InProcessFanOutClientMiddleware` resolves
   `ILogger<InProcessFanOutClientMiddleware>` and logs each consumer's failure at `Warning`, naming
   the pipeline and the topic - matching `MessageRouter`'s own "a baseline failure signal even when
   no logging middleware is wired" precedent, so a failure is at minimum visible in logs even though
   nothing retries it.

## What this deliberately does not solve (said up front, not discovered later)

- **No redelivery or DLQ for a failed in-process consumer.** If a topic's fan-out reactions must
  not be lost on failure, in-process fan-out is the wrong tool for that topic - route it over a real
  SNS/SQS fan-out instead. This is a real, load-bearing limitation, not a corner case: it is the
  entire reason rule 5 of the modular-monolith pattern ("consumers are idempotent, eventually")
  matters more once a topic fans out in-process than it does for a single `.UseInProcess(name)` call,
  which at least degrades to an honest `NotFound` the caller can see.
- **No ordering guarantee beyond what `Task.WhenAll` gives you** - unordered, concurrent delivery.
  A consumer that must see one entity's events in order is the wrong fit for fan-out at all,
  in-process or over SNS; this design does not attempt to fix that.
- **No partial-list start-up validation beyond what `InProcessRouteStartUpCheck` already does per
  name.** Each name in the fan-out list becomes its own `InProcessRouteReference`, so the existing
  check validates every one of them for free - no changes needed to the check itself.

## Estimated shape of the change

- `InProcessFanOutClientMiddleware` (new, roughly the size of `InProcessClientMiddleware`):
  resolves N dispatchers from the registry, `Task.WhenAll`, try/catch + log per consumer, returns a
  fixed Void-success response regardless of individual outcomes.
- `.UseInProcessFanOut(params string[] names)` (new, in `Extensions.cs`): registers one
  `InProcessRouteReference` per name (no changes needed to `InProcessRouteStartUpCheck` - it already
  validates every reference regardless of which route added it) and converts/uses the new
  middleware.
- **Open question, to resolve before writing code, not while writing it:** how to enforce
  Void-only. Unlike single-target `.UseInProcess(name)`, which can return any typed response,
  fan-out's multi-target nature makes a non-`Void` response meaningless - there is no single
  response to return. Two options: (a) a start-up check (mirroring `TerminalMiddlewareStartUpCheck`'s
  pipeline-shape-mistake pattern) that inspects the route's declared response type; or (b) a
  differently-typed entry point that only compiles against `Void` routes, if that's achievable
  without contorting `OutboundRoutingBuilder.Route`'s existing generic shape. Spike both before
  committing to one - (a) is cheaper to build but is a runtime catch for what's really a compile-time
  fact; (b) is more honest but may not fit the existing builder's generics cleanly.

## Explicit note for whoever implements this

**`Task.WhenAll`, not sequential dispatch, was chosen deliberately** to match SNS's own actual
delivery model - concurrent, independent, no consumer blocks another. Don't "simplify" this to a
sequential loop during implementation; that would quietly change the isolation guarantee described
in semantics point 2 above (a slow or hanging consumer would then delay every consumer after it in
the list, which SNS fan-out never does).

This turned out to matter less than the correction below - `Task.WhenAll` shipped exactly as
specified, no simplification attempted.

## What shipped, and where it diverges from the original proposal

### The pipeline-names-only signature was wrong - discovered by a failing test, not by inspection

The proposal above assumed `.UseInProcessFanOut("billing", "shipping", "analytics")` - a pipeline
name per target, all dispatched under the route's own topic - was the whole shape. It compiled,
looked reasonable, and was wrong: the first test written against it
(`SendAsync_RoutedThroughFanOut_...`, registering `BillingHandler` for `"order:created"` in the
`"billing"` pipeline and `ShippingHandler` for the *same* `"order:created"` in the `"shipping"`
pipeline) failed with both dispatches landing on the same handler.

The reason is a fact about the framework this document did not check before proposing the
signature: **`MessageHandlerDefinitionIndex` (`Benzene.Core.MessageHandlers`) is one singleton per
`IBenzeneServiceContainer`, aggregating every registered `IMessageHandlersFinder`'s definitions
across the *entire* container** - not one index per pipeline. Every named pipeline
`InProcessMessagingBuilder.Add(name, configure)` builds is constructed against the *same* outer
`_benzeneServiceContainer` (deliberately - so a pipeline's handlers can resolve the app's other
cross-cutting services, like a DB connection, without re-registering them per pipeline). That
sharing is real and desirable for cross-cutting services, but it has a consequence for topics: two
named pipelines cannot each register a handler for the literal same topic id + version, because
`DuplicateTopicStartUpCheck` (part of the *core* message-handling package, unrelated to InProcess)
treats that as a startup error, and short of that check running, `MessageHandlerDefinitionIndex`
resolves the ambiguity to whichever definition happened to win the `GroupBy(...).First()` - silently,
not an error. This is exactly what the failing test hit: nothing ran the startup check, so the
"two handlers, one topic" mistake surfaced as a silent misroute instead of a clear failure.

This is not a bug to route around with more InProcess-package machinery - it is the framework's
process-wide topic model working as documented (core-concepts.md §2: "a (topic id, version) pair
maps to **at most one** handler"), applied honestly to the fact that every in-process pipeline in
one service shares one process. Real SNS fan-out doesn't hit this because each subscriber is its
own process with its own topic namespace that merely *happens* to reuse the same topic string; two
in-process pipelines in the same `IBenzeneServiceContainer` do not get that isolation for free.

### What shipped instead: per-target topics, not a shared one

`.UseInProcessFanOut(...)` takes **`InProcessFanOutTarget(string PipelineName, string Topic)`**
tuples, not bare pipeline names. Each target dispatches under *its own* topic - e.g.
`UseInProcessFanOut(new("billing", "billing:order-created"), new("shipping", "shipping:order-created"))`
- so each target's handler is free to register under a topic that doesn't collide with any other
target's, exactly the same way a real, separately-deployed subscriber would name its own internal
handler however it likes. `InProcessRequestBuilder` (new, shared with `InProcessContextConverter`)
builds a `BenzeneMessageRequest` per target from the same outbound request/headers but the target's
own topic.

**`.UseInProcessFanOut(...)` now also validates eagerly, at the route-construction call itself,
that no two targets in the same call share a topic** (`DuplicateInProcessFanOutTargetException`) -
tighter than relying solely on `DuplicateTopicStartUpCheck`, which only fires if start-up checks
actually run. This was added specifically because the failing test that found this whole issue had
start-up checks disabled (implicitly, by never calling them) and got a silent misroute instead of
any error at all - the fan-out route itself is now the first line of defense, checked whether or
not the app ever runs `RunStartUpChecks()`.

### The Void-only open question resolved itself

The proposal's open question - how to enforce that a fan-out route can only be sent via
`SendAsync<TRequest, Void>` - turned out to already have a shipped, established answer once the
codebase was searched properly: `OutboundResponseTypeMismatchException`
(`Benzene.Clients/OutboundResponseTypeMismatchException.cs`), thrown by
`DefaultBenzeneMessageSender.SendAsync<TRequest,TResponse>` whenever a route's response doesn't
match the caller's requested `TResponse`. SQS and SNS - the two other transports with no real
response beyond an acknowledgement - already rely on exactly this: their outbound context
converters *always* set an `IBenzeneResult<Void>` response regardless of what `TResponse` the
caller asked for, and the mismatch is caught generically, at the sender, not per-transport. There
was no need to invent (a) a start-up check or (b) a differently-typed builder entry point - fan-out
follows the exact same established pattern: `InProcessFanOutClientMiddleware.HandleAsync` always
sets `context.Response = BenzeneResult.Ok<Void>()`, and a caller requesting a non-`Void` response
gets the same `OutboundResponseTypeMismatchException` SQS/SNS callers already get.

### Everything else shipped as proposed

`Task.WhenAll` (not sequential), per-consumer try/catch with `Warning`-level logging naming the
pipeline and topic, no in-process DLQ (documented, not solved), no ordering guarantee beyond
`Task.WhenAll`'s. See `src/Benzene.Clients.InProcess/CLAUDE.md` for the shipped API reference.

## Testing

`test/Benzene.Core.Test/Clients/InProcess/InProcessFanOutTest.cs` covers: dispatch to every target
under its own topic, one consumer throwing does not affect the others or the caller, one consumer
returning a failure status does not affect the others or the caller, requesting a non-`Void`
response throws the same mismatch exception SQS/SNS callers get, an empty target list throws, two
targets sharing a topic throws `DuplicateInProcessFanOutTargetException` naming the topic, and a
target naming an unregistered pipeline throws the same `InProcessPipelineNotFoundException` a
single-target `.UseInProcess(name)` would.
