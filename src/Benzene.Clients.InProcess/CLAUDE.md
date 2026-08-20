# Benzene.Clients.InProcess

## What this package does
An in-process outbound transport: dispatches an outbound send straight to a handler registered in the
same runtime, in the shared `BenzeneMessage` envelope every transport uses, without going over any
wire (no SQS/SNS/HTTP/socket - not even loopback). It exists for the case where functionality that used
to live in a different service has been moved into the caller's own service, and the topic that used to
be sent over a real transport now has no reason to leave the process - see
`work/archive/internal-transport-design-2026-08.md` for the rationale, and the [modular monolith
pattern](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/modular-monolith.md) for
the shape this is written toward: many in-process modules, each with its own pipeline, extracted to
real services one route at a time.

## Key types/interfaces
- `InProcessMessagingBuilder` - accumulates one named pipeline per module within a single
  `AddInProcessMessaging(...)` call via `Add(name, configure)` (and a parameterless `Add(configure)`
  sugar for the single-pipeline case), mirroring how `OutboundRoutingBuilder.Route` accumulates one
  outbound pipeline per topic within a single `AddOutboundRouting(...)` call. `Build()` (internal)
  throws `DuplicateInProcessPipelineException` if the same name is added twice.
- `InProcessDispatcherRegistry` - the built, name-keyed dispatcher set `InProcessMessagingBuilder`
  produces; registered as a single singleton instance. `.UseInProcess(name)` resolves the named
  dispatcher from it at dispatch time via `Resolve(name)`, which throws
  `InProcessPipelineNotFoundException` (listing every registered name) if `name` isn't registered.
- `InProcessSendMessageContext` - the pipeline context wrapping an `IBenzeneMessageRequest Request` and
  a settable `IBenzeneMessageResponse Response`, mirroring `SqsSendMessageContext`'s shape.
- `InProcessContextConverter` - `IContextConverter<OutboundContext, InProcessSendMessageContext>`.
  `CreateRequestAsync` serializes the outbound request into a `BenzeneMessageRequest` (topic, headers,
  JSON body). `MapResponseAsync` sets `OutboundContext.Response` to a raw `BenzeneMessageClientResponse`
  - it does not know the caller's `TResponse`, so it can't produce an already-typed
  `IBenzeneResult<TResponse>` the way SQS's `Void`-only response can. `DefaultBenzeneMessageSender`
  deserializes that raw envelope once it knows `TResponse` (see below).
- `InProcessClientMiddleware` - `IMiddleware<InProcessSendMessageContext>`, `ITerminalMiddleware`.
  Dispatches the request to the specific named `IMiddlewareApplication<IBenzeneMessageRequest,
  IBenzeneMessageResponse>` its `.UseInProcess(name)` call resolved from the registry, giving it a
  **fresh DI scope** via `IServiceResolverFactory` - the same isolation the dispatched handler would
  get if it were invoked from a different process, not the sending call's own scope. The middleware
  class itself is unchanged from the single-pipeline design; only where `Extensions.UseInProcess`
  sources the dispatcher changed (a registry lookup instead of a direct DI resolution).
- `DependencyInjectionExtensions.AddInProcessMessaging` - **two overloads**:
  - `AddInProcessMessaging(services, Action<InProcessMessagingBuilder> configure)` - the
    many-modules-in-one-call shape: `configure` adds one or more named pipelines via the builder.
  - `AddInProcessMessaging(services, Action<IMiddlewarePipelineBuilder<BenzeneMessageContext>> configure)`
    - sugar for the single-pipeline case, registered under `InProcessMessagingBuilder.DefaultName`
    ("default"). Internally just calls the builder overload with one `Add(configure)`.

  Both throw `InProcessMessagingAlreadyRegisteredException` if `AddInProcessMessaging` (either
  overload) was already called on this container - **one call is the contract**, not one call per
  module; see that exception's remarks for why (the container abstraction has no way to fetch a
  previously-registered singleton instance back out during `ConfigureServices` to merge into, only
  `IsTypeRegistered<T>` to detect the second call and reject it). Calls the shared
  `AddBenzeneMessageHandling()` (message extraction/response adaptation) rather than
  `AddBenzeneMessage()`, so it does **not** also announce an `ITransportInfo("benzene")` wire endpoint
  the service doesn't actually expose - it registers its own `ITransportInfo("in-process")` instead,
  once, regardless of how many named pipelines were added.
