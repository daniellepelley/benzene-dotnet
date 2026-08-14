# Message Handlers

Message handlers are the components that receive and process a single message. There should be
exactly one message handler per topic your service handles. The topic (and the request/response
types) form the front-facing contract for the service — they're what's used to generate OpenAPI /
AsyncAPI documentation, client code, etc. (see [Spec](spec.md)).

Handlers support constructor dependency injection, so keep the handler itself thin and push
business logic into an injected service.

## `IMessageHandler<TRequest, TResponse>` / `IMessageHandler<TRequest>`

Defined in `Benzene.Abstractions.MessageHandlers`:

```csharp
public interface IMessageHandler<TRequest, TResponse>
    : IMessageHandlerBase<TRequest, TResponse>
{}

public interface IMessageHandlerBase<TRequest, TResponse>
{
    Task<IBenzeneResult<TResponse>> HandleAsync(TRequest request);
}

public interface IMessageHandler<TRequest>
{
    Task HandleAsync(TRequest request);
}
```

- Use `IMessageHandler<TRequest, TResponse>` for request/response handlers — `HandleAsync` returns
  the response wrapped in an `IBenzeneResult<TResponse>`.
- Use `IMessageHandler<TRequest>` for fire-and-forget handlers with no meaningful response.
  Internally, `MessageHandlerNoResultWrapper<TRequest, TResponse>` wraps it so it still fits the
  request/response shape; the wrapper always returns `BenzeneResult.Accepted<TResponse>()` once
  your handler's `HandleAsync` completes — this is why a no-response handler always reports back an
  "accepted" result.

See [Message Results](message-result.md) for everything about `IBenzeneResult<T>` and the available
status factories — this page doesn't repeat that detail.

### Request / response example

```csharp
[HttpEndpoint("POST", "/orders")]
[Message("order:create")]
public class CreateOrderMessageHandler : IMessageHandler<CreateOrderMessage, OrderDto>
{
    private readonly IOrderService _orderService;

    public CreateOrderMessageHandler(IOrderService orderService)
    {
        _orderService = orderService;
    }

    public async Task<IBenzeneResult<OrderDto>> HandleAsync(CreateOrderMessage request)
    {
        return await _orderService.SaveAsync(request);
    }
}
```

### Fire-and-forget (no response) example

```csharp
[Message("order:archive")]
public class ArchiveOrderMessageHandler : IMessageHandler<ArchiveOrderMessage>
{
    private readonly IOrderService _orderService;

    public ArchiveOrderMessageHandler(IOrderService orderService)
    {
        _orderService = orderService;
    }

    public async Task HandleAsync(ArchiveOrderMessage request)
    {
        await _orderService.ArchiveAsync(request);
    }
}
```

## `[Message("topic")]`

Defined in `Benzene.Core.MessageHandlers`:

```csharp
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class MessageAttribute : Attribute
{
    public MessageAttribute(string topic, string version = "");

    public string Version { get; }
    public string Topic { get; }
}
```

