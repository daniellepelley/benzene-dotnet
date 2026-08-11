# Getting Started: Benzene on Kubernetes

This guide takes you from an empty folder to **one Benzene handler running as three independent
Kubernetes Deployments** — an HTTP API, an SQS worker, and a Kafka worker — all dispatching into the
exact same handler class. That's deliberately more than "deploy ASP.NET Core to a pod": see
[Why not just ASP.NET Core?](#why-not-just-aspnet-core) below for why a single-transport example
wouldn't actually show what Benzene is for here.

> **Runnable version:** this guide follows [`examples/K8sTransports`](../examples/K8sTransports) —
> Dockerfiles, Kubernetes manifests, and a `docker-compose.yml` that runs all three legs locally
> against LocalStack + a throwaway Kafka broker, no cloud account needed.

## What you'll build

```
                              ┌──────────────────────────────────────┐
        HTTP  ──────────────▶│  orders-api           (Deployment)    │──┐
                              └──────────────────────────────────────┘  │
                              ┌──────────────────────────────────────┐  │   all three dispatch
        SQS queue  ─────────▶│  orders-sqs-worker    (Deployment)    │──┼──▶ PlaceOrderMessageHandler
                              └──────────────────────────────────────┘  │
                              ┌──────────────────────────────────────┐  │
        Kafka topic  ───────▶│  orders-kafka-worker  (Deployment)    │──┘
                              └──────────────────────────────────────┘
```

One handler project (`Domain`), referenced by three host projects, each its own container image,
each its own Kubernetes Deployment, each independently replicated and scaled.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Docker](https://docs.docker.com/get-docker/).
- A cluster and `kubectl` — [kind](https://kind.sigs.k8s.io/) is the quickest for local work
  (`kind create cluster`).
- To follow along with real messages rather than just reading: an SQS queue and a Kafka topic
  somewhere reachable (LocalStack and a throwaway broker via `docker compose` cover both with no
  account at all — see the [runnable example](../examples/K8sTransports)).

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
for the HTTP host below. Nothing here mentions Kubernetes, SQS, Kafka, or HTTP status codes — that's
the whole point of a message handler in Benzene's hexagonal architecture: the domain logic sits behind
a port, and a transport is just an adapter in front of it.

The topic is `order-place`, not `order:place` — Benzene's usual colon convention (`order:create`,
`payment:take`) doesn't survive contact with Kafka, whose topic names may only contain letters,
digits, `.`, `_`, and `-`. Since the Kafka worker below routes on the record's *literal* topic name
against this same `[Message(...)]` value, the topic has to be spelled in a way all three transports
can use unmodified.

## 2. Host it over HTTP

```bash
mkdir ../Api && cd ../Api
dotnet new web -f net10.0
dotnet add package Benzene.AspNet.Core --prerelease
dotnet add reference ../Domain
```

```csharp
// Startup.cs
using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Microsoft.Dependencies;

public class Startup : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.UsingBenzene(x => x
            .AddMessageHandlers(new[] { typeof(PlaceOrderMessageHandler) })
            .AddHttpMessageHandlers());
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
        app.UseHttp(asp => asp.UseMessageHandlers());
    }
}
```

```csharp
// Program.cs
using Benzene.AspNet.Core;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{Environment.GetEnvironmentVariable("PORT") ?? "8080"}");
builder.UseBenzene<Startup>();

var app = builder.Build();
app.UseBenzene();
app.Run();
```

This is exactly [Getting Started: ASP.NET Core](getting-started-aspnet.md) — nothing here is
Kubernetes-specific yet.

## 3. Host it on SQS

A second, completely independent project, sharing nothing with `Api` except a reference to `Domain`:

```bash
mkdir ../SqsWorker && cd ../SqsWorker
dotnet new worker -f net10.0
dotnet add package Benzene.Aws.Sqs --prerelease
dotnet add package Benzene.HostedService --prerelease
dotnet add reference ../Domain
```

```csharp
// Startup.cs
using Amazon.Runtime;
using Amazon.SQS;
using Benzene.Abstractions.Hosting;
using Benzene.Aws.Sqs;
using Benzene.Aws.Sqs.Consumer;
using Benzene.Core.MessageHandlers;
using Benzene.Microsoft.Dependencies;
using Benzene.SelfHost;

public class Startup : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // UseSqs (below) wires the SQS mappers; UseMessageHandlers() discovers PlaceOrderMessageHandler
        // by reflection because this project references Domain - nothing to register here.
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
        var config = new SqsConsumerConfig
        {
            QueueUrl = configuration["QUEUE_URL"]
                ?? throw new InvalidOperationException("QUEUE_URL is not set"),
            MaxNumberOfMessages = 10,
        };

        // SQS_SERVICE_URL is only set for a LocalStack/emulator run (see the runnable example's
        // docker-compose.yml). Unset - the real-AWS/EKS case - AmazonSQSClient() falls through to the
        // SDK's default credential chain, e.g. an IRSA-mapped pod service account: this worker never
        // prescribes how you authenticate, same as every other Benzene AWS client.
        var localEndpoint = configuration["SQS_SERVICE_URL"];
        var sqsClient = string.IsNullOrEmpty(localEndpoint)
            ? new AmazonSQSClient()
            : new AmazonSQSClient(new BasicAWSCredentials("local", "local"),
                new AmazonSQSConfig { ServiceURL = localEndpoint });

        app.UseWorker(worker => worker.UseSqs(
            config,
            new SqsClientFactory(sqsClient),
            sqs => sqs.UseMessageHandlers()));
    }
}
```

```csharp
// Program.cs
using Benzene.HostedService;

IHost host = Host.CreateDefaultBuilder(args)
    .UseBenzene<Startup>()
    .Build();

await host.RunAsync();
```

`SqsConsumer` (`Benzene.Aws.Sqs`) is a long-running poller, not a Lambda trigger — the right shape for
a pod that stays up. It long-polls the queue, runs each message through the same middleware pipeline
`Api` uses, and deletes only the messages whose handler actually succeeded (`SqsConsumerAckMode.PerMessage`,
the default) — a failed or unrouted message is left on the queue for redelivery/DLQ redrive rather than
silently dropped. See [Worker Service Setup](getting-started-worker.md) for the general shape this
follows and every other built-in worker (Kafka, RabbitMQ, Service Bus, Event Hub, Cosmos DB) available
the same way.

## 4. Host it on Kafka

A third project, independent of the other two:

```bash
mkdir ../KafkaWorker && cd ../KafkaWorker
dotnet new worker -f net10.0
dotnet add package Benzene.Kafka.Core --prerelease
dotnet add package Benzene.HostedService --prerelease
dotnet add reference ../Domain
```

```csharp
// Startup.cs
using Benzene.Abstractions.Hosting;
using Benzene.Core.MessageHandlers;
using Benzene.Kafka.Core;
using Benzene.Microsoft.Dependencies;
using Benzene.SelfHost;
using Confluent.Kafka;

public class Startup : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // UseKafka (below) wires the Kafka mappers; UseMessageHandlers() discovers
        // PlaceOrderMessageHandler the same way the SQS worker's does.
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
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

        app.UseWorker(worker =>
            worker.UseKafka<Ignore, string>(kafkaConfig, kafka => kafka.UseMessageHandlers()));
    }
}
```

```csharp
// Program.cs
using Benzene.HostedService;

IHost host = Host.CreateDefaultBuilder(args)
    .UseBenzene<Startup>()
    .Build();

await host.RunAsync();
```

See [Kafka Setup](getting-started-kafka.md) for `BenzeneKafkaConfig`'s other options
(`ConcurrentRequests` bounds how many records this worker processes at once) and why the consumer
routes on the record's literal Kafka topic name rather than a colon-style topic id.

## 5. Containerise all three

Each project gets its own `Dockerfile`; the two workers use the plain runtime image (no ASP.NET, no
inbound listener) rather than `aspnet`:

```dockerfile
# Api/Dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish Api/Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Api.dll"]
```

```dockerfile
# SqsWorker/Dockerfile and KafkaWorker/Dockerfile follow the same shape, swapping the publish path,
# and use the runtime (not aspnet) base image - a worker has nothing listening on a port:
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish SqsWorker/SqsWorker.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "SqsWorker.dll"]
```

```bash
docker build -f Api/Dockerfile         -t orders-api:local         .
docker build -f SqsWorker/Dockerfile   -t orders-sqs-worker:local  .
docker build -f KafkaWorker/Dockerfile -t orders-kafka-worker:local .
kind load docker-image orders-api:local orders-sqs-worker:local orders-kafka-worker:local
```

## 6. Deploy all three

`orders-api` gets a `Deployment` + `Service`, same as any HTTP workload. The two workers get a
`Deployment` each and **no** `Service` — nothing calls a worker pod, it calls out:

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: orders-api
spec:
  replicas: 2
  selector: { matchLabels: { app: orders-api } }
  template:
    metadata: { labels: { app: orders-api } }
    spec:
      containers:
        - name: orders-api
          image: orders-api:local
          imagePullPolicy: IfNotPresent
          ports: [{ containerPort: 8080 }]
          env: [{ name: PORT, value: "8080" }]
          readinessProbe:
            tcpSocket: { port: 8080 }
            initialDelaySeconds: 3
---
apiVersion: v1
kind: Service
metadata:
  name: orders-api
spec:
  selector: { app: orders-api }
  ports: [{ port: 80, targetPort: 8080 }]
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: orders-sqs-worker
spec:
  replicas: 1
  selector: { matchLabels: { app: orders-sqs-worker } }
  template:
    metadata: { labels: { app: orders-sqs-worker } }
    spec:
      containers:
        - name: orders-sqs-worker
          image: orders-sqs-worker:local
          imagePullPolicy: IfNotPresent
          env:
            - { name: QUEUE_URL, value: "https://sqs.eu-west-1.amazonaws.com/<account-id>/orders" }
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: orders-kafka-worker
spec:
  replicas: 1
  selector: { matchLabels: { app: orders-kafka-worker } }
  template:
    metadata: { labels: { app: orders-kafka-worker } }
    spec:
      containers:
        - name: orders-kafka-worker
          image: orders-kafka-worker:local
          imagePullPolicy: IfNotPresent
          env:
            - { name: KAFKA_BOOTSTRAP_SERVERS, value: "kafka-bootstrap.kafka.svc.cluster.local:9092" }
```

```bash
kubectl apply -f k8s.yaml
kubectl get pods   # 4 pods: 2x orders-api, 1x orders-sqs-worker, 1x orders-kafka-worker
```

## 7. Watch the same handler run three ways

```bash
kubectl port-forward service/orders-api 8080:80 &
curl -X POST http://localhost:8080/orders \
  -H "Content-Type: application/json" -d '{"customerId":"cust-1","sku":"espresso","quantity":2}'
```

```json
{"orderId":"order-xxxxxxxxxxx","status":"placed"}
```

```bash
kubectl logs deploy/orders-api | tail -1
# order placed: order-xxxxxxxxxxx - 2x espresso for cust-1
```

Send a message to the SQS queue or the Kafka topic directly (from your own producer, `aws sqs
send-message`, `kafka-console-producer` — see [the runnable example](../examples/K8sTransports) for
exact commands against a local LocalStack/Kafka pair) and the **same log line** appears in
`orders-sqs-worker`'s or `orders-kafka-worker`'s pod, for a request that never touched HTTP. That's
the proof: one handler, three independently deployed, independently scaled entry points.

```bash
kubectl scale deploy/orders-kafka-worker --replicas=3   # only the Kafka leg scales
```

## Why not just ASP.NET Core?

A Kubernetes pod running plain ASP.NET Core already gives you an HTTP service on a `Deployment` — you
don't need Benzene, or this guide, for that alone. The reason to reach for Benzene here is what
happens the moment a second entry point shows up: a queue a partner team publishes to, a Kafka topic
another service already streams onto, a batch job that used to call your REST endpoint but really
just wants to drop a message and move on. Without a message-handler abstraction, each of those becomes
its own bespoke controller/consumer with its own copy (or its own subtly-diverged reimplementation) of
the same validation and business logic.

With Benzene, that second entry point is a new host project and a `UseSqs`/`UseKafka` call — the
handler doesn't change, because it was never written against HTTP in the first place. That's what
sections 2–4 above actually demonstrate: the identical `PlaceOrderMessageHandler`, unmodified between
them, wired onto three transports in about twenty lines of host-specific code each. On Kubernetes
specifically, that means each transport is also its own Deployment: the HTTP leg scales on request
volume, the Kafka leg scales on consumer lag, and a bad deploy to one doesn't touch the others —
`kubectl rollout undo deploy/orders-kafka-worker` while `orders-api` keeps serving traffic
uninterrupted, because they're not the same pod, the same image tag being rolled out, or even the
same restart schedule.

> The `readinessProbe` above is a bare TCP check — wire a real health endpoint with
> [Kubernetes Health Checks](kubernetes-health-checks.md) (`Benzene.HealthChecks`) so the probe
> reflects your dependencies, not just process liveness. Same for the workers: they have no probe at
> all above (nothing polls them over HTTP), but `Benzene.HealthChecks`' `/benzene/health` works
> identically if you host it on a spare port.

## Next steps

- **Health & readiness** — [Kubernetes Health Checks](kubernetes-health-checks.md).
- **Observability** — [Monitoring & Diagnostics](monitoring.md) and distributed tracing in the
  [Cookbooks](cookbooks/README.md).
- **More built-in workers** — [Worker Service Setup](getting-started-worker.md) covers RabbitMQ, Azure
  Service Bus, Event Hubs, and Cosmos DB Change Feed the same way SQS and Kafka are covered above.
- **A full mesh on Kubernetes** — [`examples/K8sMesh`](../examples/K8sMesh) deploys three
  *discovering* services plus the Mesh UI, credential-free on kind and on real EKS — a different
  demonstration (service-to-service discovery and calls) from this guide's (one handler, several
  inbound transports).
