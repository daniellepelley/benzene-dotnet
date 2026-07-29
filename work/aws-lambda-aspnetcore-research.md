# Running ASP.NET Core and Benzene in one Lambda — research

**Status:** RESEARCH — feasibility established, one open question needs a spike before committing.
**Date:** 2026-07-27
**Question:** Can a single Lambda serve HTTP through a real ASP.NET Core application while the same
function handles SQS/SNS/EventBridge through Benzene's pipelines? If so, does that justify a
`Benzene.Aws.Lambda.AspNetCore` package?

---

## 1. Answer: yes, and the seam already exists on both sides

The two facts that decide it:

| | |
|---|---|
| **Benzene's Lambda entry point takes a raw `Stream`** | `AwsLambdaEntryPoint.FunctionHandlerAsync(Stream, ILambdaContext)` runs a middleware pipeline over an `AwsEventStreamContext`. Each binding (`ApiGatewayLambdaHandler`, `SqsLambdaHandler`, …) tries to deserialize the stream into its own event type and claims it or passes on. Dispatch-by-event-shape is already the model. |
| **AWS's ASP.NET function is callable, not just an entry point** | `AbstractAspNetCoreFunction<TREQUEST,TRESPONSE>.FunctionHandlerAsync(TREQUEST, ILambdaContext)` is `public virtual`. Nothing requires it to *be* the Lambda handler — it can be called from ours. |

So ASP.NET becomes **one more claimant in the existing chain**, not a competing host. This is the
same shape as a pattern the codebase already supports: `Extensions.UseApiGatewayV2`'s own docs say
"register this alongside `UseApiGateway` to serve REST (v1) and HTTP API (v2) front doors from one
Lambda — the two routers are mutually exclusive on payload shape". ASP.NET is a third front door
claiming the same payload shapes.

## 2. The container is the interesting part, and it also lines up

Both sides accept a **pre-built `IServiceProvider`**:

- `protected AbstractAspNetCoreFunction(IServiceProvider hostedServices)` — bypasses AWS's internal
  host creation.
- `public MicrosoftServiceResolverFactory(IServiceProvider serviceProvider)` — Benzene's MS-DI
  bridge, already used by every Lambda host.

Feed both the *same* provider and there is one container, one host build, two dispatch paths. That
matters for more than tidiness: the application's services (stores, clients, the mesh's collector)
are singletons that should not exist twice in one process, and Benzene handlers invoked over SQS
must see the same registrations the HTTP path sees.

AWS also builds its host **once and caches it** (`Start()` sets `_hostServices`, and
`FunctionHandlerAsync` does `if (!IsStarted) Start();`), so there is no per-invocation host cost.

## 3. Shape of the integration

```
Lambda handler  ──►  AwsLambdaEntryPoint (Stream)
                        │
                        ├─ AspNetCoreLambdaHandler ─► APIGatewayHttpApiV2ProxyFunction
                        │    (claims API GW v1/v2/ALB payloads)   └─► the ASP.NET pipeline
                        │
                        ├─ SqsLambdaHandler       ─► Benzene SQS pipeline
                        ├─ SnsLambdaHandler       ─► Benzene SNS pipeline
                        └─ …                          (one DI scope per record)
```

Registration would mirror the existing extensions exactly:

```csharp
app.UseAspNetCore<MyLambdaEntryPoint>()   // instead of UseApiGateway(...)
   .UseSqs(sqs => sqs.UseMessageHandlers())
   .UseSns(sns => sns.UseMessageHandlers());
```

**Do not use `AddAWSLambdaHosting`/`Amazon.Lambda.AspNetCoreServer.Hosting` for this.** That package
takes over `Main` — it starts the Lambda runtime loop with an HTTP-only handler, which is precisely
the thing a mixed function cannot allow. The older class-library model
(`Amazon.Lambda.AspNetCoreServer`, subclass + `Init(IWebHostBuilder)`) is the right one here because
it leaves the entry point ours.

## 4. The insight worth acting on: the two compose rather than compete

Choosing ASP.NET for HTTP looks like it means abandoning Benzene's HTTP binding and its
`[HttpEndpoint]` handlers. It does not — **`Benzene.AspNet.Core` already mounts Benzene inside an
ASP.NET pipeline** (`app.UseBenzene(b => b.UseHttp(...))`, the model `examples/Asp` uses today).

So the full stack in one Lambda is:

- ASP.NET Core owns the HTTP front door — controllers, auth middleware, static files, whatever the
  team already has.
- `Benzene.AspNet.Core` mounts the *same* Benzene handlers inside it, so `[HttpEndpoint]` routes
  keep working.
- Benzene's own bindings handle SQS/SNS/EventBridge on the same function, sharing the container.

One set of handlers, reachable over HTTP through ASP.NET and over queues directly — which is
Benzene's central promise, extended to teams that cannot give up ASP.NET.

## 5. Open question — the one thing to spike first

`AbstractAspNetCoreFunction(IServiceProvider)` expects a provider whose `IServer` is AWS's
`LambdaServer` (that is what `MarshallRequest` → `_server.Application.CreateContext(features)`
drives). `AddAWSLambdaHosting` normally registers it, and that is the package we are avoiding.

**Spike:** build a `WebApplication` manually, register `LambdaServer` as `IServer`, hand
`app.Services` to both the ASP.NET function and `MicrosoftServiceResolverFactory`, and confirm an
API Gateway v2 payload round-trips. If registering `LambdaServer` outside AWS's hosting package
turns out to be impractical, the fallback is to let the ASP.NET function build its own host and give
Benzene that provider instead (the reverse direction) — worse ergonomically, since the app's
composition root then lives inside `Init(IWebHostBuilder)`, but still one container.

Everything else in this document is verified against the two libraries' source; this is the only
step I could not confirm without compiling against `Amazon.Lambda.AspNetCoreServer`.

## 6. Costs to weigh

- **Cold start.** A full ASP.NET Core host in a Lambda is materially heavier than Benzene's own HTTP
  binding, which exists partly to avoid it. Teams already paying that cost lose nothing; teams on
  Benzene's binding today should not be moved onto this by default.
- **Two scope models.** ASP.NET scopes per `HttpContext`, Benzene per invocation (per *record* for
  batches). They never overlap inside one invocation — an event is either HTTP or it is not — so
  this is safe, but a shared scoped service must not be assumed to span both paths.
- **Package weight.** `Benzene.Aws.Lambda.AspNetCore` would take a dependency on
  `Amazon.Lambda.AspNetCoreServer`, pulling the ASP.NET hosting stack into anything referencing it.
  It must therefore be its own package, never a dependency of `Benzene.Aws.Lambda.Core`.

## 7. Recommendation

Worth building, as a **new package `Benzene.Aws.Lambda.AspNetCore`** containing:

1. `AspNetCoreLambdaHandler` — the `AwsEventStreamContext` middleware that claims API Gateway
   v1/v2/ALB payloads and delegates to an `AbstractAspNetCoreFunction`. Payload detection should
   reuse the existing routers' rules rather than re-deriving them.
2. `UseAspNetCore(...)` on `IMiddlewarePipelineBuilder<AwsEventStreamContext>`, mirroring
   `UseApiGateway`.
3. The shared-provider composition helper (§2), which is the part users would otherwise get wrong.
4. A TestHelpers sibling, matching every other binding, so a mixed function can be tested in memory.

Sequence: spike §5 first. It is the only thing that can invalidate the design, and it is a day's
work rather than a package's.