Applied once per handler class. `Topic` is the routing key used to look up the handler at request
time (see [Handler discovery](#handler-discovery-imessagehandlersfinder) below); `Version` is optional and lets multiple
versions of a handler coexist for the same topic — `IVersionSelector` picks which version answers a
given request.

## `[HttpEndpoint("METHOD", "/path")]`

Defined in `Benzene.Http`:

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class HttpEndpointAttribute : Attribute
{
    public HttpEndpointAttribute(string method, string url);

    public string Method { get; }
    public string Url { get; }
}
```

Maps an HTTP method + URL pattern (route parameters like `/orders/{id}` are supported) onto a
message handler, for any HTTP-shaped transport (ASP.NET Core, AWS API Gateway). Unlike `[Message]`,
it can be applied **multiple times** to the same handler class to expose it under more than one
route/method. It's discovered separately from `[Message]` by `IHttpEndpointFinder`
(`ReflectionHttpEndpointFinder`, `CacheHttpEndpointFinder`, `CompositeHttpEndpointFinder`, ... — all
in `Benzene.Http`), which is what HTTP transports use to route an inbound HTTP request to the right
topic before handing off to the same message-handler pipeline every other transport uses.

A handler commonly carries both attributes together, so the same handler answers both an HTTP
route and a direct topic dispatch (e.g. from a queue or another service):

```csharp
[HttpEndpoint("GET", "/orders/{id}")]
[Message("order:get")]
public class GetOrderMessageHandler : IMessageHandler<GetOrderMessage, OrderDto>
{
    // ...
}
```

## Handler discovery (`IMessageHandlersFinder`)

```csharp
public interface IMessageHandlersFinder : IMessageDefinitionFinder<IMessageHandlerDefinition>
{}
```

New handlers are found automatically — you don't register them individually. Discovery is layered:

- **`ReflectionMessageHandlersFinder`** — scans a set of types/assemblies for classes implementing
  `IMessageHandler<TRequest, TResponse>` or `IMessageHandler<TRequest>` that also carry a
  `[Message]` attribute, and builds an `IMessageHandlerDefinition` for each (topic, version, request
  type, response type, handler type). A type without `[Message]` is skipped (logged via
  `Debug.WriteLine`, not an error).
- **`CacheMessageHandlersFinder`** — wraps another finder and caches its results, since reflection
  scanning is done once at startup and re-used for every request afterwards.
- **`DependencyMessageHandlersFinder`** — discovers definitions already registered directly in DI
  (used when you register a handler by hand via `IMessageRouterBuilder.AddMessageHandler<...>`
  instead of relying on assembly scanning).
- **`CompositeMessageHandlersFinder`** — combines multiple finders into one, so reflection-based and
  DI-based discovery can coexist.

`IMessageHandlerDefinitionLookUp` (`MessageHandlerDefinitionLookUp`) is what actually answers "which
handler serves this topic at request time": it merges every registered finder's definitions, groups
by `(topic, version)`, and uses `IVersionSelector` to pick the best-matching version when more than
one exists for the same topic.

`UseMessageHandlers(...)` wires the assemblies/types you pass (or, if you pass none,
`AppDomain.CurrentDomain.GetAssemblies()`) into `AddMessageHandlers(...)`, which registers
`MessageHandlersList`, `DependencyMessageHandlersFinder`, and a `CompositeMessageHandlersFinder`
combining them (plus a `CacheMessageHandlersFinder` wrapping a `ReflectionMessageHandlersFinder`
when you pass explicit types/assemblies) — all as part of one call.

## `.UseMessageHandlers(...)`

Adds the routing middleware to a pipeline (`Benzene.Core.MessageHandlers.MiddlewarePipelineExtensions`):

```csharp
// Scan the current AppDomain's assemblies
app.UseMessageHandlers();

// Scan specific assemblies/types, optionally configuring handler-pipeline middleware
app.UseMessageHandlers(typeof(CreateOrderMessageHandler).Assembly);
app.UseMessageHandlers(router => router.UseFluentValidation());
app.UseMessageHandlers(typeof(CreateOrderMessageHandler).Assembly, router => router.UseFluentValidation());
```

Every overload ultimately registers a **`MessageRouter<TContext>`** as an `IMiddleware<TContext>` in
the pipeline. `MessageRouter<TContext>.HandleAsync`:

1. Extracts the topic via `IMessageGetter<TContext>`. The built-in topic getters never return a
   null topic — an unresolvable topic becomes the `"<missing>"` sentinel — so the router's
   `validation-error` ("Topic is missing") short-circuit fires only for a custom topic getter that
   returns null/empty.
2. Looks up the handler definition for that topic via `IMessageHandlerDefinitionLookUp`. If none is
   found, sets a `not-found` result and returns. This is where an unresolvable topic actually
   surfaces: for the `"<missing>"` sentinel the result's detail is the actionable "No topic could
   be resolved from the message. On HTTP this means no route matched; on a queue/stream transport,
   set the transport's topic attribute/header on the producer (check the configured topic key
   matches the one the producer sends), or configure UsePresetTopic(...)…"; for a real topic it's
   "No handler found for topic '{topic}'".
3. Creates the handler instance via `IMessageHandlerFactory` (resolving it from DI, wrapping it per
   `IMessageHandlerWrapper`, and building its own per-handler middleware pipeline — see
   [Response handling](#response-handling) below).
4. Invokes the handler through an `IDeferredRequestMapper` (defers request-body mapping until
   the handler actually needs it) and sets the resulting `IMessageHandlerResult` on the context via
   `IMessageHandlerResultSetter<TContext>`.

The overload that accepts `Action<MessageRouterBuilder> router` lets you add middleware that runs
**per handler invocation**, wrapped around the actual call — this is how
[`.UseFluentValidation()`](fluent-validation.md) plugs in: it registers an `IHandlerMiddlewareBuilder`
that runs before `MessageHandlerMiddleware<TRequest, TResponse>` and short-circuits with a
validation-failure result if the request fails validation, without ever reaching your handler code.

## Request mapping (`IRequestMapper<TContext>`)

```csharp
public interface IRequestMapper<in TContext>
{
    TRequest? GetBody<TRequest>(TContext context) where TRequest : class;
}
```

`RequestMapper<TContext>` (the low-level implementation, given an explicit `ISerializer`) resolves
the request body two ways:

- If the context already implements `IRequestContext<TRequest>` (some contexts carry an
  already-deserialized/typed request), that's returned directly.
- Otherwise it reads the raw body string via `IMessageBodyGetter<TContext>` and deserializes it with
  `ISerializer` — falling back to `Activator.CreateInstance<TRequest>()` (an empty instance) if the
  body is empty, rather than passing `null` to your handler.

The default registered via `AddContextItems()`, `MultiSerializerOptionsRequestMapper<TContext>`,
picks which `ISerializer` to use per request by asking the scoped `IMediaFormatNegotiator<TContext>`
which registered `IMediaFormat<TContext>` applies (typically decided from the request's
`content-type` header — e.g. negotiating between JSON and [XML](common-middleware.md) bodies) instead of
always using the one default serializer. `EnrichingRequestMapper<TContext>` layers on
`IRequestEnricher<TContext>` to merge extra context-derived fields into the deserialized request
object.

### Content negotiation (`IMediaFormat<TContext>` / `IMediaFormatNegotiator<TContext>`)

A single `IMediaFormat<TContext>` registration describes one wire format for *both* directions:

```csharp
public interface IMediaFormat<TContext>
{
    string ContentType { get; }
    bool CanRead(TContext context, IServiceResolver serviceResolver);
    bool CanWrite(TContext context, IServiceResolver serviceResolver);
    ISerializer GetSerializer(IServiceResolver serviceResolver);
}
```

The scoped, memoizing `IMediaFormatNegotiator<TContext>` (`MediaFormatNegotiator<TContext>`) picks
the format to read the request with (`SelectRead` — first registered format whose `CanRead` matches,
typically via `content-type`, falling back to the process default, JSON) and to write the response
with (`SelectWrite` — first format whose `CanWrite` matches, typically via `accept`, falling back to
whatever `SelectRead` picked). `AcceptHeaderMediaFormatBase<TContext>` (`Benzene.Core.MessageHandlers`)
is the base class for this header-negotiated shape — `Benzene.Xml`'s `XmlMediaFormat<TContext>` is the
only built-in example beyond the JSON default. Every transport calls
`services.AddMediaFormatNegotiation<TContext>()` to register the negotiator and the default JSON
format for its context type.

## Response handling

Once your handler returns an `IBenzeneResult<TResponse>`, a chain of `IResponseHandler<TContext>`s
(registered via `AddContextItems()`/`AddBenzeneMessage()`) turns it into whatever the transport
needs:

- **`RendererResponseHandler<TContext>`** — short-circuits if a body has already been set, otherwise
  walks the registered `IResponseRenderer<TContext>`s in order and delegates to the first whose
  `CanRender` matches. Every transport registers exactly one built-in renderer,
  `SerializerResponseRenderer<TContext>` (the catch-all, registered last): it asks
  `IMediaFormatNegotiator<TContext>.SelectWrite` for the format, then serializes the payload
  (success) or an RFC 9457 problem document (failure — `ProblemTypes.From`) via
  `IResponsePayloadMapper<TContext>` (`DefaultResponsePayloadMapper<TContext>`), rewriting the
  negotiated content type to its `problem+` counterpart on failure (`application/json` →
  `application/problem+json`, `application/xml` → `application/problem+xml`). A handler whose
  payload implements `IRawContentMessage` (`Benzene.Abstractions.Messages`) is delivered as-is, with
  the response content type taken from `IRawContentMessage.ContentType` instead of the negotiated
  format — useful for a handler that renders its own body (e.g. pre-built HTML) and wants it
  delivered verbatim. A custom `IResponseRenderer<TContext>` (e.g. an HTML templating renderer,
  matched via `accept: text/html`) registers *before* the serializer renderer and owns its own error
  representation instead of the problem-details JSON.
- **`DefaultResponseStatusHandler<TContext>`** / transport-specific status handlers — map the
  `IBenzeneResult.Status` string onto the transport's native status/acknowledgement concept (HTTP
  status code, SQS batch-item-failure, etc. — see [Message Results](message-result.md#transport-mapping)
  for the full mapping table).

`IMessageHandlerResultSetter<TContext>` is the seam between `MessageRouter<TContext>` and all of
this — it's what actually stores the `IMessageHandlerResult` on the context so the response
handlers (and diagnostics, e.g. `ActivityMiddlewareDecorator`'s `benzene.handler` tag — see
[Middleware](middleware.md#automatic-activity-wrapping-imiddlewarewrapper)) can read it afterwards.

## Cancellation

Handler and middleware signatures never carry a `CancellationToken` parameter — that's a deliberate
design choice (`IMessageHandler<TRequest, TResponse>.HandleAsync(TRequest request)` stays exactly as
shown above, on every transport). Instead, cancellation is **ambient**: inject
`ICancellationTokenAccessor` (`Benzene.Abstractions.DI`) like any other scoped dependency, and read
its `.CancellationToken` property at the point you actually need it — for example, right before an
outbound call — rather than caching it in a field at construction time. Wrapping middleware (like
[`UseTimeout`](common-middleware.md#usetimeout)) can replace the ambient token for the duration of an
inner call, so a value captured earlier can be stale by the time it matters.

```csharp
[Message("order:sync")]
public class SyncOrderMessageHandler : IMessageHandler<SyncOrderMessage>
{
    private readonly HttpClient _httpClient;
    private readonly ICancellationTokenAccessor _cancellation;

    public SyncOrderMessageHandler(HttpClient httpClient, ICancellationTokenAccessor cancellation)
    {
        _httpClient = httpClient;
        _cancellation = cancellation;
    }

    public async Task HandleAsync(SyncOrderMessage request)
    {
        // Read .CancellationToken here, at the point of use - not captured in the constructor.
        await _httpClient.PostAsJsonAsync("https://partner.example.com/orders", request, _cancellation.CancellationToken);
    }
}
```

**The guarantee.** The accessor's token defaults to `CancellationToken.None`. A handler, middleware,
or component that never resolves `ICancellationTokenAccessor` behaves byte-for-byte as before — no
new exceptions, no new statuses, no timing changes. A component that does resolve it must treat the
token as *advisory and possibly `CancellationToken.None`*: on transports with no cancellation concept
it simply never fires, and code written as `await client.DoAsync(x, cancellation.CancellationToken)`
is correct everywhere without checking which host it runs on.

**When would I actually use this?** For a handler that makes a long-running external call (an HTTP
request to a partner API, a slow database query) that should be interrupted rather than left running
to completion when the host is shutting down, the caller has disconnected, or an operator-configured
deadline ([`UseTimeout`](common-middleware.md#usetimeout)) has elapsed. Most handlers never need it —
short, fast handlers can safely ignore `ICancellationTokenAccessor` entirely and rely on the guarantee
above.

**Which hosts seed a real token, and with what.** A host that has nothing to seed simply leaves the
accessor at its default `CancellationToken.None` — every handler still runs correctly, it just can't
be interrupted by that host's own signal (`UseTimeout` still works everywhere, since it supplies its
own timer regardless of what, if anything, the host seeded).

| Host / entry point | What gets seeded |
| --- | --- |
| ASP.NET Core, Azure Functions HTTP (AspNet) | `HttpContext.RequestAborted` |
| gRPC | `ServerCallContext.CancellationToken` |
| RabbitMQ worker | `BasicDeliverEventArgs.CancellationToken` |
| Kafka worker | the worker's own shutdown/run token |
| Azure Service Bus worker | `ProcessMessageEventArgs.CancellationToken` |
| Azure Event Hub worker | `ProcessEventArgs.CancellationToken` |
| SQS consumer (self-hosted worker) | the consumer's run/shutdown token |
| Google Cloud Functions (Pub/Sub) | the function's own `CancellationToken` parameter |
| Azure Functions (isolated worker, non-HTTP triggers) | `FunctionContext.CancellationToken` |
| AWS Lambda (all `Benzene.Aws.Lambda.*`) | nothing — `ILambdaContext` has no cancellation token, so this is `CancellationToken.None` by design. Use [`UseTimeout`](common-middleware.md#usetimeout) with a value derived from your function's configured timeout if you need a deadline here. |

See [Middleware and cancellation](middleware.md#middleware-and-cancellation) for how middleware reads
and (for a component like `UseTimeout` that creates a deadline) replaces the ambient token.

## See also

- [Message Results](message-result.md) — `IBenzeneResult<T>`, the `BenzeneResult` factory, result
  statuses, and how they map onto transport-specific responses.
- [Middleware](middleware.md) — the pipeline mechanism `MessageRouter<TContext>` and per-handler
  middleware (like FluentValidation) are built on.
- [Common Middleware](common-middleware.md#usemessagehandlers) — `.UseMessageHandlers(...)` and
  `.UseFluentValidation()` as ready-made pipeline middleware.
- [Common Middleware: UsePresetTopic](common-middleware.md#usepresettopic) — route every message on
  one specific queue/subscription's pipeline to a fixed topic, for producers that never set the
  usual topic attribute/property.
- [Fluent Validation](fluent-validation.md) — request validation before a handler is invoked.
- [Spec](spec.md) — generating OpenAPI/AsyncAPI documentation from `[Message]`/`[HttpEndpoint]`
  metadata.
