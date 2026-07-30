# ASP.NET Core + SQS + SNS in one Lambda

Serve HTTP through a real ASP.NET Core application while the *same* Lambda function consumes SQS and
SNS through Benzene — one process, one DI container, one deployment, wired the way
`AddAWSLambdaHosting` + `app.Run()` already feels.

## Problem Statement

You have an ASP.NET Core application you are not going to rewrite: controllers, authentication
middleware, filters, maybe static files. You also need that service to consume a queue and a
notification topic. The obvious options are both bad — deploy a second Lambda and duplicate every
registration, or reimplement the HTTP surface on Benzene's own binding.

Neither is necessary. Benzene's Lambda entry point takes a raw `Stream` and runs a chain where each
binding claims the payload shapes it recognises, so ASP.NET is **one more claimant in a chain that
already works this way**:

```
Lambda handler ──► AwsLambdaEntryPoint (Stream)
                     │
                     ├─ HttpBridge  ─► your ASP.NET Core app   (claims API Gateway payloads)
                     ├─ UseSqs      ─► Benzene SQS pipeline
                     └─ UseSns      ─► Benzene SNS pipeline
```

`Benzene.Aws.Lambda.AspNet` packages that up. You keep `app.Run()` — Benzene, not ASP.NET, owns the
Lambda runtime loop underneath it, and HTTP still flows into your ASP.NET pipeline.

## Prerequisites

- An ASP.NET Core application targeting .NET 10
- An API Gateway HTTP API (payload format 2.0), an SQS queue, and an SNS topic

## Installation

```bash
dotnet add package Benzene.Aws.Lambda.AspNet --prerelease
dotnet add package Benzene.Aws.Lambda.Sqs --prerelease
dotnet add package Benzene.Aws.Lambda.Sns --prerelease
```

`Benzene.Aws.Lambda.AspNet` brings in `Amazon.Lambda.AspNetCoreServer` (the class-library hosting
model) for you. You do **not** add `Amazon.Lambda.AspNetCoreServer.Hosting`/`AddAWSLambdaHosting`
yourself — the package re-creates the ~30 lines of it that matter, with Benzene as the top-level
dispatcher instead of an HTTP-only handler.

## Step-by-Step Implementation

### 1. Write the message handlers

Ordinary Benzene handlers — nothing about them knows a bridge exists:

```csharp
[Message("order:created")]
public class OrderCreatedHandler : IMessageHandler<OrderCreated, OrderAccepted>
{
    public Task<IBenzeneResult<OrderAccepted>> HandleAsync(OrderCreated message)
    {
        return Task.FromResult(BenzeneResult.Ok(new OrderAccepted { OrderId = message.OrderId }));
    }
}

[Message("order:shipped")]
public class OrderShippedHandler : IMessageHandler<OrderShipped>
{
    public Task HandleAsync(OrderShipped message)
    {
        // SNS delivery is fire-and-forget, so this handler returns no response
        return Task.CompletedTask;
    }
}
```

### 2. Wire the function

This is the whole thing. `UsingBenzene(...)` registers your handlers and transports;
`AddBenzeneAwsLambdaHosting(...)` configures the event pipeline, wires the built-in HTTP bridge, and —
inside Lambda — hands the runtime loop to Benzene:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.UsingBenzene(x => x
    .AddMessageHandlers(typeof(OrderCreatedHandler).Assembly)
    .AddSqs()
    .AddSns());

builder.Services.AddBenzeneAwsLambdaHosting(events => events
    .UseHttpBridgeV2()                          // HTTP -> ASP.NET
    .UseSqs(sqs => sqs.UseMessageHandlers())    // SQS  -> Benzene
    .UseSns(sns => sns.UseMessageHandlers()));  // SNS  -> Benzene

var app = builder.Build();

app.MapGet("/orders/{id}", (string id) => new { orderId = id, status = "ok" });
// ...or app.MapControllers(), app.UseAuthentication(), whatever you already have