- `Extensions.UseInProcess(name = InProcessMessagingBuilder.DefaultName)` - the outbound route
  extension: `OutboundRoutingBuilder.Route(topic, p => p.UseInProcess("billing"))` converts that
  route's pipeline to `InProcessSendMessageContext` and terminates it with `InProcessClientMiddleware`
  resolving the named dispatcher from `InProcessDispatcherRegistry`. Also registers an
  `InProcessRouteReference(name)` and (idempotently) `InProcessRouteStartUpCheck`, so every
  `.UseInProcess(...)` call anywhere in the app contributes to the same start-up validation.
- `InProcessRouteReference` - a tiny marker record, one registered per `.UseInProcess(name)` call
  (multi-registered via `AddSingleton` + resolved with `GetServices<T>`, the same idiom
  `MessageHandlerCandidateTypes` uses for discovery diagnostics) - lets the start-up check see every
  referenced pipeline name without reflecting into the built outbound routing table.
- `InProcessRouteStartUpCheck` - `IStartUpCheck` ("in-process-routes"). Cross-references every
  `InProcessRouteReference`'s name against `InProcessDispatcherRegistry.Names` and throws
  `MissingInProcessPipelineException` (listing the missing names and what *is* registered) if a route
  names a pipeline nothing registered. **Deliberately narrower than per-topic handler validation** -
  see its own doc comment and `work/archive/internal-transport-design-2026-08.md`'s follow-up note for why threading
  the topic through would require changing `OutboundRoutingBuilder.Route`'s signature, out of scope
  here.
