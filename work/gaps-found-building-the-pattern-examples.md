# Gaps found while building the pattern examples

Seven runnable pattern examples now live in
[benzene-patterns](https://github.com/daniellepelley/benzene-patterns), each consuming the published
NuGet packages like any other downstream user. Building them surfaced four framework gaps and two
documentation errors. This note collects them in one place so they can be triaged as a set; each one
is also written up in the README of whichever example ran into it.

Nothing here blocked an example — every one has a local workaround in the repo, which is exactly the
problem: the workarounds are what a real adopter would also have to write, without knowing that the
framework nearly does it for them.

Status at time of writing: all four gaps are present in **0.0.2-alpha.6**.

---

## 1. No `OutboundContext` overload for RabbitMQ, Kafka or HTTP — **seven copies**

**What's missing.** The outbound routing table's pipelines are
`IMiddlewarePipelineBuilder<OutboundContext>`. Every cloud transport ships an extension against that
shape *and* the older `IBenzeneClientContext<T, Void>` one:

| Transport | `IBenzeneClientContext<T, Void>` | `OutboundContext` |
|---|---|---|
| SQS, SNS, EventBridge, Service Bus, Event Grid, Event Hub, Queue Storage, Pub/Sub, in-process | ✅ | ✅ |
| **RabbitMQ** | ✅ | ❌ |
| **Kafka** | ✅ | ❌ |
| **HTTP** (`HttpBenzeneMessageClient`) | registered as `IBenzeneMessageClient` | ❌ |

So a route cannot reach any of those three without a hand-written terminal middleware.

**Cost so far.** Seven copies of the same ~50-line adapter across five patterns:

| Example | Adapter | Transport |
|---|---|---|
| real-time-risk (Risk Coordinator) | `BenzeneMessageOverHttp.cs` | HTTP |
| modular-monolith (extracted Orders) | `BenzeneMessageOverHttp.cs` | HTTP |
| transactional-outbox (Orders, Relay) | `BenzeneMessageOverHttp.cs` ×2 | HTTP |
| two-tier-architecture (Orchestrator) | `BenzeneMessageOverHttp.cs` | HTTP |
| choreography (Emitter) | `RabbitMqOverOutbound.cs` | RabbitMQ |
| cqrs-read-models (Tenant, User) | `RabbitMqOverOutbound.cs` ×2 | RabbitMQ |

**The fix.** For RabbitMQ and Kafka this is a missing overload rather than a missing feature — the
context converter and publish middleware already exist; they are bound to the wrong builder type. For
HTTP, `HttpBenzeneMessageClient` already exists and is documented as "the HTTP counterpart of the AWS
Lambda invoke path"; it needs a `UseBenzeneMessageOverHttp()` on `OutboundContext` to bind it into a
route the way `UseSqs`/`UseServiceBus`/`UseInProcess` do.

**Worth noting** that HTTP-between-services is the shape every one of these examples needs to run on
a laptop, so this gap is on the path of anyone who tries Benzene locally before deploying it.

---

## 2. `UseStream` is not marked terminal, so its own start-up check rejects it

**What happens.** `StreamExtensions.UseStream` is documented as *"a terminal stream-processing
step"* — and it is; nothing runs after it. But it is built on `Use(name, func)` rather than
`UseTerminal(name, func)`, so it is not marked `ITerminalMiddleware`. A pipeline that ends in it
fails to boot:

```
terminal-middleware: 1 pipeline(s) cannot handle a message:
  the StreamContext`1 pipeline has no terminal middleware, so every message reaching it would run to
  the end of the pipeline unhandled
```

The check is doing exactly its job, on a false positive.

**The fix.** One word: `UseStream` calling `UseTerminal` instead of `Use`. The workaround is in
`streaming-processing/dotnet/TickPipeline/TerminalStream.cs`, which is that fix written out locally.

**Worth checking** whether the Kinesis and Event Hubs bindings hit this too, or whether they happen
to add something after the stream step — the report above came from a hand-built pipeline over
`Benzene.Core.Middleware`, which is the transport-neutral path the operators are meant to support.

---

## 3. Nothing restores the correlation id inbound

**What's missing.** `Benzene.Clients` stamps the correlation id on the way **out**
(`UseCorrelationId()`), and `ActivityMiddlewareDecorator` reads it back onto the inbound diagnostics
span. But nothing puts it back into `ICorrelationId`, so a consumer's own correlation id is a fresh
`Guid` and the chain breaks exactly where a reader would look for it.

Every choreography reaction and the CQRS read model carry six lines of pipeline to do it:

```csharp
var headers = resolver.GetService<IMessageHeadersGetter<RabbitMqContext>>().GetHeaders(context);
if (headers.TryGetValue(WireHeaders.CorrelationId, out var correlationId))
{
    resolver.GetService<ICorrelationId>().Set(correlationId);
}
```

**The fix.** An inbound `UseCorrelationId()` counterpart, transport-generic over
`IMessageHeadersGetter<TContext>` the way `UseW3CTraceContext()` already is. That would delete five
copies.

**Related, and worth fixing together:** the default header key. `CorrelationHeaderDefaults` (added
after alpha.4) settles this, but on alpha.4 the outbound middleware defaults to `correlationId` while
`wire-contracts.md` §1.1 uses `x-correlation-id`. The examples name the key explicitly at both ends
to be version-independent, which is the right habit anyway — but a fresh install should join up
without it.

---

## 4. `UseAspNet` does not exist before alpha.6

Not a gap in the current release, recorded because it forced a version split across the examples.
`UseAspNet` — which mounts Kestrel as a peer worker beside another transport, so a projection side
and a query side share one process and one store — arrived in alpha.5 or .6. The CQRS and later
examples pin **alpha.6** for it; the earlier ones pin alpha.4. Without it the CQRS read model would
need two Benzene containers in one process passing a store between them, which is a hosting
workaround a pattern example should not have to explain.

---

## 5. Doc error: the single-generic `IMessageHandler<TRequest>` snippet does not compile

Four pattern docs showed:

```csharp
public class ProjectTenant : IMessageHandler<TenantCreated>
{
    public async Task<IBenzeneResult> HandleAsync(TenantCreated e) { … }
}
```

The shipped `IMessageHandler<TRequest>` returns a plain `Task`. Fixed in the spec repo
(`choreography`, `cqrs-read-models`, `event-sourcing`, `transactional-outbox`), and each snippet now
carries a note saying why the response type is there when nothing reads it.

**It is not cosmetic on those four in particular.** Every one is a handler on a queue- or
stream-shaped transport, where the handler's result **status** is what settles the delivery — success
acks, failure nacks and the source redelivers. A projection that cannot report failure is one whose
failed projections are acked and silently dropped, which is precisely the bug the surrounding prose
is warning about.

**Possibly worth reconsidering the API rather than only the docs**: four independent documents
reached for the single-generic form, which suggests it reads as the natural choice for an event
handler. If it cannot report failure, it may deserve either a result-returning sibling or a note on
the interface itself.

---

## 6. Smaller notes

- **`BenzeneResult.Set<T>(status, string[])` is obsolete** in alpha.6 in favour of `SetFailed<T>`,
  with a clear reason on the attribute. The alpha.4-pinned examples still use `Set`; the alpha.6 ones
  use `SetFailed`. Nothing to fix — noting that the deprecation message did its job.
- **The start-up checks earned their keep repeatedly.** Across seven examples they caught: a missing
  terminal middleware (three times, including gap 2 above), an unregistered `ICorrelationId` on an
  outbound pipeline, and an unresolvable serializer in a pure outbound worker. Every one was named
  with the pipeline and the missing piece, before any message was handled. Worth saying out loud in
  the docs — it is one of the better things about adopting the framework and it is currently
  discovered by accident.
