# ASP.NET Core + SQS + SNS in one Lambda

Serve HTTP through a real ASP.NET Core application while the *same* Lambda function consumes SQS and
SNS through Benzene — one process, one DI container, one deployment.

## Problem Statement

You have an ASP.NET Core application you are not going to rewrite: controllers, authentication
middleware, filters, maybe static files. You also need that service to consume a queue and a
notification topic. The obvious options are both bad — deploy a second Lambda and duplicate every
registration, or reimplement the HTTP surface on Benzene's own binding.

Neither is necessary. Benzene's Lambda entry point takes a raw `Stream` and runs a chain where each
binding claims the payload shapes it recognises, so ASP.NET can be **one more claimant in a chain
that already works this way**:

```
Lambda handler ──► AwsLambdaEntryPoint (Stream)
                     │
                     ├─ HttpBridgeLambdaHandler ─► your ASP.NET Core app
                     │    claims API Gateway payloads
                     ├─ SqsLambdaHandler        ─► Benzene SQS pipeline
                     └─ SnsLambdaHandler        ─► Benzene SNS pipeline
```

This cookbook covers wiring that up, testing all three paths in memory, and the three ways it goes
wrong — each of which fails a long way from its cause.

> **`Benzene.Aws.Lambda.HttpBridge` does not reference ASP.NET.** It is a *port*: a one-method
> interface Benzene hands HTTP-shaped invocations to. Its only dependencies are
> `Benzene.Aws.Lambda.Core` and the API Gateway event POCOs, so it never drags the hosting stack into
> anything, and it does not need re-releasing when that stack moves. The ASP.NET-specific code is the
> ten-line adapter in step 2 — in your project, which already has ASP.NET on hand.

## Prerequisites

- An ASP.NET Core application targeting .NET 10
- An API Gateway HTTP API (payload format 2.0), an SQS queue, and an SNS topic

## Installation

```bash
dotnet add package Benzene.Aws.Lambda.HttpBridge --prerelease
dotnet add package Benzene.Aws.Lambda.Sqs --prerelease
dotnet add package Benzene.Aws.Lambda.Sns --prerelease
dotnet add package Amazon.Lambda.AspNetCoreServer
```

`Amazon.Lambda.AspNetCoreServer` is the **class-library** hosting model, and that is the one you
want. Do **not** use `Amazon.Lambda.AspNetCoreServer.Hosting`/`AddAWSLambdaHosting` here: it takes
over `Main` and starts the Lambda runtime loop with an HTTP-only handler, which is precisely what a
mixed function cannot allow.

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

### 2. Write the bridge adapter

This is the only ASP.NET-aware code, and it is the whole integration. It works because
`AbstractAspNetCoreFunction.FunctionHandlerAsync` is `public virtual` and its `ctor(IServiceProvider)`
is `protected` — so the function can be *called* by us rather than having to *be* the Lambda entry
point:

```csharp
public class AspNetBridge : APIGatewayHttpApiV2ProxyFunction,
    IAwsHttpBridge<APIGatewayHttpApiV2ProxyRequest, APIGatewayHttpApiV2ProxyResponse>
{
    public AspNetBridge(IServiceProvider services) : base(services) { }

    Task<APIGatewayHttpApiV2ProxyResponse> IAwsHttpBridge<APIGatewayHttpApiV2ProxyRequest, APIGatewayHttpApiV2ProxyResponse>
        .HandleAsync(APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        => FunctionHandlerAsync(request, context);
}
```

For a REST API (payload format 1.0) derive from `APIGatewayProxyFunction` and implement
`IAwsHttpBridge<APIGatewayProxyRequest, APIGatewayProxyResponse>` instead.

### 3. Compose the function

Both sides take the **same `IServiceProvider`**. That matters beyond tidiness: your stores and
clients are singletons that must not exist twice in one process, and a handler invoked over SQS has
to see the same registrations the HTTP path sees.

