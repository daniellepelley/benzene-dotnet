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
per-invocation marker in logs. To populate it from a custom source (e.g. a partner's proprietary
header), register `AddCorrelationId()` and call `ICorrelationId.Set(...)` from your own middleware:

```csharp
app.Use("PartnerCorrelation", resolver => async (context, next) =>
{
    var headers = resolver.GetService<IMessageHeadersGetter<MyContext>>();
    resolver.GetService<ICorrelationId>().Set(headers.GetHeader(context, "x-partner-request-id"));
    await next();
});
```

Outbound clients can still forward the value: `.UseCorrelationId()` on an outbound route pipeline
(see [Clients — Outbound middleware](clients.md#outbound-middleware)) stamps the current
`ICorrelationId` onto the outgoing request's headers under the **`correlationId`** key by default.
The key is configurable — `.UseCorrelationId(correlationKey)` and `CorrelationIdMiddleware`'s
constructor both take a `correlationKey` parameter (default `"correlationId"`). Note that the
inbound diagnostics span tag (`benzene.correlation-id`, set by `AddDiagnostics()`'s middleware
decorator) reads the **`x-correlation-id`** header, so a service that wants the receiving side's
span tag to join up with the forwarded value should pass the key explicitly:

```csharp
pipeline.UseCorrelationId("x-correlation-id")
```

An explicit per-call header wins: if the outgoing message already carries a header under the same
key (e.g. via `IBenzeneMessageSender.SendAsync`'s per-call `headers` parameter), the middleware
leaves it untouched instead of overwriting it with the ambient — possibly self-generated — value.

## See Also

- [Monitoring & Diagnostics — W3C Trace Context](monitoring.md#w3c-trace-context)
- [Request Correlation cookbook](cookbooks/request-correlation.md)
