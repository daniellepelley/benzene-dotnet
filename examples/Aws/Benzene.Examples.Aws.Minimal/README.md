# Benzene AWS Lambda — Minimal Example

The smallest AWS Lambda that shows Benzene's central promise: **one message handler, reached over four
event sources from a single function** — API Gateway (HTTP), SNS, SQS and EventBridge. It's the runnable
companion to [docs/getting-started-aws.md](../../../docs/getting-started-aws.md).

## The one handler

[`PlaceOrderMessageHandler`](PlaceOrderMessageHandler.cs) handles the `order:placed` topic and knows
nothing about AWS. Over API Gateway the response is returned to the caller; over SNS/SQS/EventBridge it's
fire-and-forget. You would carry this file over verbatim if you moved to Azure or ASP.NET — only the host
wiring changes.

```csharp
[Message("order:placed")]
[HttpEndpoint("POST", "/orders")]
public class PlaceOrderMessageHandler : IMessageHandler<OrderPlaced, OrderAccepted> { ... }
```

## The wiring

[`StartUp`](StartUp.cs) is the platform-neutral `BenzeneStartUp`; everything AWS-specific is one block:

```csharp
app.UseAwsLambda(aws =>
{
    var pipeline = aws.Create<BenzeneMessageContext>().UseMessageHandlers(_ => { });
    aws.UseBenzeneMessage(pipeline);                                    // the envelope
    aws.UseApiGateway(api => api.UseMessageHandlers(_ => { }));         // HTTP: POST /orders
    aws.UseSns(sns => sns.UseMessageHandlers(_ => { }));               // topic in the "topic" attribute
    aws.UseSqs(sqs => sqs.UseMessageHandlers(_ => { }));
    aws.UseEventBridge(eb => eb.UseMessageHandlers(_ => { }));         // topic = detail-type
});
```

Adding a transport is one `ProjectReference` and one `UseXxx` line — never a change to the handler. The
version travels as metadata (`topic` message attribute on SQS/SNS, the HTTP route on API Gateway, the
event's `detail-type` on EventBridge).

## Run the tests

```bash
dotnet test examples/Aws/Benzene.Examples.Aws.Minimal.Tests/Benzene.Examples.Aws.Minimal.Tests.csproj
```

Everything is in-memory — no AWS account, no localstack. `BenzeneTestHost.Create<StartUp>()` boots the
real `StartUp` the same way a deployed Lambda would, and each test pushes a native transport event
(API Gateway / SNS / SQS / EventBridge / envelope) through the front door and asserts the one handler ran.
It's built and its tests run in CI's `examples-build` job, so a `src/` change that breaks it fails the build.
