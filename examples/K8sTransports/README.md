# One handler, one container, three transports

The runnable version of [Getting Started: Benzene on Kubernetes](../../docs/getting-started-kubernetes.md).

A single handler - `Domain/PlaceOrderMessageHandler.cs` - reached three different ways, from **one**
running container:

```
        HTTP        ─────────┐
        SQS queue   ─────────┼──▶  orders-app (Deployment)  ──▶  PlaceOrderMessageHandler
        Kafka topic ─────────┘         (App/)                        (Domain/)
```

Nothing in the handler knows which transport called it. That's the point: `App/Program.cs` hosts
Kestrel (`POST /orders`), an SQS poller, and a Kafka consumer together, in the same process, all
dispatching to the exact same handler class - plain ASP.NET Core alone gives you the HTTP leg;
Benzene gives you all three from one class, one image, one Deployment.

## Projects

| Path | What it is |
|---|---|
| `Domain/` | the shared handler - `[Message("order-place")] [HttpEndpoint("POST", "/orders")] PlaceOrderMessageHandler` |
| `App/` | the one host - `Program.cs` wires **two** `BenzeneStartUp`s onto **one** `WebApplicationBuilder`: `HttpStartup` (Kestrel, via `Benzene.AspNet.Core`) and `WorkerStartup` (`Benzene.HostedService` + `Benzene.Aws.Sqs`'s `UseSqs` + `Benzene.Kafka.Core`'s `UseKafka`, composed as one background worker) |
| `k8s/` | one Deployment + Service, pointed at a real SQS queue and Kafka cluster via env vars - no bundled infra |
| `compose/` | `docker-compose.yml` - LocalStack (SQS) + a throwaway Kafka broker + the one app image, for a credential-free local run |

See `App/Program.cs`, `App/HttpStartup.cs`, and `App/WorkerStartup.cs` for **why it's two
`BenzeneStartUp`s and not one**: a single `Configure(IBenzeneApplicationBuilder app, ...)` can only
own the platform its `app` was built for - `app.UseHttp(...)` no-ops on a worker builder and
`app.UseWorker(...)` no-ops on an ASP.NET Core builder, by design (see each `BenzeneApplicationBuilder`
subclass). Two `BenzeneStartUp`s sharing the same `builder.Services` is what lets one process own all
three transports without either call becoming a silent no-op.

## Run it locally (no Kubernetes, no cloud account)

```bash
docker compose -f examples/K8sTransports/compose/docker-compose.yml up --build
```

Then, in three more terminals:

```bash
# 1. HTTP
curl -XPOST localhost:8080/orders -H 'content-type: application/json' \
     -d '{"customerId":"cust-1","sku":"espresso","quantity":2}'
# orders-app logs: "order placed: order-xxxxxxxxxxx - 2x espresso for cust-1"

# 2. SQS - send straight to the queue LocalStack created, no HTTP involved. `run --rm --entrypoint aws`
# starts a fresh throwaway container on the sqs-init service's image/network/credentials, overriding
# its create-queue entrypoint so the args below reach the aws-cli directly (that service's own
# container already exited once it finished creating the queue, so `exec` won't find it running).
docker compose -f examples/K8sTransports/compose/docker-compose.yml run --rm --entrypoint aws sqs-init \
  --endpoint-url=http://localstack:4566 sqs send-message \
    --queue-url http://localstack:4566/000000000000/orders \
    --message-body '{"customerId":"cust-2","sku":"latte","quantity":1}' \
    --message-attributes 'topic={StringValue=order-place,DataType=String}'
# orders-app logs the same line shape, for a message that never touched HTTP

# 3. Kafka - produce straight to the topic, no HTTP involved
docker compose -f examples/K8sTransports/compose/docker-compose.yml exec kafka \
  bash -c 'echo "{\"customerId\":\"cust-3\",\"sku\":\"filter\",\"quantity\":4}" | \
    kafka-console-producer --bootstrap-server localhost:29092 --topic order-place'
# orders-app logs the same line shape again
```

Three different entry points, one unmodified log line, one container's logs - `docker compose logs -f
orders-app` - proving all three ran through the exact same handler code.

## Deploy to Kubernetes

Build and load the one image (against a [kind](https://kind.sigs.k8s.io) cluster, as in the main
guide - swap for your registry's push/pull on a real cluster):

```bash
docker build -f examples/K8sTransports/App/Dockerfile -t benzene-k8stransports-app:local .
kind load docker-image benzene-k8stransports-app:local
```

Edit `QUEUE_URL` and `KAFKA_BOOTSTRAP_SERVERS` in `k8s/app.yaml` to point at a real queue and cluster
(there is deliberately no bundled SQS/Kafka in this manifest - see the file's own comment for why and
for the IRSA note on the SQS side), then:

```bash
kubectl apply -k examples/K8sTransports/k8s/
kubectl -n k8s-transports get pods   # 2 pods: 2x orders-app
kubectl -n k8s-transports logs -f deploy/orders-app
```

There's only one Deployment to scale - scaling it scales all three transports' consuming capacity
together:

```bash
kubectl -n k8s-transports scale deploy/orders-app --replicas=4
```

## Why this, and not just ASP.NET Core

The [Kubernetes guide](../../docs/getting-started-kubernetes.md#why-not-just-aspnet-core) covers the
reasoning this example exists to prove.

## The alternative: one Deployment per transport

Combining all three transports into one container is not the only valid shape - splitting them into
**separate** Deployments (one for HTTP, one for the SQS poller, one for the Kafka consumer, each its
own `BenzeneStartUp`/`Program.cs`/image) is a legitimate pattern too, and sometimes the better one:
each transport then scales, rolls back, and fails independently of the others - a Kafka outage or a
bad Kafka-worker deploy no longer risks the HTTP leg's availability, the way it does when they share a
process and a crash-loop. The tradeoff is real: more images to build, more Deployments to manage, and
(per the two-`BenzeneStartUp`-sharing-one-image structure above) some duplicated `Program.cs`
boilerplate per transport. Reach for that shape instead when the transports' traffic, failure modes,
or scaling needs genuinely diverge - the handler in `Domain/` doesn't change either way, only how many
`BenzeneStartUp`s and Dockerfiles wrap it.