app.Run();
```

Both HTTP and the queues run off the **one** `IServiceProvider` `builder.Build()` produces — your
stores and clients are singletons that exist once, and a handler invoked over SQS sees the same
registrations the HTTP path sees.

Two things the package does for you that a hand-composed function is prone to getting wrong:

- **`AddBenzene()` is called for you.** Composing by hand and forgetting it fails only on the queue
  path — a clean `200` on HTTP while every SQS record is redriven — see [Troubleshooting](#troubleshooting).
- **The ASP.NET pipeline is captured for you.** There is no `await app.StartAsync()` to remember; the
  Benzene-driven `IServer` captures it when `app.Run()` starts.

**Register `UseHttpBridgeV2()` *or* `UseApiGatewayV2(...)`, never both.** They claim the same payload
shapes, and a function serves a given shape from one place or the other. Everything non-HTTP is
unaffected. The bridge's detection rules are lifted from Benzene's own routers, so bridging changes
*who serves* an event, never *which events are served*.

### 3. Point all three event sources at the one function

.NET has no managed Lambda runtime, so the function ships as a self-contained executable on
`provided.al2023` — `app.Run()` is the custom-runtime entry point.

```yaml
Resources:
  MixedFunction:
    Type: AWS::Serverless::Function
    Properties:
      Runtime: provided.al2023
      Handler: bootstrap
      Events:
        HttpApi:
          Type: HttpApi
        Orders:
          Type: SQS
          Properties:
            Queue: !GetAtt OrdersQueue.Arn
            FunctionResponseTypes: [ReportBatchItemFailures]
        Shipped:
          Type: SNS
          Properties:
            Topic: !Ref ShippedTopic
```

`ReportBatchItemFailures` is what makes Benzene's partial-batch-failure reporting effective — without
it a single bad record redrives the whole batch. See
[Handling SQS Message Failures](handling-sqs-failures.md).

## Running locally

`dotnet run` still hosts your HTTP endpoints on Kestrel: `AddBenzeneAwsLambdaHosting` takes over the
`IServer` **only inside Lambda** (detected via `AWS_LAMBDA_RUNTIME_API`), so local development is an
ordinary ASP.NET run. The queue/notification paths are exercised in the Lambda runtime (and in the
in-memory test below), not by the local Kestrel host.

## Testing all three paths in memory

The whole point of one function is that one test host exercises all of it. Build the same event
pipeline the package builds, then drive it through the entry point:

```csharp
var services = new ServiceCollection();
services.AddLogging();
services.UsingBenzene(x => x
    .AddBenzene()
    .AddMessageHandlers(typeof(OrderCreatedHandler).Assembly)
    .AddSqs()
    .AddSns());
services.AddSingleton<IAwsHttpBridge<APIGatewayHttpApiV2ProxyRequest, APIGatewayHttpApiV2ProxyResponse>>(
    _ => new StubBridge());     // stand in for the ASP.NET app; assert HTTP is claimed, not what it returns

var container = new MicrosoftBenzeneServiceContainer(services);
var pipeline = new MiddlewarePipelineBuilder<AwsEventStreamContext>(container);
pipeline.UseHttpBridgeV2().UseSqs(sqs => sqs.UseMessageHandlers()).UseSns(sns => sns.UseMessageHandlers());

var entryPoint = new AwsLambdaEntryPoint(pipeline.Build(), new MicrosoftServiceResolverFactory(services));
var host = new AwsLambdaBenzeneTestHost(entryPoint);

var http = await host.SendEventAsync<APIGatewayHttpApiV2ProxyResponse>(apiGatewayV2Request);
Assert.Equal(200, http.StatusCode);

var sqs = await host.SendSqsAsync(MessageBuilder.Create("order:created", new OrderCreated { OrderId = "1" }));
Assert.Empty(sqs.BatchItemFailures);

await host.SendEventAsync(MessageBuilder.Create("order:shipped", new OrderShipped { OrderId = "1" }).AsSns());
```

Testing the real ASP.NET pipeline end-to-end is an ASP.NET concern (`WebApplicationFactory`); the test
above is about proving the *routing* — HTTP claimed, queues routed — on one function. See
[Testing Lambda Functions](testing-lambda-functions.md).

## Troubleshooting

**HTTP works, queues do not — every SQS record is a batch item failure with**

```
Benzene.Core.Exceptions.BenzeneException: Unable to resolve type MessageRouter`1[[SqsMessageContext, …]]
 ---> System.InvalidOperationException: Unable to resolve service for type 'IDefaultStatuses'
      while attempting to activate 'MessageHandlerFactory'.
```