```csharp
var builder = WebApplication.CreateBuilder();

// LambdaServer replaces Kestrel. Registered after CreateBuilder so it wins the IServer resolve.
builder.Services.AddSingleton<IServer, LambdaServer>();

builder.Services.UsingBenzene(x => x
    .AddBenzene()                                          // required — see Troubleshooting
    .AddMessageHandlers(typeof(OrderCreatedHandler).Assembly)
    .AddSqs()
    .AddSns());

builder.Services.AddSingleton<IAwsHttpBridge<APIGatewayHttpApiV2ProxyRequest, APIGatewayHttpApiV2ProxyResponse>>(
    sp => new AspNetBridge(sp));

var container = new MicrosoftBenzeneServiceContainer(builder.Services);
var eventPipeline = new MiddlewarePipelineBuilder<AwsEventStreamContext>(container);

eventPipeline
    .UseHttpBridgeV2()                                     // HTTP  -> ASP.NET
    .UseSqs(sqs => sqs.UseMessageHandlers())               // SQS   -> Benzene
    .UseSns(sns => sns.UseMessageHandlers());              // SNS   -> Benzene

var app = builder.Build();

app.MapGet("/orders/{id}", (string id) => new { orderId = id, status = "ok" });
// ...or app.MapControllers(), app.UseAuthentication(), whatever you already have

await app.StartAsync();                                    // required — see Troubleshooting
```

`LambdaServer` lives in `Amazon.Lambda.AspNetCoreServer.Internal`. Despite the namespace it is a
public type with a public parameterless constructor, and registering it directly is what lets a mixed
function avoid `AddAWSLambdaHosting`.

**Register `UseHttpBridgeV2()` *or* `UseApiGatewayV2(...)`, never both.** They claim the same payload
shapes, and a function serves a given shape from one place or the other. Everything non-HTTP is
unaffected. The bridge's detection rules are lifted from Benzene's own routers, so bridging changes
*who serves* an event, never *which events are served*.

### 4. Wire the Lambda entry point

```csharp
var entryPoint = new AwsLambdaEntryPoint(
    eventPipeline.Build(),
    new MicrosoftServiceResolverFactory(app.Services));
```

Expose `entryPoint.FunctionHandlerAsync(Stream, ILambdaContext)` as your function handler. AWS builds
its ASP.NET host once and caches it, so there is no per-invocation host cost.

### 5. Point all three event sources at the one function

```yaml
Resources:
  MixedFunction:
    Type: AWS::Serverless::Function
    Properties:
      Handler: MyFunction::MyFunction.Function::FunctionHandlerAsync
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

## Testing all three paths in memory

The whole point of one function is that one test host exercises all of it:

```csharp
var host = new AwsLambdaBenzeneTestHost(entryPoint);

var http = await host.SendEventAsync<APIGatewayHttpApiV2ProxyResponse>(apiGatewayV2Request);
Assert.Equal(200, http.StatusCode);

var sqs = await host.SendSqsAsync(MessageBuilder.Create("order:created", new OrderCreated { OrderId = "1" }));
Assert.Empty(sqs.BatchItemFailures);

await host.SendEventAsync(MessageBuilder.Create("order:shipped", new OrderShipped { OrderId = "1" }).AsSns());
```

See [Testing Lambda Functions](testing-lambda-functions.md) for the general pattern.

## Troubleshooting

Three failure modes, all of them remote from their cause. Each symptom below is what the code
actually produces, not what it ought to.

**Every HTTP request throws `NullReferenceException`.** You skipped `await app.StartAsync()`.
`LambdaServer` captures the `IHttpApplication` there, so without it the HTTP path has nothing to
dispatch into. It opens no socket and costs nothing — it is not optional just because there is no
server to start.

**HTTP works, queues do not.** You left out `.AddBenzene()`. This one is nasty because the two paths
fail *differently*: a smoke test against the HTTP endpoint returns a clean `200` while every SQS
record comes back as a batch item failure with

```
Benzene.Core.Exceptions.BenzeneException: Unable to resolve type MessageRouter`1[[SqsMessageContext, …]]
 ---> System.InvalidOperationException: Unable to resolve service for type 'IDefaultStatuses'
      while attempting to activate 'MessageHandlerFactory'.
```

The Lambda hosts call `AddBenzene()` for you; composing by hand does not. If your service is green on
HTTP and silently redriving its queue to the DLQ, check this first.

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

## Limitations

ALB (`ApplicationLoadBalancerRequest`) is not wired up yet — the bridge currently covers API Gateway
payload formats 1.0 and 2.0. It is the same shape to add.

## See Also

- [Getting Started: AWS Lambda](../getting-started-aws.md) — the event sources, and Benzene's own
  API Gateway binding
- [Handling SQS Message Failures](handling-sqs-failures.md) — partial batch failures and DLQ patterns
- [SNS Fan-Out Pattern](sns-fan-out.md) — and why `SnsOptions.RaiseOnFailureStatus` matters
- [ASP.NET Core](../asp-net-core.md) — running Benzene inside an ASP.NET Core pipeline
- [Diagnosing Failures](../diagnosing-failures.md) — what reaches your logs, per transport
