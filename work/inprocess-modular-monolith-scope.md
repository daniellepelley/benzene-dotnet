# In-process transport — remaining scope for the modular-monolith pattern

**Status: SCOPING — nothing here is committed work.** Companion to
`work/internal-transport-design.md` (what shipped as `Benzene.Clients.InProcess`) and to the
cross-language pattern page `docs/patterns/modular-monolith.md` in the benzene repo, which is the
consumer of this scope: that pattern describes building a system as one deliverable whose modules
talk by topic through in-process routes, then extracting modules to services by repointing routes.

## What the shipped package already covers

`Benzene.Clients.InProcess` (landed on main from the original feature branch) covers the pattern's
core mechanic end-to-end:

- `AddInProcessMessaging(pipeline => …)` builds an independent `BenzeneMessageContext` pipeline and
  registers its dispatcher; `.UseInProcess()` terminates an outbound route with it.
- Typed request/response works through the ordinary `IBenzeneMessageSender.SendAsync<TReq, TRes>` —
  the same call site as SQS/SNS/HTTP — via the generic `BenzeneMessageClientResponse` fallback in
  `DefaultBenzeneMessageSender`.
- Each dispatch runs in a fresh DI scope (`IServiceResolverFactory`), matching cross-process
  isolation semantics; payloads serialize by default, so nothing crosses by live reference.
- An in-process route to an unhandled topic degrades to `MessageRouter`'s honest NotFound.

So the headline claim of the pattern — *extraction is a routing-table edit, call sites unchanged* —
is true today for the simple shape: one in-process pipeline, request/response and fire-and-forget,
single runtime.

## Gap 1 — one in-process pipeline per runtime (silent last-wins on a second)

**Problem.** `AddInProcessMessaging` registers its dispatcher as a plain (unkeyed)
`AddSingleton<IMiddlewareApplication<IBenzeneMessageRequest, IBenzeneMessageResponse>>`, and
`.UseInProcess()` resolves it with `GetService<…>()`. Calling `AddInProcessMessaging` twice — the
natural reading of "one pipeline per module, each with its own middleware stack" in the pattern —
does not error: the second registration shadows the first, and every `.UseInProcess()` route
dispatches to whichever pipeline registered last. Modules quietly disappear.

**Proposal, in two steps:**

1. *(small, immediate)* **Fail loudly on the second anonymous registration.** A guard in
   `AddInProcessMessaging` (e.g. `TryAdd` + throw if already present) turns the silent shadowing
   into a startup error with a message pointing at step 2's named form. This is the
   fail-at-boot-not-at-3am posture the outbound router already takes (`MissingOutboundRoutesException`).
2. *(medium)* **Named pipelines.** `AddInProcessMessaging("billing", pipeline => …)` +
   `.UseInProcess("billing")`, so each module keeps its own handler set *and its own middleware
   stack*, and a route names the module it targets. Implementation shape: a keyed registry
   (a `Dictionary<string, IMiddlewareApplication<…>>` behind a small interface — keyed DI
   services would tie this to MS DI specifics; the container abstraction doesn't promise them).
   The parameterless forms stay as the single-pipeline convenience and become sugar for a
   well-known default name.

Until step 2 lands, the documented workaround is one `AddInProcessMessaging` call containing every
module's handlers (module assemblies listed explicitly) — module boundaries hold at the
routing/contract level but per-module middleware isn't expressible.

## Gap 2 — no boot-time "handler exists" validation for in-process routes

**Problem.** The routing table validates *route* existence at startup, but an in-process route
whose topic has no handler on the in-process pipeline is only discovered at first send (as a
NotFound result). `internal-transport-design.md` dropped the original fail-fast proposal because,
at the time, there was no non-reflective signal of *which topics* route in-process.

**Proposal.** The signal can be created rather than discovered: `OutboundRoutingBuilder.Route`
knows its topic, so `.UseInProcess()` (or the `Route` overload it sits in) can record
`(topic, pipelineName)` pairs into a small registration the existing startup-check machinery
(`OutboundRoutingStartUpCheck` / `ValidateOutboundRoutingExtensions`) consumes: for each recorded
topic, assert the named in-process pipeline's handler registry can route it. In-process is the one
transport where this check is *possible* — the "callee" is in the same container — so the pattern's
"a missing route is a deploy-time error" property can extend to "a missing handler is a boot-time
error" with no reflection. *(medium; depends on Gap 1's naming for the pipeline lookup)*

## Gap 3 — no in-process event fan-out

**Problem.** A modular monolith also choreographs: one module raises `order:created`, several
modules react. Over the wire that's SNS fan-out; in process there is no equivalent — a topic maps
to at most one handler per pipeline, and one `.UseInProcess()` route reaches one pipeline. Today
the pattern's choreography story only starts *after* extraction.

**Proposal.** An explicit fan-out route — e.g. `.UseInProcessFanOut("billing", "shipping", …)` or
`.UseInProcess()` against multiple named pipelines — with SNS-matching semantics: `Void` responses,
each consumer dispatched in its own scope, per-consumer failure isolated (one failing reaction
doesn't fail the others; statuses aggregated or logged, mirroring how SNS delivery failures behave).
Extraction symmetry is the design constraint: moving one consumer out should be "remove its name
from the fan-out list, keep the SNS route", with no emitter code change. *(medium-large; wants
Gap 1 first; semantics need a short design note of their own before code)*

## Gap 4 — .NET-only; the pattern is cross-language

`docs/patterns/modular-monolith.md` is language-neutral, but only the .NET port has an in-process
transport. Two pieces:

- *(per-port, medium each)* Go/TypeScript/Python equivalents of `AddInProcessMessaging` /
  `.UseInProcess()` — each port already has the two ingredients (an in-process pipeline invocation
  path and an outbound sender), so this is composition, not new architecture.
- *(small, benzene repo)* An informative note in the spec's porting guide naming the in-process
  transport as a recommended port capability with its required semantics (explicit per-topic opt-in,
  fresh scope per dispatch, serialize by default, honest NotFound degradation) — so ports converge
  on the same shape instead of re-deriving it.

## Housekeeping

- `internal-transport-design.md` says "no `ITransportInfo`… held as originally proposed" while the
  shipped `AddInProcessMessaging` *does* register `ITransportInfo(TransportNames.InProcess)` (and a
  test asserts it). The code is right — the registration describes the transport surface honestly —
  so fix the stale paragraph in the design doc. *(trivial)*

## Suggested order

1. Gap 1 step 1 (loud failure on double registration) — smallest, removes the silent-loss trap.
2. Gap 1 step 2 (named pipelines) — unlocks per-module middleware and Gaps 2–3.
3. Gap 2 (boot-time handler validation) — restores the pattern's validated-at-startup promise.
4. Gap 3 (fan-out) — completes the in-monolith choreography story.
5. Gap 4 in parallel per port, once 1–3 settle the .NET shape worth copying.