You called `AddBenzene()` nowhere and something removed the one `AddBenzeneAwsLambdaHosting` adds — or
you are composing the entry point entirely by hand (see [Under the hood](#under-the-hood-the-http-bridge-port)).
The helper calls `AddBenzene()` for you; hand-composition does not. If a service is green on HTTP and
silently redriving its queue to the DLQ, check this first.

**A broken queue pipeline looks exactly like a message that did not route.** `SqsApplication` catches
per-record exceptions, logs them, and reports a batch item failure — so with no logging provider
configured, a completely broken pipeline is byte-identical on the wire to a message with no matching
handler (`{"batchItemFailures":[{"itemIdentifier":"…"}]}`). Do not clear the logging providers on this
path. See [Diagnosing Failures](../diagnosing-failures.md).

## Trade-offs

**Cold start.** A full ASP.NET Core host inside a Lambda is materially heavier than Benzene's own
HTTP binding, which exists partly to avoid it. If you already run ASP.NET in Lambda you lose nothing;
if you are on Benzene's binding today, this is not an upgrade. See
[Lambda Cold Start Optimization](lambda-cold-start-optimization.md).

**Two scope models.** ASP.NET scopes per `HttpContext`; Benzene scopes per invocation, and per
*record* for batches. They never overlap inside one invocation — an event is either HTTP or it is not
— so this is safe, but do not assume a scoped service spans both paths.

**You can have both HTTP models.** Choosing ASP.NET for the front door does not mean abandoning
`[HttpEndpoint]` handlers: `Benzene.AspNet.Core` mounts Benzene *inside* the ASP.NET pipeline
(`app.UseBenzene(b => b.UseHttp(...))`), so the same handlers stay reachable over HTTP and over
queues. See [ASP.NET Core](../asp-net-core.md).

## Fronting the function with a REST API or an ALB

The built-in bridges cover all three front-door shapes — API Gateway **HTTP API (payload format
2.0)**, API Gateway **REST API (payload format 1.0)**, and **Application Load Balancer** — so switching
is a one-word change in the `events` pipeline, with no adapter class either way:

```csharp
builder.Services.AddBenzeneAwsLambdaHosting(events => events
    .UseHttpBridge()                            // REST API (payload format 1.0)
    .UseSqs(sqs => sqs.UseMessageHandlers()));

// ...or, behind an ALB:
builder.Services.AddBenzeneAwsLambdaHosting(events => events
    .UseHttpBridgeAlb()                         // Application Load Balancer
    .UseSqs(sqs => sqs.UseMessageHandlers()));
```

`AddBenzeneAwsLambdaHosting` registers all three built-in bridges; each is only constructed if its
`UseHttpBridge*()` is in the pipeline, so picking a front door is just which line you write.

ALB is a **distinct payload pair, not a flavour of API Gateway**. The request carries
`requestContext.elb`, and the response *requires* `statusDescription` — a field the API Gateway
response type does not have, so an ALB target answering in the API Gateway shape returns 502. The
built-in `UseHttpBridgeAlb()` uses the ALB response type, so that is handled for you.

**If a single function is fronted by both an ALB and a REST API, register `UseHttpBridgeAlb()` before
`UseHttpBridge()`.** An ALB payload also deserializes into an `APIGatewayProxyRequest` with
`HttpMethod` populated, and the REST rule — inherited unchanged from Benzene's own API Gateway router —
accepts exactly that, so whichever is registered first claims it. Get it backwards and the REST bridge
answers ALB traffic without `statusDescription`, which is a 502 in production and completely silent in
a unit test. Most functions are fronted by one or the other and never meet this.

## Under the hood: the HTTP bridge port

`Benzene.Aws.Lambda.AspNet` is a thin adapter over two things you can also use directly:

- **`Benzene.Aws.Lambda.HttpBridge`** — the *port*. A one-method interface,
  `IAwsHttpBridge<TRequest, TResponse>`, that Benzene hands HTTP-shaped invocations to, plus the
  `UseHttpBridge*` registrations. It references only `Benzene.Aws.Lambda.Core` and the API Gateway
  event POCOs — no ASP.NET — so it never drags the hosting stack into anything. The AspNet package
  supplies the ASP.NET *adapters* (`BenzeneAspNetBridge`, `BenzeneAspNetRestBridge`,
  `BenzeneAspNetAlbBridge` — two-line `AbstractAspNetCoreFunction` subclasses, one per payload shape)
  so you do not have to write any.
- **`Benzene.Aws.Lambda.Hosting`** — the ASP.NET-free bootstrap loop
  (`AwsLambdaBootstrap.RunAsync<StartUp>()`), for a pure-Benzene function with no ASP.NET at all. The
  AspNet package drives the same loop from inside `app.Run()`.

If you need to own the composition — a custom `IServer`, a bridge the package does not provide, or a
non-ASP.NET HTTP stack — build directly on the port. The pattern is the same chain of claimants shown
above; `AddBenzeneAwsLambdaHosting` is just the assembled common case.

## See Also

- [Getting Started: AWS Lambda](../getting-started-aws.md) — the event sources, and Benzene's own
  API Gateway binding
- [Handling SQS Message Failures](handling-sqs-failures.md) — partial batch failures and DLQ patterns
- [SNS Fan-Out Pattern](sns-fan-out.md) — and why `SnsOptions.RaiseOnFailureStatus` matters
- [ASP.NET Core](../asp-net-core.md) — running Benzene inside an ASP.NET Core pipeline
- [Diagnosing Failures](../diagnosing-failures.md) — what reaches your logs, per transport
```
