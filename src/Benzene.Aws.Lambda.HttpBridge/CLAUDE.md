# Benzene.Aws.Lambda.HttpBridge

## What this package does
Lets a Lambda serve HTTP through an application Benzene does not own — typically ASP.NET Core hosted
by `Amazon.Lambda.AspNetCoreServer` — while Benzene handles SQS/SNS/EventBridge on the *same*
function, against the *same* DI container.

It is a **port, not an integration**. Benzene owns the claiming (recognising an HTTP payload among
the other events arriving at the function) and hands the request over; the application supplies the
adapter. That is the whole reason this package exists in this shape.

## Why a bridge instead of an ASP.NET package
An `…AspNetCore` integration package would reference `Amazon.Lambda.AspNetCoreServer` and drag the
ASP.NET hosting stack into everything that referenced it, and would need re-releasing whenever that
stack moved. This package references `Amazon.Lambda.APIGatewayEvents` (POCOs) and
`Benzene.Aws.Lambda.Core`, and **nothing else** — verified: no ASP.NET assembly appears anywhere in
its transitive graph. The ASP.NET-specific code is a handful of lines in the consuming application,
which already has ASP.NET on hand.

## Key types
- `IAwsHttpBridge<TRequest, TResponse>` — the port. One method: `HandleAsync(request, ILambdaContext)`.
- `HttpBridgeLambdaHandler<TRequest, TResponse>` — the `AwsEventStreamContext` middleware that claims
  HTTP-shaped invocations and delegates. Same chain as `SqsLambdaHandler` and friends.
- `Extensions.UseHttpBridgeV2(...)` / `UseHttpBridge(...)` / `UseHttpBridgeAlb(...)` — registration,
  in delegate and DI-resolved forms, for API Gateway payload format 2.0, format 1.0, and Application
  Load Balancer respectively.

## When to use this package
This is the **port**, referenced directly when you need to own the composition — a bridge to a
non-ASP.NET HTTP stack, a hand-written HTTP handler, or a custom `IServer`.

**For the common ASP.NET-Core-in-one-Lambda case, reach for `Benzene.Aws.Lambda.AspNet` instead.** It
references this port and supplies the ASP.NET adapter (`BenzeneAspNetBridge`), the Benzene-driven
`IServer`, and a one-call `AddBenzeneAwsLambdaHosting(events => …)` that drives the whole thing from
`app.Run()` — none of the hand-composition below. This package stays the ASP.NET-free port underneath it.

Not needed at all if Benzene serves the HTTP itself — use `Benzene.Aws.Lambda.ApiGateway`. Its routers
and this bridge claim the same payload shapes, so register **one or the other**, never both.

## Composition (the part that is easy to get wrong — done for you by `Benzene.Aws.Lambda.AspNet`)
Hand-composed, a mixed function is:
```csharp
builder.Services.AddSingleton<IServer, LambdaServer>();      // replaces Kestrel
builder.Services.UsingBenzene(x => x.AddBenzene()…);         // AddBenzene() is required by hand
builder.Services.AddSingleton<IAwsHttpBridge<…>>(sp => new AspNetBridge(sp));

pipeline.UseHttpBridgeV2().UseSqs(sqs => sqs.UseMessageHandlers());

var app = builder.Build();
await app.StartAsync();     // LambdaServer captures the IHttpApplication here — required
```
Three failure modes worth knowing, all remote from their cause and all recorded in
`work/aws-lambda-aspnetcore-research.md`: skipping `StartAsync()` leaves the HTTP path with nothing
to dispatch into; skipping `AddBenzene()` surfaces as `IDefaultStatuses` unresolvable from inside
`MessageHandlerFactory`; and per-record SQS errors surface only through `ILogger`, so a cleared
logging pipeline makes a broken handler look like a message that simply did not route. The first two
are exactly what `AddBenzeneAwsLambdaHosting` removes — it captures the pipeline and calls
`AddBenzene()` for you; the third is inherent to the SQS transport and stated in its docs.

## Dependencies on other Benzene packages
- **Benzene.Aws.Lambda.Core** — the event-stream pipeline and router base.

## Important conventions
- API Gateway detection reuses the rules of `ApiGatewayV2LambdaHandler`/`ApiGatewayLambdaHandler`,
  so bridging changes *who serves* an event, never *which events are served*.
- **ALB's rule is derived, not inherited** — Benzene has no ALB binding to copy. It keys on
  `requestContext.elb`, which is on every ALB invocation and nothing else.
- **Register `UseHttpBridgeAlb()` before `UseHttpBridge()`** if one function serves both. An ALB
  payload also deserializes into an `APIGatewayProxyRequest` with `HttpMethod` set, and the REST rule
  (inherited unchanged) accepts exactly that, so registration order decides. Getting it wrong is not
  subtle in production and is silent in a unit test: the REST bridge answers without
  `statusDescription`, which a real ALB rejects with a 502. Both orderings are pinned in
  `HttpBridgeAlbTest`.
