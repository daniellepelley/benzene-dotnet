# Benzene.Clients.InProcess

## What this package does
An in-process outbound transport: dispatches an outbound send straight to a handler registered in the
same runtime, in the shared `BenzeneMessage` envelope every transport uses, without going over any
wire (no SQS/SNS/HTTP/socket - not even loopback). It exists for the case where functionality that used
to live in a different service has been moved into the caller's own service, and the topic that used to
be sent over a real transport now has no reason to leave the process - see
`work/internal-transport-design.md` for the rationale and the option this deliberately does not try to
be a default.

## Key types/interfaces
- `InProcessSendMessageContext` - the pipeline context wrapping an `IBenzeneMessageRequest Request` and
  a settable `IBenzeneMessageResponse Response`, mirroring `SqsSendMessageContext`'s shape.
- `InProcessContextConverter` - `IContextConverter<OutboundContext, InProcessSendMessageContext>`.
  `CreateRequestAsync` serializes the outbound request into a `BenzeneMessageRequest` (topic, headers,
  JSON body). `MapResponseAsync` sets `OutboundContext.Response` to a raw `BenzeneMessageClientResponse`
  - it does not know the caller's `TResponse`, so it can't produce an already-typed
  `IBenzeneResult<TResponse>` the way SQS's `Void`-only response can. `DefaultBenzeneMessageSender`
  deserializes that raw envelope once it knows `TResponse` (see below).
- `InProcessClientMiddleware` - `IMiddleware<InProcessSendMessageContext>`, `ITerminalMiddleware`.
  Dispatches the request to the `IMiddlewareApplication<IBenzeneMessageRequest, IBenzeneMessageResponse>`
  registered by `AddInProcessMessaging`, giving it a **fresh DI scope** via `IServiceResolverFactory` -
  the same isolation the dispatched handler would get if it were invoked from a different process, not
  the sending call's own scope.
- `DependencyInjectionExtensions.AddInProcessMessaging(services, configure)` - builds the in-process
  `BenzeneMessage` pipeline (`configure` typically calls `.UseMessageHandlers(...)`), tags it with the
  `"in-process"` transport name (`TransportNames.InProcess`) via `TransportMiddlewarePipeline`, and
  registers the resulting dispatcher as `IMiddlewareApplication<IBenzeneMessageRequest, IBenzeneMessageResponse>`.
  Calls the shared `AddBenzeneMessageHandling()` (message extraction/response adaptation) rather than
  `AddBenzeneMessage()`, so it does **not** also announce an `ITransportInfo("benzene")` wire endpoint
  the service doesn't actually expose - it registers its own `ITransportInfo("in-process")` instead.
- `Extensions.UseInProcess()` - the outbound route extension: `OutboundRoutingBuilder.Route(topic, p =>
  p.UseInProcess())` converts that route's pipeline to `InProcessSendMessageContext` and terminates it
  with `InProcessClientMiddleware`.

## When to use this package
- A topic used to be sent to another service over a real transport, and that handling has since moved
  into the same runtime as the sender - keep the call shaped as an outbound send (so callers, mesh specs,
  and diagnostics don't need to change), but stop paying serialization/network cost for a hop that no
  longer crosses a process boundary.
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
- **No startup-time validation that every `.UseInProcess()` topic has a matching in-process handler.**
  An unrouted or unhandled topic gets the same honest `NotFound`-shaped response `MessageRouter` already
  returns for any other unregistered topic, rather than a dedicated fail-fast check - see
  `work/internal-transport-design.md` for why a dedicated startup check was considered and dropped.

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
- `TransportNames.InProcess` is `"in-process"` (hyphenated, matching `service-bus`/`queue-storage`).
  `OutboundRouteInspector`'s naive `XxxSendMessageContext → lowercase` convention yields `"inprocess"`
  (no hyphen) for the *same* transport when reporting a route's outbound kind for descriptor emission -
  this is not a new inconsistency this package introduces; `ServiceBusSendMessageContext`/
  `EventHubSendMessageContext` already produce `"servicebus"`/`"eventhub"` there against the hyphenated
  `TransportNames` constants. Two different reporting surfaces, two different spellings, pre-existing.

## Dependencies on other Benzene packages
- **Benzene.Abstractions** / **Benzene.Abstractions.MessageHandlers** - `IContextConverter`,
  `IServiceResolverFactory`, `ITransportInfo`, `TransportNames`
- **Benzene.Clients** - `OutboundContext`, `BenzeneMessageClientResponse`, outbound `Convert(...)`
- **Benzene.Core.MessageHandlers** - `AddBenzeneMessageHandling`, `TransportMiddlewarePipeline`
- **Benzene.Core.Messages** - `BenzeneMessageContext`, `BenzeneMessageRequest`, `IBenzeneMessageRequest`/`IBenzeneMessageResponse`
- **Benzene.Core.Middleware** - `MiddlewareApplication`, `MiddlewarePipelineBuilder`
