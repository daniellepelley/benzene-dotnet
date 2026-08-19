# Correlation Ids

> Cross-service correlation is handled by automatic [W3C `traceparent` propagation](monitoring.md#w3c-trace-context)
> (`UseW3CTraceContext()`), which continues a distributed trace from the incoming
> `traceparent`/`tracestate` headers on every transport.

## What remains

`ICorrelationId` and its log-scope enrichment are still available. `WithCorrelationId()` attaches a
`correlationId` value to the logging scope (via `ILogger.BeginScope`):

```csharp
.UseLogResult(x => x.WithCorrelationId());
```

With nothing populating it, `ICorrelationId` self-generates a GUID per scope — useful as a
per-invocation marker in logs, but it means an *inbound* correlation id from the caller is lost
unless something puts it back.

**Inbound: `.UseCorrelationId()`.** Add it near the top of any pipeline and the caller's correlation
header is read off the incoming message and seeded into `ICorrelationId`, so this service's logs,
traces, and its own outbound sends continue the caller's chain:

```csharp
app.UseCorrelationId();
```

It is transport-agnostic in exactly the way `UseW3CTraceContext()` is — it works on any pipeline
whose context has an `IMessageHeadersGetter<TContext>` registered (HTTP, SQS, SNS, Kafka, RabbitMQ,
Service Bus, Event Hubs, …). It reads the same key the outbound side writes
(`CorrelationHeaderDefaults.HeaderKey`, `x-correlation-id`) unless you pass one, and registers
`ICorrelationId` if nothing else has. Because it resolves both `ICorrelationId` and the headers
getter when the pipeline is *built*, a pipeline missing either is named by the start-up checks
rather than failing mid-message.

**The rung below it**, if you want a different source (e.g. a partner's proprietary header) or your
own `ICorrelationId`, is the middleware it composes — the same public types, written out:

```csharp
app.Use(resolver => new InboundCorrelationIdMiddleware<MyContext>(
    resolver.GetService<ICorrelationId>(),
    resolver.GetService<IMessageHeadersGetter<MyContext>>(),
    "x-partner-request-id"));
```

and below that, an ordinary inline middleware doing it by hand:

```csharp
app.Use("PartnerCorrelation", resolver => async (context, next) =>
{
    var headers = resolver.GetService<IMessageHeadersGetter<MyContext>>();
    resolver.GetService<ICorrelationId>().Set(headers.GetHeader(context, "x-partner-request-id"));
    await next();
});
```

**Outbound: `.UseCorrelationId()` on an outbound route pipeline**
(see [Clients — Outbound middleware](clients.md#outbound-middleware)) stamps the current
`ICorrelationId` onto the outgoing request's headers under the **`x-correlation-id`** key by
default (`CorrelationHeaderDefaults.HeaderKey`, matching wire-contracts.md's own example) — the
same key the inbound diagnostics span tag (`benzene.correlation-id`, set by `AddDiagnostics()`'s
middleware decorator) reads by default, so the two directions join up without any configuration.

The key is configurable in one place for both directions: register a
`Benzene.Abstractions.CorrelationHeaderOptions` in `ConfigureServices` and both
`CorrelationIdMiddleware` and the diagnostics decorator pick it up. `.UseCorrelationId(correlationKey)`
and `CorrelationIdMiddleware`'s constructor also still take a `correlationKey` parameter to override
per call, which wins over a registered `CorrelationHeaderOptions`:

```csharp
services.UsingBenzene(x => x.AddSingleton(new CorrelationHeaderOptions { HeaderKey = "x-tenant-correlation-id" }));
```

An explicit per-call header wins: if the outgoing message already carries a header under the same
key (e.g. via `IBenzeneMessageSender.SendAsync`'s per-call `headers` parameter), the middleware
leaves it untouched instead of overwriting it with the ambient — possibly self-generated — value.

## See Also

- [Monitoring & Diagnostics — W3C Trace Context](monitoring.md#w3c-trace-context)
- [Request Correlation cookbook](cookbooks/request-correlation.md)
