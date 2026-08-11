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
