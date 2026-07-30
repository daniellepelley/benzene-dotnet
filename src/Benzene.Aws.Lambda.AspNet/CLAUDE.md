# Benzene.Aws.Lambda.AspNet

## What this package does
Lets one Lambda function serve HTTP through a real ASP.NET Core application while consuming SQS, SNS,
EventBridge and the rest through Benzene — off one DI container, driven by `app.Run()`, with the wiring
collapsed to a single call:

```csharp
builder.Services.UsingBenzene(x => x.AddMessageHandlers(...).AddSqs().AddSns());

builder.Services.AddBenzeneAwsLambdaHosting(events => events
    .UseHttpBridgeV2()                          // HTTP -> ASP.NET
    .UseSqs(sqs => sqs.UseMessageHandlers())    // SQS  -> Benzene
    .UseSns(sns => sns.UseMessageHandlers()));  // SNS  -> Benzene

var app = builder.Build();
app.MapGet("/orders/{id}", (string id) => new { orderId = id });
app.Run();
```

That replaces the hand-composition the `Benzene.Aws.Lambda.HttpBridge` cookbook documents — a bridge
adapter class, an explicit `AddSingleton<IServer, LambdaServer>()`, a `MiddlewarePipelineBuilder`, an
`await app.StartAsync()`, and a hand-built `AwsLambdaEntryPoint` — each with a footgun that fails far
from its cause.

## How it works
- **`AddBenzeneAwsLambdaHosting(events => …)`** — configures the AWS event pipeline against the app's
  service collection (so `UseSqs`/`UseSns` register their services before the container is built),
  registers the built-in bridge, calls `AddBenzene()` for you, and — only inside Lambda — registers the
  Benzene-driven `IServer`.
- **`BenzeneAspNetBridge`** — the built-in `IAwsHttpBridge`, so a mixed function needs no adapter class.
  Derives from `APIGatewayHttpApiV2ProxyFunction` purely to be *called* (its `FunctionHandlerAsync` is
  `public virtual`, its `ctor(IServiceProvider)` `protected`). Resolved by `UseHttpBridgeV2()`'s no-arg form.
- **`BenzeneLambdaServer`** — a native re-creation of `Amazon.Lambda.AspNetCoreServer.Hosting`'s internal
  `LambdaRuntimeSupportServer`, with the handler swapped from ASP.NET's HTTP-only one to the Benzene entry
  point. Its `StartAsync` lets the base `LambdaServer` capture the ASP.NET `IHttpApplication`, then runs
  the bootstrap loop via `Benzene.Aws.Lambda.Hosting`. `app.Run()` blocks there pumping invocations —
  exactly the shape of `AddAWSLambdaHosting`, but with Benzene as the top-level dispatcher.

## Why not reference `Amazon.Lambda.AspNetCoreServer.Hosting`
That package's `AddAWSLambdaHosting` takes over the runtime loop with an HTTP-only handler — precisely
what a mixed function cannot allow. Re-creating its ~30-line runtime-support server natively is what lets
`Main` stay `app.Run()` while Benzene owns dispatch. We reference `Amazon.Lambda.AspNetCoreServer` (the
class-library model: `LambdaServer` + `APIGatewayHttpApiV2ProxyFunction`) and nothing from `.Hosting`.

## This is the one package that references ASP.NET
It references `Amazon.Lambda.AspNetCoreServer` and the `Microsoft.AspNetCore.App` framework, so it drags
the ASP.NET hosting stack into whatever references it. **It is never a dependency of another Benzene
package** — the port (`Benzene.Aws.Lambda.HttpBridge`) and the ASP.NET-free loop
(`Benzene.Aws.Lambda.Hosting`) stay clean; this adapter is where ASP.NET is quarantined.

## When to use this package
- A Lambda that must serve an existing ASP.NET app (controllers, auth middleware, static files) *and*
  consume queues/events through Benzene, wired the way `AddAWSLambdaHosting` + `app.Run()` feels.
- Not needed if Benzene serves the HTTP itself (`Benzene.Aws.Lambda.ApiGateway`), or for a pure-Benzene
  function with no ASP.NET (`Benzene.Aws.Lambda.Hosting`).

## Dependencies on other Benzene packages
- **Benzene.Aws.Lambda.Hosting** — the bootstrap loop the server drives.
- **Benzene.Aws.Lambda.HttpBridge** — `IAwsHttpBridge` and `UseHttpBridgeV2()`.
- **Benzene.Microsoft.Dependencies** — `MicrosoftBenzeneServiceContainer`, `MicrosoftServiceResolverFactory`.

## Important conventions
- The Benzene `IServer` is registered only inside Lambda (detected via `AWS_LAMBDA_RUNTIME_API`); locally
  Kestrel keeps serving so `dotnet run` still hosts the HTTP endpoints. The full mixed behaviour (queues
  + HTTP off one loop) is a Lambda-runtime concern.
- Both sides share the application's `IServiceProvider`; the per-invocation `MicrosoftServiceResolverFactory`
  does not own it, so it is not disposed out from under ASP.NET.
