# Getting Started: Benzene on Google Cloud Functions

This guide takes you from an empty folder to a Benzene service running on **Google Cloud Functions
(Gen2)**. The handler you write is identical to every other host — only the entry point and deploy command
are Google-specific. Deploying somewhere else? See [AWS Lambda](getting-started-aws.md),
[Azure Functions](azure-functions.md), or the [platform picker](getting-started.md).

> **Runnable version:** this guide follows [`examples/Google`](../examples/Google) — the same `Startup`
> hosted on both Cloud Functions Gen2 (`Function.cs`) and Cloud Run (`Program.cs`).

## What you'll build

An HTTP-triggered function that handles `POST /orders` and returns a JSON response — the same
transport-agnostic handler you'd deploy to any host.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- The [`gcloud` CLI](https://cloud.google.com/sdk/docs/install), authenticated (`gcloud auth login`) with a
  project set (`gcloud config set project <id>`).

## 1. Create the project

```bash
mkdir OrdersFunction && cd OrdersFunction
dotnet new console -f net10.0
```

## 2. Install the Benzene package

Benzene's packages are prerelease (`-alpha`) until 1.0, so `--prerelease` is required:

```bash
dotnet add package Benzene.GoogleCloud.Functions.Http --prerelease
```

`Benzene.GoogleCloud.Functions.Http` brings the Google Cloud Functions Framework host plus Benzene's HTTP
pipeline and message-handler infrastructure transitively.

## 3. Write a message handler

Create `PlaceOrderMessageHandler.cs`. This file is identical to what you'd write for AWS or ASP.NET — it
knows nothing about Google Cloud:

```csharp
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Results;

namespace OrdersFunction;

[Message("order:placed")]
[HttpEndpoint("POST", "/orders")]
public class PlaceOrderMessageHandler : IMessageHandler<OrderPlaced, OrderAccepted>
{
    public Task<IBenzeneResult<OrderAccepted>> HandleAsync(OrderPlaced message)
    {
        var response = new OrderAccepted { OrderId = message.OrderId, Status = "accepted" };
        return Task.FromResult(BenzeneResult.Ok(response));
    }
}

public class OrderPlaced { public string OrderId { get; set; } public string Customer { get; set; } }
public class OrderAccepted { public string OrderId { get; set; } public string Status { get; set; } }
```

## 4. Define the StartUp and the function entry point

`StartUp.cs` is the platform-neutral [`BenzeneStartUp`](hosting.md) — the same shape every host uses:

```csharp
using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Http;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace OrdersFunction;

public class Startup : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        => services.UsingBenzene(x => x
            .AddBenzene()
            .AddMessageHandlers(typeof(PlaceOrderMessageHandler).Assembly)
            .AddHttpMessageHandlers());

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        => app.UseHttp(http => http.UseMessageHandlers());
}
```

`Function.cs` is the only Google-specific line — it hosts that `Startup` on the Functions Framework:

```csharp
using Benzene.GoogleCloud.Functions.Http;

namespace OrdersFunction;

public class Function : GoogleCloudFunctionHost<Startup> { }
```

## 5. Deploy

```bash
gcloud functions deploy orders \
  --gen2 --runtime dotnet10 --region europe-west2 \
  --source . --entry-point OrdersFunction.Function \
  --trigger-http --allow-unauthenticated
```

`--entry-point` points at your `Function` class; the Functions Framework does the rest. When it finishes it
prints the function URL:

```bash
curl -X POST "$(gcloud functions describe orders --gen2 --region europe-west2 --format 'value(serviceConfig.uri)')/orders" \
  -H "Content-Type: application/json" -d '{"orderId":"ORD-1","customer":"acme"}'
```

```json
{"orderId":"ORD-1","status":"accepted"}
```

## Beyond HTTP: Pub/Sub

The same `Startup` and handlers are reachable over **Pub/Sub** as well — a Pub/Sub-triggered function binds
an inbox topic and routes by the Benzene topic carried in the message. The
[Google Cloud Mesh example](../examples/GoogleCloudMesh) wires HTTP and Pub/Sub functions side by side over
one shared domain; see its README for the `gcloud functions deploy --trigger-topic` form.

## Next steps

- **Add validation** — [FluentValidation](fluent-validation.md) or [Data Annotations](data-annotations.md).
- **Test it** — boot the real `Startup` in-memory and push a request through the front door; see
  [Testing Benzene](testing-benzene.md) and the tests in [`examples/Google`](../examples/Google).
- **Run the same handler elsewhere** — [AWS Lambda](getting-started-aws.md),
  [Azure Functions](azure-functions.md), [Kubernetes](getting-started-kubernetes.md).