- `InProcessFanOutTarget(PipelineName, Topic)` / `Extensions.UseInProcessFanOut(params
  InProcessFanOutTarget[] targets)` / `InProcessFanOutClientMiddleware` - one outbound send dispatched
  to several named pipelines **concurrently** (`Task.WhenAll`), each under **its own topic** - not
  the route's literal topic. This is load-bearing, not a style choice: `MessageHandlerDefinitionIndex`
  (`Benzene.Core.MessageHandlers`) is one singleton per `IBenzeneServiceContainer`, shared by every
  named pipeline (they're all built against the same outer container - see "One `AddInProcessMessaging`
  call" below), so Benzene's (topic, version) → at most one handler invariant applies **process-wide**,
  not per pipeline; two targets cannot both register a handler for the literal same topic. Two targets
  naming the same topic throws `DuplicateInProcessFanOutTargetException` immediately, at the
  `.UseInProcessFanOut(...)` call itself (not deferred to the framework's own
  `DuplicateTopicStartUpCheck`, which only catches it if start-up checks actually run). Response
  is unconditionally `IBenzeneResult<Void>` once accepted - matching what a real SNS publish returns,
  no visibility into subscriber outcomes; requesting a non-`Void` response throws
  `OutboundResponseTypeMismatchException`, the same mismatch check SQS/SNS routes already rely on
  (no bespoke Void-enforcement mechanism needed - see `work/archive/inprocess-fanout-design-2026-08.md`'s "shipped"
  section for why). Each target's failure (thrown exception or a non-success status) is isolated -
  logged at `Warning` via `ILogger<InProcessFanOutClientMiddleware>`, but does not fail the other
  targets or the caller. **No in-process DLQ**: a failed target's message is genuinely lost unless
  its own handler retries internally - see the design doc for the full "what this does not solve" list.
  `InProcessRequestBuilder` (shared with `InProcessContextConverter`) builds each target's request
  from the same outbound request/headers with that target's own topic substituted in.

## When to use this package
- A topic used to be sent to another service over a real transport, and that handling has since moved
  into the same runtime as the sender - keep the call shaped as an outbound send (so callers, mesh specs,
  and diagnostics don't need to change), but stop paying serialization/network cost for a hop that no
  longer crosses a process boundary.
- Building a modular monolith on purpose: several modules, each with its own handler assembly and
  middleware stack, registered as named pipelines within one `AddInProcessMessaging(...)` call and
  addressed by topic through the ordinary outbound routing table - see the [modular monolith
  pattern](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/modular-monolith.md).
- Testing: exercising an `IBenzeneMessageSender.SendAsync` call against a real handler pipeline without
  standing up a queue, broker, or HTTP listener.

## Deliberate boundaries (NOT shipped)
- **Not a general request/response invocation shortcut.** This is specifically the outbound-routing
  integration; it does not replace `BenzeneMessageApplication`'s existing direct-invocation use (tests,
  Lambda-to-Lambda invoke) - those callers already have their own `IBenzeneMessageRequest` in hand and
  don't need `OutboundContext`/`IBenzeneMessageSender` in between.
- **No cross-process semantics are simulated.** No serialization round-trip is skipped for perf reasons
  where it's cheap to keep (headers still copy, the body still serializes to JSON) - see "Important
  conventions" below for exactly what is and isn't real here.
- **No per-topic handler-existence start-up validation.** `InProcessRouteStartUpCheck` validates
  that a `.UseInProcess(name)` route names a *pipeline* that was registered; it does not (and
  structurally cannot without a signature change elsewhere - see that check's doc comment) validate
  that the named pipeline actually *handles* the specific topic the route is for. A route naming a
  real, registered pipeline that has no handler for that topic still gets the same honest `NotFound`
  response `MessageRouter` returns for any other unregistered topic, at first send.
- **In-process fan-out (`.UseInProcessFanOut`) requires each target to name its own topic** - see
  the `InProcessFanOutTarget` entry above for why (a process-wide topic-uniqueness constraint, not
  a per-pipeline one). It is not "one topic, several subscribers" the way a real SNS topic is;
  it is "several (pipeline, topic) targets, dispatched from one outbound send."

## Important conventions
- The request body **is still serialized to JSON** on the way in (`InProcessContextConverter.CreateRequestAsync`)
  and the response **is still a raw string envelope** (`BenzeneMessageClientResponse`) on the way out,
  deserialized by `DefaultBenzeneMessageSender` exactly as any other message-client response would be.
  This transport removes the network hop and the transport-specific wire format, not the
  serialize/deserialize step - keeping it means a handler behind `.UseInProcess()` sees exactly the same
  shape of request a real transport would hand it, so switching a topic between this and a real
  transport later is a one-line change, not a rewrite.
- The dispatched handler runs in its **own DI scope** (via `IServiceResolverFactory`), not the caller's
  scope - a scoped dependency resolved by the handler is not the same instance the caller's own scoped
  dependencies are, matching what would happen if the call really did cross a process boundary.
- **One `AddInProcessMessaging(...)` call per container, many named pipelines within it.** A second
  top-level call throws `InProcessMessagingAlreadyRegisteredException` rather than silently shadowing
  the first (which is what plain `AddSingleton` + single-resolution `GetService` would otherwise do -
  the last registration wins and every earlier module's pipeline vanishes from routing with no error).
  Register every module inside one call: `AddInProcessMessaging(registry => registry.Add("billing",
  ...).Add("shipping", ...))`.
- The pipeline name and the `.UseInProcess(name)` name are matched as plain ordinal strings - no
  normalization, no case-insensitivity. `InProcessMessagingBuilder.DefaultName` ("default") is the
  name both the parameterless `AddInProcessMessaging(configure)` and the parameterless
  `.UseInProcess()` use, so the back-compat single-pipeline shape works with no name mentioned on
  either side.
- `TransportNames.InProcess` is `"in-process"` (hyphenated, matching `service-bus`/`queue-storage`).
  `OutboundRouteInspector`'s naive `XxxSendMessageContext → lowercase` convention yields `"inprocess"`
  (no hyphen) for the *same* transport when reporting a route's outbound kind for descriptor emission -
  this is not a new inconsistency this package introduces; `ServiceBusSendMessageContext`/
  `EventHubSendMessageContext` already produce `"servicebus"`/`"eventhub"` there against the hyphenated
  `TransportNames` constants. Two different reporting surfaces, two different spellings, pre-existing.

## Dependencies on other Benzene packages
- **Benzene.Abstractions** / **Benzene.Abstractions.MessageHandlers** - `IContextConverter`,
  `IServiceResolverFactory`, `ITransportInfo`, `TransportNames`, `IStartUpCheck`,
  `TryAddSingletonImplementation<IStartUpCheck,_>`, `IsTypeRegistered<T>`. This also pulls in
  `Microsoft.Extensions.Logging.Abstractions` transitively (for `ILogger<InProcessFanOutClientMiddleware>`)
  - no direct `PackageReference` needed in this project.
- **Benzene.Clients** - `OutboundContext`, `BenzeneMessageClientResponse`, outbound `Convert(...)`
- **Benzene.Core.MessageHandlers** - `AddBenzeneMessageHandling`, `TransportMiddlewarePipeline`
- **Benzene.Core.Messages** - `BenzeneMessageContext`, `BenzeneMessageRequest`, `IBenzeneMessageRequest`/`IBenzeneMessageResponse`
- **Benzene.Core.Middleware** - `MiddlewareApplication`, `MiddlewarePipelineBuilder`
- **Benzene.Results** - `BenzeneResult.Ok<Void>()`, `BenzeneResultStatus.IsSuccess(string)`
