# Getting Started: Benzene on Kubernetes

This guide takes you from an empty folder to **one Benzene handler, reached over HTTP, SQS, and
Kafka, hosted in a single container** — one `Program.cs`, one Docker image, one Kubernetes
Deployment, dispatching every message from all three transports into the exact same handler class.
That's deliberately more than "deploy ASP.NET Core to a pod": see
[Why not just ASP.NET Core?](#why-not-just-aspnet-core) below for why a single-transport example
wouldn't actually show what Benzene is for here.

> **Runnable version:** this guide follows [`examples/K8sTransports`](https://github.com/daniellepelley/benzene-dotnet/tree/main/examples/K8sTransports) —
> a Dockerfile, a Kubernetes manifest, and a `docker-compose.yml` that runs all three legs locally
> against LocalStack + a throwaway Kafka broker, no cloud account needed.

## What you'll build

```
        HTTP        ─────────┐
        SQS queue   ─────────┼──▶  orders-app (Deployment)  ──▶  PlaceOrderMessageHandler
        Kafka topic ─────────┘
```

One handler project (`Domain`), referenced by one host project that wires Kestrel, an SQS poller, and
a Kafka consumer together — one container image, one Kubernetes Deployment.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Docker](https://docs.docker.com/get-docker/).
- A cluster and `kubectl` — [kind](https://kind.sigs.k8s.io/) is the quickest for local work
  (`kind create cluster`).
- To follow along with real messages rather than just reading: an SQS queue and a Kafka topic
  somewhere reachable (LocalStack and a throwaway broker via `docker compose` cover both with no
  account at all — see the [runnable example](https://github.com/daniellepelley/benzene-dotnet/tree/main/examples/K8sTransports)).

## 1. The shared handler

Everything downstream depends on this one file. Create the domain project:

```bash
mkdir -p orders/Domain && cd orders/Domain
dotnet new classlib -f net10.0
dotnet add package Benzene.Http --prerelease
```

`Benzene.Http` alone is enough here — it transitively brings in the pieces a handler needs
(`Benzene.Core.MessageHandlers` for `[Message]`, `Benzene.Abstractions` for `IBenzeneResult<T>`,
`Benzene.Results` for the `BenzeneResult` factory) plus `[HttpEndpoint]` itself, so the same class can
carry both attributes below.

```csharp
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Results;
using Microsoft.Extensions.Logging;

public record PlaceOrderRequest(string CustomerId, string Sku, int Quantity);

public record OrderPlaced(string OrderId, string Status);

[Message("order-place")]
[HttpEndpoint("POST", "/orders")]
public class PlaceOrderMessageHandler : IMessageHandler<PlaceOrderRequest, OrderPlaced>
{
    private readonly ILogger<PlaceOrderMessageHandler> _logger;

    public PlaceOrderMessageHandler(ILogger<PlaceOrderMessageHandler> logger)
    {
        _logger = logger;
    }

    public Task<IBenzeneResult<OrderPlaced>> HandleAsync(PlaceOrderRequest request)
    {
        var orderId = $"order-{Guid.NewGuid():N}"[..13];

        _logger.LogInformation(
            "order placed: {OrderId} - {Quantity}x {Sku} for {CustomerId}",
            orderId, request.Quantity, request.Sku, request.CustomerId);

        return BenzeneResult.Created(new OrderPlaced(orderId, "placed")).AsTask();
    }
}
```

Two attributes, two transports covered for free: `[Message("order-place")]` is the topic every
transport routes on; `[HttpEndpoint("POST", "/orders")]` additionally maps it onto a REST-shaped route
for the HTTP leg below. Nothing here mentions Kubernetes, SQS, Kafka, or HTTP status codes — that's
the whole point of a message handler in Benzene's hexagonal architecture: the domain logic sits behind
a port, and a transport is just an adapter in front of it.

The topic is `order-place`, not `order:place` — Benzene's usual colon convention (`order:create`,
`payment:take`) doesn't survive contact with Kafka, whose topic names may only contain letters,
digits, `.`, `_`, and `-`. Since the Kafka leg below routes on the record's *literal* topic name
against this same `[Message(...)]` value, the topic has to be spelled in a way all three transports
can use unmodified.

## 2. Host all three in one project

```bash
mkdir ../App && cd ../App
dotnet new worker -f net10.0
dotnet add package Benzene.AspNet.Core --prerelease
dotnet add package Benzene.HostedService --prerelease
dotnet add package Benzene.Aws.Sqs --prerelease
dotnet add package Benzene.Kafka.Core --prerelease
dotnet add reference ../Domain
rm Worker.cs   # the template's sample background service - Benzene's workers replace it
```

One project, one `BenzeneStartUp`, one `Configure` — and note that's `dotnet new worker`, not
`dotnet new web`. ASP.NET Core here is purely the HTTP host for Benzene — no controllers, no other
ASP.NET middleware — so it doesn't get to own the program shape *or* the project shape: `UseAspNet`
(`Benzene.AspNet.Core`) hosts Kestrel **as a worker**, a peer of `UseSqs` and `UseKafka`,
`Program.cs` is the plain generic host, and the ASP.NET shared-framework reference flows in
transitively through `Benzene.AspNet.Core`.

```csharp
// Startup.cs - the whole service
using Amazon.Runtime;
using Amazon.SQS;
using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Aws.Sqs;
using Benzene.Aws.Sqs.Consumer;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Http;
using Benzene.Kafka.Core;
using Benzene.Microsoft.Dependencies;
using Benzene.SelfHost;
using Confluent.Kafka;

public class Startup : BenzeneStartUp
{
    // Configuration defaults to environment variables (what the container injects) -
    // override GetConfiguration() only if you need more.

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // One registration for all three transports: the handler (with its [Message] +
        // [HttpEndpoint] attributes) and the HTTP route table built from it.
        services.UsingBenzene(x => x
            .AddMessageHandlers(new[] { typeof(PlaceOrderMessageHandler) })
            .AddHttpMessageHandlers());
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
        var sqsConfig = new SqsConsumerConfig
        {
            QueueUrl = configuration["QUEUE_URL"]
                ?? throw new InvalidOperationException("QUEUE_URL is not set"),
            MaxNumberOfMessages = 10,
        };

        // SQS_SERVICE_URL is only set for a LocalStack/emulator run (see the runnable example's
        // docker-compose.yml). Unset - the real-AWS/EKS case - AmazonSQSClient() falls through to the
        // SDK's default credential chain, e.g. an IRSA-mapped pod service account.
        var localEndpoint = configuration["SQS_SERVICE_URL"];
        var sqsClient = string.IsNullOrEmpty(localEndpoint)
            ? new AmazonSQSClient()
            : new AmazonSQSClient(new BasicAWSCredentials("local", "local"),
                new AmazonSQSConfig { ServiceURL = localEndpoint });

        var kafkaConfig = new BenzeneKafkaConfig
        {
            ConsumerConfig = new ConsumerConfig
            {
                BootstrapServers = configuration["KAFKA_BOOTSTRAP_SERVERS"]
                    ?? throw new InvalidOperationException("KAFKA_BOOTSTRAP_SERVERS is not set"),
                SecurityProtocol = SecurityProtocol.Plaintext,
                GroupId = "orders-kafka-worker",
                AutoOffsetReset = AutoOffsetReset.Earliest,
            },
            Topics = new[] { "order-place" },
        };

        // Three transports, three UseX calls, one worker host. Benzene.HostedService composes the
        // three workers into ONE IHostedService that starts/stops together. UseAspNet listens on the
        // port Kubernetes gives the container (the readinessProbe and Service target this).
        app.UseWorker(worker => worker
            .UseAspNet(
                asp => asp.UseMessageHandlers(),
                options => options.Urls = $"http://0.0.0.0:{configuration["PORT"] ?? "8080"}")
            .UseSqs(sqsConfig, new SqsClientFactory(sqsClient), sqs => sqs.UseMessageHandlers())
            .UseKafka<Ignore, string>(kafkaConfig, kafka => kafka.UseMessageHandlers()));
    }
}
```

```csharp
// Program.cs - the plain generic host, nothing ASP.NET-shaped
using Benzene.HostedService;

IHost host = Host.CreateDefaultBuilder(args)
    .UseBenzene<Startup>()
    .Build();

await host.RunAsync();
```

`UseAspNet`'s inner action is the exact same HTTP pipeline `UseHttp` builds (the `options` action is
where the URL — and, via `options.ConfigureBuilder`, TLS or Kestrel limits — comes from), and the
inner Kestrel host resolves nothing itself: handlers, singletons, and the pipeline all come from the
one container your `ConfigureServices` populated. A request the pipeline doesn't route is a 404 —
there are no controllers to fall through to in this mode.

That last sentence is also the boundary of this shape. If the process ever grows real ASP.NET
surface — controllers, minimal APIs, other middleware — switch the HTTP leg to the embedded mode:
`WebApplicationBuilder.UseBenzene<HttpStartup>()` with `app.UseHttp(...)` for the HTTP side, plus
`builder.Host.UseBenzene<WorkerStartup>()` for the workers, two startups sharing one
`builder.Services` (each platform's `UseX` no-ops on the other's builder, so one `Configure` can't
serve both there — see [Getting Started: ASP.NET Core](getting-started-aspnet.md)).

`SqsConsumer` (`Benzene.Aws.Sqs`) and the Kafka consumer (`Benzene.Kafka.Core`) are long-running
pollers, not Lambda/event-source triggers — the right shape for a pod that stays up. Each runs its
messages through the same middleware pipeline the HTTP leg uses; the SQS leg deletes only the
messages whose handler actually succeeded (`SqsConsumerAckMode.PerMessage`, the default), leaving a
failed or unrouted message on the queue for redelivery/DLQ redrive rather than silently dropping it.
See [Worker Service Setup](getting-started-worker.md) for the general shape self-hosted workers
follow and every other built-in one (RabbitMQ, Service Bus, Event Hub, Cosmos DB) available the same
way, and [Kafka Setup](getting-started-kafka.md) for `BenzeneKafkaConfig`'s other options.

## 3. Containerise it

One project, one `Dockerfile`, one image:

```dockerfile
# App/Dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish App/App.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "App.dll"]
```

```bash
docker build -f App/Dockerfile -t orders-app:local .
kind load docker-image orders-app:local
```

## 4. Deploy it

One `Deployment` + `Service` — the SQS and Kafka legs don't get their own, because nothing calls this
pod over either of them; it calls out:

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: orders-app
spec:
  replicas: 2
  selector: { matchLabels: { app: orders-app } }
  template:
    metadata: { labels: { app: orders-app } }
    spec:
      containers:
        - name: orders-app
          image: orders-app:local
          imagePullPolicy: IfNotPresent
          ports: [{ containerPort: 8080 }]
          env:
            - { name: PORT, value: "8080" }
            - { name: QUEUE_URL, value: "https://sqs.eu-west-1.amazonaws.com/<account-id>/orders" }
            - { name: KAFKA_BOOTSTRAP_SERVERS, value: "kafka-bootstrap.kafka.svc.cluster.local:9092" }
          readinessProbe:
            tcpSocket: { port: 8080 }
            initialDelaySeconds: 3
---
apiVersion: v1
kind: Service
metadata:
  name: orders-app
spec:
  selector: { app: orders-app }
  ports: [{ port: 80, targetPort: 8080 }]
```

```bash
kubectl apply -f k8s.yaml
kubectl get pods   # 2 pods: 2x orders-app
```

## 5. Watch the same handler run three ways

```bash
kubectl port-forward service/orders-app 8080:80 &
curl -X POST http://localhost:8080/orders \
  -H "Content-Type: application/json" -d '{"customerId":"cust-1","sku":"espresso","quantity":2}'
```

```json
{"orderId":"order-xxxxxxxxxxx","status":"placed"}
```

```bash
kubectl logs deploy/orders-app | tail -1
# order placed: order-xxxxxxxxxxx - 2x espresso for cust-1
```

Send a message to the SQS queue or the Kafka topic directly (from your own producer, `aws sqs
send-message`, `kafka-console-producer` — see [the runnable example](https://github.com/daniellepelley/benzene-dotnet/tree/main/examples/K8sTransports) for
exact commands against a local LocalStack/Kafka pair) and the **same log line** appears in the exact
same pod's logs, for a request that never touched HTTP. That's the proof: one handler, one container,
three transports.

```bash
kubectl scale deploy/orders-app --replicas=4   # scales all three transports' consuming capacity together
```

## Why not just ASP.NET Core?

A Kubernetes pod running plain ASP.NET Core already gives you an HTTP service on a `Deployment` — you
don't need Benzene, or this guide, for that alone. The reason to reach for Benzene here is what
happens the moment a second entry point shows up: a queue a partner team publishes to, a Kafka topic
another service already streams onto, a batch job that used to call your REST endpoint but really
just wants to drop a message and move on. Without a message-handler abstraction, each of those becomes
its own bespoke controller/consumer with its own copy (or its own subtly-diverged reimplementation) of
the same validation and business logic.

With Benzene, that second (and third) entry point is a few more lines in `WorkerStartup.Configure` —
the handler doesn't change, because it was never written against HTTP in the first place. That's what
section 2 above actually demonstrates: the identical `PlaceOrderMessageHandler`, unmodified, wired
onto three transports from one project.

> The `readinessProbe` above is a bare TCP check — wire a real health endpoint with
> [Kubernetes Health Checks](kubernetes-health-checks.md) (`Benzene.HealthChecks`) so the probe
> reflects your dependencies (queue reachability, broker reachability), not just process liveness.

## One container, or one per transport?

This guide combines all three transports into a single Deployment because `UseAspNet` makes that
shape essentially free — one startup, three `UseX` calls. It is not the *only* shape, though,
and it is not always the right one. Splitting the transports into **separate** Deployments (one for
HTTP, one for the SQS poller, one for the Kafka consumer, each its own `BenzeneStartUp`/`Program.cs`/
image) is a legitimate alternative: each transport then scales, rolls back, and fails independently —
a bad Kafka-worker deploy, or the Kafka leg falling behind under load, no longer risks the HTTP leg's
availability the way it does when a crash or a resource-starved process is shared between all three.
The tradeoff is real too: more images to build, more Deployments to manage, and a little duplicated
`Program.cs`/`Startup.cs` boilerplate per transport. `Domain/PlaceOrderMessageHandler.cs` doesn't
change either way — only how many `BenzeneStartUp`s and Dockerfiles end up wrapping it. Reach for
separate Deployments when the transports' traffic, failure modes, or scaling needs genuinely diverge;
reach for one container when they don't and the operational simplicity of a single image/Deployment
is worth more than that independence.

## Next steps

- **Health & readiness** — [Kubernetes Health Checks](kubernetes-health-checks.md).
- **Observability** — [Monitoring & Diagnostics](monitoring.md) and distributed tracing in the
  [Cookbooks](cookbooks/README.md).
- **More built-in workers** — [Worker Service Setup](getting-started-worker.md) covers RabbitMQ, Azure
  Service Bus, Event Hubs, and Cosmos DB Change Feed the same way SQS and Kafka are covered above.
- **A full mesh on Kubernetes** — [`examples/K8sMesh`](https://github.com/daniellepelley/benzene-dotnet/tree/main/examples/K8sMesh) deploys three
  *discovering* services plus the Mesh UI, credential-free on kind and on real EKS — a different
  demonstration (service-to-service discovery and calls) from this guide's (one handler, several
  inbound transports).
