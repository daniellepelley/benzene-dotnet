> ARCHIVED 2026-08-20: actioned; research folded into `src/Benzene.Aws.Lambda.HttpBridge` (its CLAUDE.md cites this doc).

# Running ASP.NET Core and Benzene in one Lambda — research

**Status:** DELIVERED (2026-07-30) — shipped as a layered pair rather than the single package §7
proposed: `Benzene.Aws.Lambda.HttpBridge` (the ASP.NET-free port), `Benzene.Aws.Lambda.Hosting` (the
ASP.NET-free custom-runtime bootstrap loop), and `Benzene.Aws.Lambda.AspNet` (the ASP.NET adapter:
`BenzeneAspNetBridge`, the Benzene-driven `IServer`, and one-call `AddBenzeneAwsLambdaHosting`). The
end-user recipe collapsed further than §5 — `app.Run()` instead of a hand-built entry point — because
`AddBenzeneAwsLambdaHosting` re-creates `Amazon.Lambda.AspNetCoreServer.Hosting`'s runtime-support
server natively with Benzene as the dispatcher. See `docs/cookbooks/aspnet-with-sqs-and-sns.md`.
Original research below, kept for the rationale.

**Was:** RESEARCH — **feasibility proven by a working spike** (2026-07-27). Ready to build.
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

## 5. Open question — RESOLVED by spike

The doubt was whether AWS's `LambdaServer` could be registered outside their hosting package, since
`AbstractAspNetCoreFunction(IServiceProvider)` needs it as the `IServer` and `AddAWSLambdaHosting` is
the package a mixed function must avoid.

**It can.** Reflection over the shipped assembly confirms `LambdaServer` is a **public type with a
public parameterless constructor** (namespace `Amazon.Lambda.AspNetCoreServer.Internal`), and
`FunctionHandlerAsync` is **public virtual** on `AbstractAspNetCoreFunction<TREQUEST,TRESPONSE>`,
which also exposes a **protected `ctor(IServiceProvider)`** — inherited by
`APIGatewayHttpApiV2ProxyFunction`, so a two-line subclass is all it takes.

A spike drove a real API Gateway v2 payload into ASP.NET and a real SQS event into Benzene, in one
process off one provider:

```
HTTP  -> 200 {"greeting":"Hello benzene"}
SQS   -> {"batchItemFailures":[]}
SQS   -> handler saw [ABC]
SHARED PROVIDER -> aspnet and benzene same instance: True
```

### The verified recipe

```csharp
var builder = WebApplication.CreateBuilder();

// 1. LambdaServer replaces Kestrel. Registered after CreateBuilder so it wins the IServer resolve.
builder.Services.AddSingleton<IServer, LambdaServer>();

// 2. Benzene registers into the SAME IServiceCollection.
builder.Services.UsingBenzene(x => x
    .AddBenzene()                                     // see the gotcha below
    .AddMessageHandlers(typeof(OrderHandler).Assembly)
    .AddSqs());

var container = new MicrosoftBenzeneServiceContainer(builder.Services);
var eventPipeline = new MiddlewarePipelineBuilder<AwsEventStreamContext>(container);
eventPipeline.UseSqs(sqs => sqs.UseMessageHandlers());

var app = builder.Build();
app.MapGet("/hello/{name}", (string name) => new { greeting = $"Hello {name}" });
await app.StartAsync();          // 3. LambdaServer captures the IHttpApplication here — required

// 4. Both sides take the SAME provider.
var aspNet  = new MyAspNetFunction(app.Services);     // : APIGatewayHttpApiV2ProxyFunction(provider)
var benzene = new AwsLambdaEntryPoint(
    eventPipeline.Build(), new MicrosoftServiceResolverFactory(app.Services));
```

### Three things the spike caught that the design would not have

1. **`await app.StartAsync()` is mandatory.** `LambdaServer` captures the `IHttpApplication` in
   `StartAsync`; skip it and the ASP.NET path has nothing to dispatch into. It is a no-op otherwise —
   no socket is opened.
2. **`AddBenzene()` must be called explicitly.** *(Fixed at the source, 2026-07-30: `AddMessageHandlers`
   now pulls the `AddBenzene` baseline in, so this footgun is gone whether you hand-compose or use the
   helper — see below.)* The Lambda hosts call it for you via their startup path; composing by hand did
   not, and the failure was remote from the cause — an SQS record failing with `Unable to resolve service
   for type 'IDefaultStatuses'` from inside `MessageHandlerFactory`. The original reasoning here — "a
   composition helper in the package should call it" — is what motivated looking deeper: because the
   router/factory that needs the baseline are registered by `AddMessageHandlers`, that is where the
   baseline is now ensured, universally, rather than in each transport's helper.
3. **Errors surface only through `ILogger`.** `SqsApplication` catches per-record exceptions, logs
   them, and reports a batch-item failure. With logging cleared, a broken pipeline looks exactly like
   a message that simply did not route. Worth stating in the package docs.

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

The spike (§5) is done and the design holds, so this can be built directly. Its composition helper
should encapsulate the four steps in §5's recipe — particularly `AddBenzene()` and `StartAsync()`,
which are the two a hand-composer gets wrong and which fail far from their cause.
