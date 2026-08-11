# One handler, three Kubernetes Deployments

The runnable version of [Getting Started: Benzene on Kubernetes](../../docs/getting-started-kubernetes.md).

A single handler - `Domain/PlaceOrderMessageHandler.cs` - reached three different ways, each its
own pod:

```
                              ┌──────────────────────────────────────┐
        HTTP  ──────────────▶│  orders-api           (Deployment)    │──┐
                              └──────────────────────────────────────┘  │
                              ┌──────────────────────────────────────┐  │   all three dispatch
        SQS queue  ─────────▶│  orders-sqs-worker    (Deployment)    │──┼──▶ PlaceOrderMessageHandler
                              └──────────────────────────────────────┘  │   (Domain/)
                              ┌──────────────────────────────────────┐  │
        Kafka topic  ───────▶│  orders-kafka-worker  (Deployment)    │──┘
                              └──────────────────────────────────────┘
```

Nothing in the handler knows which pod called it. That's the point: the same business logic scales,
deploys, and rolls back independently behind whichever transport actually reaches it, no rewrite at
the boundary - plain ASP.NET Core alone gives you the first Deployment; Benzene gives you all three
from one class.

## Projects

| Path | What it is |
|---|---|
| `Domain/` | the shared handler - `[Message("order-place")] [HttpEndpoint("POST", "/orders")] PlaceOrderMessageHandler`, referenced by all three hosts below |
| `Api/` | ASP.NET Core host - `POST /orders` |
| `SqsWorker/` | `Benzene.HostedService` + `Benzene.Aws.Sqs`'s self-hosted polling consumer (`UseSqs`) |
| `KafkaWorker/` | `Benzene.HostedService` + `Benzene.Kafka.Core`'s self-hosted consumer (`UseKafka`) |
| `k8s/` | three Deployments (`api.yaml` also a Service) + a kustomize base, pointed at a real SQS queue and Kafka cluster via env vars - no bundled infra |
| `compose/` | `docker-compose.yml` - LocalStack (SQS) + a throwaway Kafka broker + all three services, for a credential-free local run |

## Run it locally (no Kubernetes, no cloud account)

```bash
docker compose -f examples/K8sTransports/compose/docker-compose.yml up --build
```

Then, in three more terminals:

```bash
# 1. HTTP
curl -XPOST localhost:8080/orders -H 'content-type: application/json' \
     -d '{"customerId":"cust-1","sku":"espresso","quantity":2}'
# orders-api logs: "order placed: order-xxxxxxxxxxx - 2x espresso for cust-1"

# 2. SQS - send straight to the queue LocalStack created, no HTTP involved. `run --rm --entrypoint aws`
# starts a fresh throwaway container on the sqs-init service's image/network/credentials, overriding
# its create-queue entrypoint so the args below reach the aws-cli directly (that service's own
# container already exited once it finished creating the queue, so `exec` won't find it running).
docker compose -f examples/K8sTransports/compose/docker-compose.yml run --rm --entrypoint aws sqs-init \
  --endpoint-url=http://localstack:4566 sqs send-message \
    --queue-url http://localstack:4566/000000000000/orders \
    --message-body '{"customerId":"cust-2","sku":"latte","quantity":1}' \
    --message-attributes 'topic={StringValue=order-place,DataType=String}'
# orders-sqs-worker logs the same line shape, for a message that never touched HTTP

# 3. Kafka - produce straight to the topic, no HTTP involved
docker compose -f examples/K8sTransports/compose/docker-compose.yml exec kafka \
  bash -c 'echo "{\"customerId\":\"cust-3\",\"sku\":\"filter\",\"quantity\":4}" | \
    kafka-console-producer --bootstrap-server localhost:29092 --topic order-place'
# orders-kafka-worker logs the same line shape again
```

Three different entry points, one unmodified log line proving all three ran the exact same handler
code. `docker compose logs -f orders-api orders-sqs-worker orders-kafka-worker` to watch all three
at once.

## Deploy to Kubernetes

Build and load the three images (against a [kind](https://kind.sigs.k8s.io) cluster, as in the
main guide - swap for your registry's push/pull on a real cluster):

```bash
docker build -f examples/K8sTransports/Api/Dockerfile         -t benzene-k8stransports-api:local         .
docker build -f examples/K8sTransports/SqsWorker/Dockerfile   -t benzene-k8stransports-sqs-worker:local   .
docker build -f examples/K8sTransports/KafkaWorker/Dockerfile -t benzene-k8stransports-kafka-worker:local .
kind load docker-image benzene-k8stransports-api:local benzene-k8stransports-sqs-worker:local benzene-k8stransports-kafka-worker:local
```

Edit the `QUEUE_URL` in `k8s/sqs-worker.yaml` and the `KAFKA_BOOTSTRAP_SERVERS` in
`k8s/kafka-worker.yaml` to point at a real queue and cluster (there is deliberately no bundled
SQS/Kafka in these manifests - see each file's own comment for why and for the IRSA note on the SQS
side), then:

```bash
kubectl apply -k examples/K8sTransports/k8s/
kubectl -n k8s-transports get pods   # 4 pods: 2x orders-api, 1x orders-sqs-worker, 1x orders-kafka-worker
kubectl -n k8s-transports logs -f deploy/orders-sqs-worker
```

Scale the transports independently, because they're independent Deployments:

```bash
kubectl -n k8s-transports scale deploy/orders-kafka-worker --replicas=3
```

## Why this, and not just ASP.NET Core

The [Kubernetes guide](../../docs/getting-started-kubernetes.md#why-not-just-aspnet-core) covers the
reasoning this example exists to prove.
