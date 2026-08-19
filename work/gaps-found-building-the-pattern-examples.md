# Gaps found while building the pattern examples

Seven runnable pattern examples now live in
[benzene-patterns](https://github.com/daniellepelley/benzene-patterns), each consuming the published
NuGet packages like any other downstream user. Building them surfaced four framework gaps and two
documentation errors; a later duplication sweep over `examples/` added two more (7 and 8). This
note collects them in one place so they can be triaged as a set; each one is also written up in the
README of whichever example ran into it.

Nothing here blocked an example — every one had a local workaround in the repo, which was exactly the
problem: the workarounds are what a real adopter would also have to write, without knowing that the
framework nearly does it for them.

**Status: gaps 1, 2, 3 and 8 are fixed and released in `0.0.3-alpha.2`.** Gap 4 and the `BenzeneHost`
suggestion under 5b shipped in alpha.1. benzene-patterns is pinned to alpha.2 and **every workaround
is deleted** — nine files, 810 net lines: five copies of the HTTP outbound adapter, three of its
RabbitMQ twin, four inbound-correlation blocks, and the local terminal-stream shim. The examples
were run, not merely rebuilt, to confirm the shipped extensions behave like the code they replaced:
the two-tier saga's four outcomes, the modular monolith answering identically in-process and over
HTTP, the choreography fan-out carrying one correlation id across three reactions, the CQRS read
model joining two write services, and the streaming pipeline's resume-from-failure fold.

What remains open here is items 5, 6 and 7 — the documentation errors, the smaller notes, and the
mesh aggregation seam.

---

## 1. No `OutboundContext` overload for RabbitMQ, Kafka or HTTP — **seven copies** — **FIXED, released in 0.0.3-alpha.2**

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

**Done.** `.UseRabbitMq(...)`, `.UseKafka(...)` and `.UseBenzeneMessageOverHttp(...)` now exist on
`IMiddlewarePipelineBuilder<OutboundContext>`, each over a public `Outbound*ContextConverter` mirroring
`OutboundSqsContextConverter`, each with the `Action<...>` inner-pipeline overload as the rung below.
Documented in `docs/clients.md`. The seven downstream adapters can be deleted once the examples repin.

---

## 2. `UseStream` is not marked terminal, so its own start-up check rejects it — **FIXED, released in 0.0.3-alpha.2**

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

**Done.** `UseStream` calls `UseTerminal("Stream", ...)`. Checked the other bindings while there: the
Kinesis, Event Hubs, Cosmos change-feed and Blob Storage extensions all just *document* `UseStream`
rather than reimplementing it, so the one-word fix covers every one of them. Pinned by
`StreamMiddlewareApplicationTest.UseStream_IsMarkedTerminal_SoAPipelineEndingInItBoots`.

---

## 3. Nothing restores the correlation id inbound — **FIXED, released in 0.0.3-alpha.2**

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

**Done.** `Benzene.Diagnostics.Correlation.Extensions.UseCorrelationId<TContext>(correlationKey = null)`,
over the public `InboundCorrelationIdMiddleware<TContext>`. It resolves both `ICorrelationId` and the
headers getter when the pipeline is built, so a pipeline missing either is named at start-up rather
than mid-message. The non-generic outbound overload still wins on an `OutboundContext` builder when
both namespaces are imported - pinned by a test, because that would otherwise have been a silent
breaking change. `docs/correlation-ids.md` now leads with it and keeps the hand-written form as the
rung below.

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

## 5b. `UseAspNet` is the right shape and nobody finds it

Not a gap in the code — a gap in where it is signposted. `AspNetSelfHostExtensions.UseAspNet` runs
Kestrel as a Benzene worker, so a service with no ASP.NET surface of its own declares HTTP in
`Configure` alongside every other transport and `Program.cs` stays the plain generic host:

```csharp
// Program.cs, entire
var host = Host.CreateDefaultBuilder(args).UseBenzene<StartUp>().Build();
await host.RunAsync();

// StartUp.Configure
app.UseWorker(worker => worker
    .UseAspNet(http => http.UseMessageHandlers(), o => o.Urls = $"http://0.0.0.0:{port}"));
```

**Four of the seven pattern examples were written in the embedded shape instead** — the
`WebApplicationBuilder.UseBenzene<StartUp>()` / `app.UseBenzene()` / `app.UseHttp(...)` triangle —
for services that have no controllers and no minimal APIs, i.e. exactly the case `UseAspNet`'s own
doc comment says it is for. They have since been switched, and behave identically.

The reason for the mistake is instructive: the embedded shape is what every getting-started path
shows, so it is what gets copied, and `UseAspNet` is discoverable only by reading
`Benzene.AspNet.Core`'s source or the `K8sTransports` example. The distinction is not a style
preference — it decides whether adding a queue consumer later is *one line in `Configure`* or a
rewrite of the host — which makes it exactly the kind of thing a newcomer should meet early rather
than discover on their third service.

**Suggested:** lead with the self-hosted shape in `docs/getting-started-aspnet.md` for a
Benzene-only service and present the embedded one as the *embedding* case it is; and cross-reference
`UseAspNet` from `UseBenzene<TStartUp>(WebApplicationBuilder)`'s remarks, which currently point one
way only.

**Also worth considering — now done.** The residual `Program.cs` was still two lines of generic-host
ceremony that said nothing about the application. `Benzene.HostedService.BenzeneHost` now provides
`RunAsync<TStartUp>(args, configureHost, ct)`, `Run<TStartUp>(...)` and `Build<TStartUp>(...)`, so an
entry point is:

```csharp
await BenzeneHost.RunAsync<StartUp>(args);
```

`configureHost` is applied before `UseBenzene<TStartUp>()` so a caller can replace a `TryAdd`ed
Benzene default; `Build` returns the unstarted `IHost` for tests and manual lifecycles. Documented in
`hosting.md` and led with in `getting-started-worker.md`; `examples/K8sTransports` uses it.

One consequence worth a maintainer's eye: `Benzene.HostedService` now references
`Microsoft.Extensions.Hosting` (not just Abstractions), because `Host.CreateDefaultBuilder` lives
there. Floor 6.0.0 — the lowest with that API — and the Abstractions floor was raised from 2.1.1 to
match, since Hosting 6.0.0 requires it anyway. Every consumer that ran a worker had to add that
package themselves before, so this is a footprint shift rather than a new dependency in practice.

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

---

## 7. The mesh aggregation pass is hand-rolled four times, and the fourth copy dropped the lock — **FIXED**

Found by the duplication sweep over `examples/`, not by the pattern examples.
`MeshAggregationService.cs` + `MeshRefreshHandler.cs` exist as near-identical pairs in
**four** mesh examples - `AzureMesh/Mesh`, `AzureFunctionsMesh/Mesh`, `K8sMesh/Mesh`,
`GoogleCloudMesh/Mesh`. The body is always the same three calls:

```csharp
var registry = /* discovered, or MeshRegistry.FromEnvironment() */;
await store.PublishAsync("registry.json", MeshRegistryJson.Serialize(registry));
await aggregator.RunOnceAsync(registry);
```

plus a `POST /mesh/refresh` handler that calls it and returns the count.

**The count is not the whole argument - the drift is.** Three of the four wrap the pass in a
`SemaphoreSlim` single-writer gate, because an on-demand refresh and the periodic pass write the same
artifact store and can interleave into a momentarily inconsistent catalog. `K8sMesh/Mesh` does not.
That is the copies diverging on a correctness property, which is what a missing seam looks like from
the outside.

**Done.** `Benzene.Mesh.Aggregator.MeshAggregationPass` owns "run one pass, serialised". The registry
source is the constructor parameter, because it is the only genuine per-platform difference - a
discovery call on Azure/AzureFunctions/K8s, `MeshServiceRegistry.FromEnvironment()` on GoogleCloud.
All four copies are deleted and all four hosts now have the gate, K8sMesh included.

Five tests cover it, and the one that matters - two overlapping passes never interleave - was checked
against a gate-less build to confirm it fails there. Two more pin what a hand-rolled gate gets wrong
even when it remembers the gate: the registry source is asked once per pass rather than cached, and
the gate is released when a pass throws, so a failed discovery does not wedge every later pass.

The ladder holds: the explicit form is still three lines a host can write itself (publish the
registry, `RunOnceAsync`, count), which is what to drop to for a different write order, artifact key,
or concurrency policy.

---

## 8. Eleven copies of the embedded ASP.NET entry point - **FIXED, released in 0.0.3-alpha.2**

The same duplication sweep found the `WebApplication.CreateBuilder` / `builder.UseBenzene<T>()` /
`Build()` / `app.UseBenzene()` / `Run()` triangle written out in **eleven** example `Program.cs`
files, nine of which also repeated the same two-line `PORT`-env-var block. Nothing in any of them
said anything about the application except the `Startup` type name.

**Done.** `Benzene.AspNet.Core.BenzeneWebHost` (`Run`/`RunAsync`/`Build`) composes exactly those
calls, with `configureBuilder` (before the startup runs) and `configureApp` (before Benzene's
terminal wiring) as the hooks. Ten entry points collapsed onto it;
`examples/Asp/Benzene.Example.Asp.Minimal` keeps the explicit form written out and now says in a
comment that it does so deliberately. Two generic-host entry points collapsed onto the existing
`BenzeneHost.RunAsync` at the same time.

**Still open from 5b:** `AzureMesh/Mesh`, `K8sMesh/Mesh`, `K8sMesh/Service` and
`Cloudflare` are single-host containers with no ASP.NET surface of their own, so by 5b's own rule
they want `BenzeneHost.RunAsync<Startup>(args)` + `UseWorker(w => w.UseAspNet(...))` rather than the
embedded shape at all. That is a change to each `Startup.Configure`, not just the entry point, and it
was left alone. The `GoogleCloudMesh` five genuinely need the embedded shape - the same `Startup` also
runs under a Cloud Functions host, where `UseAspNet` would start a second Kestrel.

---
