# Getting Started

Benzene's promise is **write your message handler once, host it anywhere**. Your logic lives in a
handler that knows nothing about HTTP, Lambda, or queues; a thin bit of host wiring connects it to
whatever platform you deploy on. So the fastest way to get started is to **start on the platform you're
actually deploying to** — pick it below.

> **Just want to see the shape first?** Every guide builds the same tiny thing and differs only in the
> host wiring. If you have no platform in mind yet, the [AWS Lambda](getting-started-aws.md) guide is the
> best tour of what makes Benzene worth it — one function, many event sources.

## Pick your platform

| You're deploying to… | Guide | Runnable example |
|---|---|---|
| **AWS Lambda** — API Gateway, SQS, SNS, EventBridge (the flagship: one function, every event source) | [Getting Started: AWS Lambda](getting-started-aws.md) | [`examples/Aws/Benzene.Examples.Aws.Minimal`](../examples/Aws/Benzene.Examples.Aws.Minimal) |
| **Azure Functions** — HTTP + Service Bus, Event Hubs, Event Grid, Cosmos DB, Timer | [Getting Started: Azure Functions](azure-functions.md) | [`examples/Azure`](../examples/Azure) |
| **Google Cloud Functions** — HTTP + Pub/Sub | [Getting Started: Google Cloud Functions](getting-started-google.md) | [`examples/Google`](../examples/Google) |
| **Kubernetes** — HTTP, SQS, Kafka (one handler, three independent Deployments) | [Getting Started: Kubernetes](getting-started-kubernetes.md) | [`examples/K8sTransports`](../examples/K8sTransports) |
| **ASP.NET Core** — a plain web app or API | [Getting Started: ASP.NET Core](getting-started-aspnet.md) | [`examples/Asp/Benzene.Example.Asp.Minimal`](../examples/Asp/Benzene.Example.Asp.Minimal) |

Other hosts have their own guides too: [Worker Services](getting-started-worker.md) (Kafka, HTTP,
Service Bus, Event Hubs, Cosmos DB Change Feed in a long-running process), [gRPC](getting-started-grpc.md),
[Kafka](getting-started-kafka.md), [RabbitMQ](getting-started-rabbitmq.md), and
[Cloudflare Containers](getting-started-cloudflare.md). Prefer scaffolding? The
[project templates](getting-started-templates.md) give you `dotnet new` starters for every host.

## The one idea they all share

Whichever guide you pick, you write the same three things — and only the third changes between platforms:

1. **A message handler** — your logic. It receives a typed request and returns a typed response wrapped
   in a [result](message-result.md). It knows nothing about the transport.

   ```csharp
   [Message("order:placed")]
   public class PlaceOrderMessageHandler : IMessageHandler<OrderPlaced, OrderAccepted>
   {
       public Task<IBenzeneResult<OrderAccepted>> HandleAsync(OrderPlaced message) { /* ... */ }
   }
   ```

2. **A topic** — the stable string (`order:placed`) every transport routes by. Handlers are discovered by
   reflection, so there's no routing table to maintain.

3. **The host wiring** — the *only* platform-specific part. On AWS it's `app.UseAwsLambda(aws => aws.UseApiGateway(...).UseSqs(...))`; on ASP.NET it's `app.UseBenzene(b => b.UseHttp(...))`; on Google Cloud it's `GoogleCloudFunctionHost<Startup>`. The handler above is byte-for-byte identical in every one.

That's the portability Benzene's hexagonal design buys you: **the handler is the asset; the host is a
detail.** See [Message Handlers](message-handlers.md) and [Middleware](middleware.md) for the full model,
and [Hosting](hosting.md) for how each platform is wired.

## Prerequisites (all platforms)

- [.NET 10 SDK](https://dotnet.microsoft.com/download) and any editor (Visual Studio, Rider, or VS Code).
- Benzene's packages are prerelease (`-alpha`) until 1.0, so `dotnet add package … --prerelease` is
  required. Each guide lists the specific packages for its host.

Ready? **[Pick your platform](#pick-your-platform)** and go.
