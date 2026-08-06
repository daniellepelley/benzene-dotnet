# Getting Started: Benzene on Kubernetes

This guide takes you from an empty folder to a Benzene service running on **Kubernetes** — any cluster
(kind, minikube, EKS, GKE, AKS). On Kubernetes, a Benzene service is just a container that speaks HTTP:
you host it with ASP.NET Core (exactly as in the [ASP.NET guide](getting-started-aspnet.md)), put it in an
image, and run it as a Deployment behind a Service. The handler is identical to every other host.

> **Runnable version:** this guide follows [`examples/K8sMesh/Service`](../examples/K8sMesh/Service) — a
> containerised Benzene service with a `Dockerfile` and `k8s/` manifests that deploy to kind and to real
> EKS unchanged.

## What you'll build

A containerised HTTP service handling `POST /orders`, deployed as a `Deployment` + `Service` and reachable
inside (or outside) the cluster.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Docker](https://docs.docker.com/get-docker/).
- A cluster and `kubectl` — [kind](https://kind.sigs.k8s.io/) is the quickest for local work
  (`kind create cluster`).

## 1. Create the service

The service itself is a normal ASP.NET Core Benzene app — follow
[Getting Started: ASP.NET Core](getting-started-aspnet.md) to create `HelloBenzene` with a handler and a
`Program.cs`. In short:

```bash
mkdir orders-service && cd orders-service
dotnet new web -f net10.0
dotnet add package Benzene.AspNet.Core --prerelease
```

`Program.cs` hosts a `Startup` the same way every ASP.NET Benzene service does — bind to the port
Kubernetes provides via the `PORT` env var:

```csharp
using Benzene.AspNet.Core;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{Environment.GetEnvironmentVariable("PORT") ?? "8080"}");
builder.UseBenzene<Startup>();

var app = builder.Build();
app.UseBenzene();
app.Run();
```

(Your `Startup : BenzeneStartUp` and `PlaceOrderMessageHandler` are exactly as in the ASP.NET guide — no
Kubernetes-specific code anywhere in your app.)

## 2. Containerise it

Add a `Dockerfile` — multi-stage so the runtime image stays small, listening on `8080`:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "orders-service.dll"]
```

```bash
docker build -t orders-service:local .
```

On kind, load the local image so the cluster can pull it without a registry:

```bash
kind load docker-image orders-service:local
```

## 3. Deploy it

Add `k8s.yaml` — a `Deployment` that runs the image and a `Service` that fronts it:

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: orders-service
spec:
  replicas: 2
  selector:
    matchLabels: { app: orders-service }
  template:
    metadata:
      labels: { app: orders-service }
    spec:
      containers:
        - name: orders-service
          image: orders-service:local
          imagePullPolicy: IfNotPresent   # kind uses the loaded local image
          ports:
            - containerPort: 8080
          env:
            - name: PORT
              value: "8080"
          readinessProbe:
            httpGet: { path: /health, port: 8080 }
            initialDelaySeconds: 3
---
apiVersion: v1
kind: Service
metadata:
  name: orders-service
spec:
  selector: { app: orders-service }
  ports:
    - port: 80
      targetPort: 8080
```

```bash
kubectl apply -f k8s.yaml
```

## 4. Call it

Port-forward the Service and post an order:

```bash
kubectl port-forward service/orders-service 8080:80 &
curl -X POST http://localhost:8080/orders \
  -H "Content-Type: application/json" -d '{"orderId":"ORD-1","customer":"acme"}'
```

```json
{"orderId":"ORD-1","status":"accepted"}
```

That's a Benzene service on Kubernetes. Scaling is `replicas:` or an HPA; the handler doesn't change.

> The `readinessProbe` above hits `/health` — wire a real health endpoint with
> [Kubernetes Health Checks](kubernetes-health-checks.md) (`Benzene.HealthChecks`) so the probe reflects
> your dependencies, not just process liveness.

## Consuming queues/streams on Kubernetes

A pod doesn't have to be HTTP-only. To consume Kafka, Service Bus, Event Hubs, or SQS from a long-running
pod, host a **worker** instead of (or alongside) the web server — see
[Getting Started: Worker Services](getting-started-worker.md). The same handlers serve both.

## Next steps

- **Health & readiness** — [Kubernetes Health Checks](kubernetes-health-checks.md).
- **Observability** — [Monitoring & Diagnostics](monitoring.md) and distributed tracing in the
  [Cookbooks](cookbooks/README.md).
- **A full mesh on Kubernetes** — [`examples/K8sMesh`](../examples/K8sMesh) deploys three discovering
  services plus the Mesh UI, credential-free on kind and on real EKS.
