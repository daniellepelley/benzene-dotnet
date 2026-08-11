# Kafka Setup

Benzene ships four separate Kafka integrations — a platform-neutral self-hosted consumer/producer
built on `Confluent.Kafka`, a windowed/checkpointed **stream** binding over that same consumer, an
AWS Lambda MSK/self-managed Kafka event source trigger, and an Azure Functions Kafka trigger. They share the same `[Message]`/message-handler programming model,
but are three independent implementations, not one library reused three times — the AWS and Azure
packages do **not** depend on `Benzene.Kafka.Core`, and each maps a Kafka record onto the pipeline
in its own way. Pick the section below that matches how you're hosting the service.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A Kafka broker to develop against. The examples below use the
  [`examples/Kafka/docker-compose.yaml`](../examples/Kafka/docker-compose.yaml) file, which brings
  up a single-broker Confluent Kafka cluster (`localhost:9092`) plus
  [Kafdrop](https://github.com/obsidiandynamics/kafdrop) at `http://localhost:19000` for inspecting
  topics
- For the AWS or Azure sections: the same cloud prerequisites as
  [AWS Lambda Setup](getting-started-aws.md) / [Azure Functions Setup](azure-functions.md)

## Which package do I need?

| Scenario | Package | Underlying client |
|---|---|---|
| Long-running worker/console app consuming Kafka directly, one message handler per record | `Benzene.Kafka.Core` | `Confluent.Kafka` |
| The same, but windowed/aggregated stream processing over batches of records | `Benzene.Kafka.Streaming` | `Confluent.Kafka` (via `Benzene.Kafka.Core`) |
| AWS Lambda triggered by MSK or self-managed Kafka | `Benzene.Aws.Lambda.Kafka` | `Amazon.Lambda.KafkaEvents` |
| Azure Function triggered by Kafka (incl. Event Hubs' Kafka-compatible endpoint) | `Benzene.Azure.Function.Kafka` | `Microsoft.Azure.Functions.Worker.Extensions.Kafka` |

---

## 1. Self-hosted Kafka worker (`Benzene.Kafka.Core`)

This is the option for a long-running process that owns its own consumer group and polls Kafka
continuously — a Worker Service, a container, or a plain console app. Under the hood,
`BenzeneKafkaWorker<TKey, TValue>` runs a `while` loop calling `IConsumer.Consume`, dispatching each
record through the middleware pipeline with up to `ConcurrentRequests` records in flight at once
(bounded by a `BoundedConcurrentDispatcher`, a `System.Threading.Channels`-based lane dispatcher).

### 1.1 Create the project

```bash
mkdir MyKafkaWorker && cd MyKafkaWorker
dotnet new worker -f net10.0
```

### 1.2 Install the NuGet packages

```bash
dotnet add package Benzene.Kafka.Core --prerelease
dotnet add package Benzene.HostedService --prerelease
dotnet add package Benzene.SelfHost --prerelease
```

`Benzene.Kafka.Core` brings in `Benzene.HostedService` and `Benzene.Core` transitively (see its
`.csproj`), but add them explicitly since you'll reference their types directly below.
`Confluent.Kafka` comes in transitively from `Benzene.Kafka.Core`.

### 1.3 Define a message handler

The Kafka consumer routes each record to a handler by matching the record's **literal Kafka topic
name** against the handler's `[Message("...")]` value — there's no colon-separated topic-id
convention here the way there is for HTTP/SQS/SNS; whatever you pass in `[Message(...)]` must be
exactly the Kafka topic string:

<!-- compile: quickstart sdk=worker -->
```csharp
using Benzene.Abstractions.MessageHandlers;
using Benzene.Core.MessageHandlers;

[Message("hello_world")]
public class HelloWorldMessageHandler : IMessageHandler<HelloWorldMessage>
{
    public Task HandleAsync(HelloWorldMessage message)
    {
        Console.WriteLine($"Hello {message.Name}!");
        return Task.CompletedTask;
    }
}

public class HelloWorldMessage
{
    public string Name { get; set; }
}
```

This is an `IMessageHandler<TRequest>` (no response type) — the right shape for a fire-and-forget
Kafka record, since nothing is written back to the broker. See
[Message Handlers](message-handlers.md) for the request/response shape if you need one for another
transport on the same handler.

### 1.4 Define your StartUp

Kafka consumption uses the same platform-neutral `BenzeneStartUp` as AWS/Azure/ASP.NET Core. The
`Benzene.Kafka.Core.Extensions.UseKafka<TKey, TValue>` extension targets `IBenzeneWorkerStartup` —
the worker-specific builder that `UseWorker(...)` hands you — so you wire Kafka up inside
`app.UseWorker(worker => worker.UseKafka(...))`:

<!-- compile: quickstart sdk=worker -->
```csharp
using Benzene.Abstractions.Hosting;
using Benzene.Core.MessageHandlers;
using Benzene.Kafka.Core;
using Benzene.Microsoft.Dependencies;
using Benzene.SelfHost;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public class StartUp : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
    {
        return new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();
    }

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Nothing Benzene-specific to register - register your own services here. UseKafka and
        // UseMessageHandlers in Configure wire up the Kafka mappers and discover your handlers.
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
        var kafkaConfig = new BenzeneKafkaConfig
        {
            ConsumerConfig = new ConsumerConfig
            {
                BootstrapServers = "localhost:9092",
                SecurityProtocol = SecurityProtocol.Plaintext,
                GroupId = "my-kafka-worker",
                AutoOffsetReset = AutoOffsetReset.Earliest
            },
            Topics = new[] { "hello_world" },
            ConcurrentRequests = 5
        };

        app.UseWorker(worker =>
            worker.UseKafka<Ignore, string>(kafkaConfig, kafka => kafka.UseMessageHandlers()));
    }
}
```

- `TKey`/`TValue` are the Confluent.Kafka deserialized record types — `Ignore, string` (as in
  `examples/Kafka`) is the common case where you don't care about the message key and the value is
  a JSON string; use `Confluent.Kafka`'s other built-in deserializers (or your own) for other
  shapes.
- `UseKafka<TKey, TValue>(...)` registers the topic/body/headers getters that adapt a
  `ConsumeResult<TKey, TValue>` to the message-handler pipeline (`AddKafka<TKey, TValue>()` is called
  for you), so no Benzene registration is needed in `ConfigureServices` — `UseMessageHandlers()`
  discovers your handlers and pulls in the core pipeline. Kafka record headers **are** mapped to
  Benzene message headers on this path (UTF-8 decoded) — unlike the Azure Functions Kafka trigger below.
- `BenzeneKafkaConfig.ConcurrentRequests` (default `5`) bounds how many records this worker
  processes concurrently via an internal `BoundedConcurrentDispatcher` (Channels-based).

### 1.5 Wire up `Program.cs`

`Benzene.HostedService`'s `UseBenzene<StartUp>()` registers the Kafka worker as an `IHostedService`,
so it starts and stops with the host:

<!-- compile: quickstart sdk=worker -->
```csharp
using Benzene.HostedService;

IHost host = Host.CreateDefaultBuilder(args)
    .UseBenzene<StartUp>()
    .Build();

await host.RunAsync();
```

Run it locally against the compose stack:

```bash
docker compose -f docker-compose.yaml up -d   # from examples/Kafka
dotnet run
```

### 1.6 Producing messages

To send Kafka messages from another Benzene service (rather than a plain `Confluent.Kafka`
producer), build a `KafkaBenzeneMessageClient` from a middleware pipeline over
`KafkaSendMessageContext`:

```csharp
using Benzene.Core.Middleware;
using Benzene.Kafka.Core.Kafka;
using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;

var producerConfig = new ProducerConfig { BootstrapServers = "localhost:9092" };
var producer = new ProducerBuilder<string, string>(producerConfig).Build();

var pipeline = new MiddlewarePipelineBuilder<KafkaSendMessageContext>(benzeneServiceContainer)
    .UseKafkaClient(producer)
    .Build();

var client = new KafkaBenzeneMessageClient(pipeline, NullLogger<KafkaBenzeneMessageClient>.Instance, serviceResolver);

await client.SendMessageAsync<object, Benzene.Abstractions.Results.Void>("hello_world", new { Name = "World" });
```

`KafkaContextConverter` JSON-serializes the payload as the Kafka message value and forwards
`IBenzeneClientRequest.Headers` onto the outbound `Message.Headers` (UTF-8 encoded) — the same
mechanism correlation-ID/trace-context decorators rely on to reach the wire. A plain
`IProducer<string, string>.ProduceAsync(...)` call works exactly as well if you don't need that;
see `examples/Kafka/Benzene.Examples.Kafka.Producer` for a minimal producer console app built this
way.

### 1.7 Testing

There's no `BenzeneTestHost` support for Kafka — `Benzene.Testing` has no `Send*Async` extension
for it (unlike SQS/SNS/API Gateway on AWS). The pattern used in
`examples/Kafka/Benzene.Examples.Kafka.Test` instead runs the worker for real against a live broker
and polls for the effect:

1. `KafkaSetUp` deletes/recreates the test's Kafka topics against the compose broker and builds a
   real `Confluent.Kafka` producer.
2. `WorkerSetUp` starts a real `StartUp` instance (`await _worker.StartAsync(...)`) on a background
   thread.
3. The test publishes a message with `KafkaSetUp.SendAsync(topic, message)`, then polls
   (`ResultPoller.Poll(delayMs, times, () => <condition>, "failure message")`) until the handler's
   observable side effect (e.g. a row in an in-memory store) appears, since consumption is
   asynchronous.
4. `WorkerSetUp.TearDownAsync()`/`KafkaSetUp.TearDownAsync()` stop the worker and delete the topics
   afterward.

This is a real integration test against Docker Kafka, not an in-memory fake — budget for the
broker's startup time in CI.

---

## 1b. Windowed stream processing (`Benzene.Kafka.Streaming`)

Section 1 fans **out**: one context, one DI scope and one message-handler route per record. That's
the right shape for command/event handling and the wrong one for stream processing — once a batch
has been fanned out into isolated per-record contexts you can no longer window it, aggregate it, or
re-order it by key.

`Benzene.Kafka.Streaming` fans **in**. `worker.UseKafkaStream<TKey, TValue>(...)` accumulates
consumed records into a batch — flushed when either `MaxBatchSize` records have arrived or
`MaxBatchWait` has elapsed since the batch's *first* record, whichever comes first — and hands the
whole batch to the pipeline as one `StreamContext<ConsumeResult<TKey, TValue>>`, consumed with
`UseStream(...)` plus the stream operators (`Window(n)`, `PartitionBy(...)`). This is the Kafka
counterpart of [`UseKinesisStream`](getting-started-aws.md) and
[`UseCosmosDbChangeFeed`](getting-started-worker.md#azure-cosmos-db-change-feed-benzeneazurecosmosdb);
handlers port between the three unchanged.

It builds on section 1's package rather than replacing it — same `BenzeneKafkaConfig`, same
`IKafkaConsumerFactory` seam, same health check — and `BenzeneKafkaWorker` is unaffected.

```bash
dotnet add package Benzene.Kafka.Streaming --prerelease
```

```csharp
using Benzene.Core.Middleware;
using Benzene.Kafka.Core;
using Benzene.Kafka.Streaming;
using Confluent.Kafka;

public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
{
    app.UseWorker(worker => worker.UseKafkaStream<Ignore, string>(
        new BenzeneKafkaConfig
        {
            ConsumerConfig = new ConsumerConfig
            {
                BootstrapServers = configuration["Kafka:BootstrapServers"],
                GroupId = "market-data-aggregator",
                AutoOffsetReset = AutoOffsetReset.Earliest,
            },
            Topics = new[] { "market-data" },
        },
        stream => stream.UseStream<ConsumeResult<Ignore, string>>(async context =>
        {
            // Records within a partition are in offset order; partitions are independent.
            await foreach (var partition in context.Items.PartitionBy(r => r.TopicPartition, context.CancellationToken))
            {
                foreach (var window in partition.Value.Chunk(50))
                {
                    await AggregateAsync(window);

                    // "Everything up to here, on THIS partition, is safe."
                    await context.Checkpointer.CheckpointAsync(window[^1]);
                }
            }
        }),
        new KafkaStreamOptions
        {
            // Defaults shown except the window sizes.
            MaxBatchSize = 1000,
            MaxBatchWait = TimeSpan.FromMilliseconds(250),
            AutoCheckpointOnSuccess = true,
            CatchHandlerExceptions = false,
        }));
}
```

There is no `UseMessageHandlers()` routing here — a stream is handed to the handler intact, exactly
as with Kinesis and the Cosmos change feed.

**Checkpointing is per topic-partition.** Kafka's commit unit is `(topic, partition) → offset`, and a
batch can span partitions, so `CheckpointAsync(record)` advances only *that record's own* partition
and says nothing about any other. Each partition's watermark only ever moves forward — a checkpoint
that would rewind it is ignored. Like every Kafka commit it has no gap tracking, so checkpoint a
partition's *frontier* (the highest offset with every earlier offset on that partition complete),
which processing each partition in offset order gives you for free. With `AutoCheckpointOnSuccess`
(the default) a handler that never checkpoints still advances every partition to its last record when
the pipeline completes cleanly.

**On failure**, `CatchHandlerExceptions` picks between retry and skip:

| Value | Behavior |
|---|---|
| `false` (default) | Commit whatever the handler checkpointed, then rewind each partition to *its own* first unprocessed record and retry the batch after `FailedBatchRetryDelay`. Nothing is lost; a reliably-failing batch retries forever. |
| `true` | Log, checkpoint the batch anyway, and move on — the poison window is permanently skipped. |

The per-partition rewind is something Kinesis structurally can't do (its resume point is a single
sequence number for the whole batch). The batch's distinct partitions are available on
`StreamContext.Metadata` under `KafkaStreamApplication<TKey, TValue>.TopicPartitionsMetadataKey`.

For the full set of design tradeoffs — why the batch deadline is anchored to the first record rather
than being a rolling idle timer, why offsets are always managed manually, and what happens during a
rebalance — see `src/Benzene.Kafka.Streaming/CLAUDE.md` and
`work/kafka-stream-binding-design.md`.

---

## 2. AWS Lambda (MSK / self-managed Kafka)

`Benzene.Aws.Lambda.Kafka` handles the Lambda event source mapping for MSK or self-managed Kafka
clusters. It's a thin adapter over `Amazon.Lambda.KafkaEvents`'s `KafkaEvent` and does **not** use
`Confluent.Kafka` or `Benzene.Kafka.Core` at all — records already arrive deserialized as part of
the Lambda invocation payload.

Add it to an existing [AWS Lambda](getting-started-aws.md) `StartUp`:

```bash
dotnet add package Benzene.Aws.Lambda.Kafka --prerelease
```

```csharp
public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    => app.UseAwsLambda(eventPipeline => eventPipeline
        .UseKafka(kafkaApp => kafkaApp
            .UseMessageHandlers(router => router.UseFluentValidation())));
```

As with the worker path, the handler's `[Message("...")]` value must match the Kafka topic name
literally (`KafkaMessageTopicGetter` reads it straight off the Lambda event record). Kafka record
headers are mapped to Benzene headers, and partition/offset are available on `KafkaContext`. Kafka
events are fire-and-forget — no response is written back, and a Lambda invocation carrying multiple
records processes all of them (flattened across topic-partitions) through the pipeline before
returning.

See [AWS Lambda Setup](getting-started-aws.md#kafka) for how this fits alongside other AWS event
sources in the same function, and
[AWS IAM Permissions Reference](aws-iam-permissions.md#kafka-trigger-benzeneawslambdakafka) for the
MSK-specific execution-role permissions — these are more involved than other AWS event sources
because MSK event source mappings require VPC connectivity.

## 3. Azure Functions Kafka trigger

`Benzene.Azure.Function.Kafka` wraps the isolated-worker `KafkaTrigger` binding from
`Microsoft.Azure.Functions.Worker.Extensions.Kafka`. It works against any Kafka-compatible
endpoint the trigger binding supports, including Azure Event Hubs' Kafka-compatible endpoint —
despite its package description mentioning Event Hubs specifically, the source has no Event
Hubs-specific code; it's a generic `KafkaRecord`/`KafkaRecord[]` adapter.

Add it to an existing [Azure Functions](azure-functions.md) `StartUp`:

```bash
dotnet add package Microsoft.Azure.Functions.Worker.Extensions.Kafka
dotnet add package Benzene.Azure.Function.Kafka --prerelease
```

```csharp
public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    => app.UseKafka(kafka => kafka.UseMessageHandlers());
```

Then add a Kafka trigger function that injects `IAzureFunctionApp` and dispatches to it — unlike
HTTP, there's no single catch-all trigger Benzene can generate for you, since the trigger binding
needs its own attribute-declared topic/broker list per function. `Benzene.Azure.Function.Kafka`'s
own `KafkaApplication`/`HandleKafkaEvents` take a `KafkaRecord[]` (the type from
`Microsoft.Azure.Functions.Worker.Extensions.Kafka` itself — verified from this repo's source); the
exact shape of your `[KafkaTrigger]`-bound parameter depends on which overload of that Microsoft
binding you use, so check its docs/samples for the trigger attribute signature that gives you
`KafkaRecord[]` (or something you can map to it) directly:

```csharp
using Benzene.Azure.Function.Core;
using Benzene.Azure.Function.Kafka;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Kafka;

public class KafkaFunction
{
    private readonly IAzureFunctionApp _app;

    public KafkaFunction(IAzureFunctionApp app)
    {
        _app = app;
    }

    [Function("kafka")]
    public async Task Run([KafkaTrigger("BrokerList", "hello_world")] KafkaRecord[] events)
    {
        await _app.HandleKafkaEvents(events);
    }
}
```

As with the worker and AWS paths, `[Message("...")]` must match the Kafka topic name literally
(`KafkaMessageTopicGetter.GetTopic` reads `KafkaEvent.Topic` directly). Kafka record headers **are**
mapped to Benzene headers on this path too (`KafkaMessageHeadersGetter` reads `KafkaRecord.Headers`,
UTF-8 decoding each value) — header-based middleware (correlation IDs, W3C trace context via
`.UseW3CTraceContext<KafkaContext>()`) works the same as on the worker and AWS paths above.

See [Azure Functions Setup](azure-functions.md#kafka) for how this fits alongside HTTP and Event Hubs
triggers in the same Function App.

## Troubleshooting

- **Handler never fires** — the `[Message("...")]` value must equal the literal Kafka topic name on
  all three paths (not a colon-separated topic id). Double-check `BenzeneKafkaConfig.Topics` (worker
  path) actually includes that topic, and that the broker has it (`docker compose ... kafdrop` at
  `http://localhost:19000` lets you inspect topics/messages directly).
- **`GroupId` collisions in tests** — the worker example generates a random `GroupId` per test run
  (`Guid.NewGuid().ToString()`) to avoid two test runs fighting over the same consumer group's
  offsets; reuse a stable `GroupId` only once you want durable, resumable consumption.
- **MSK connectivity failures on AWS** — almost always a VPC/security-group issue, not a Benzene
  issue; see [AWS IAM Permissions Reference](aws-iam-permissions.md#kafka-trigger-benzeneawslambdakafka).

## See Also

- [Unified Hosting Model](hosting.md)
- [Testing Benzene](testing-benzene.md)
- [AWS Lambda Setup](getting-started-aws.md)
- [Azure Functions Setup](azure-functions.md)
- [Message Handlers](message-handlers.md)
- [AWS IAM Permissions Reference](aws-iam-permissions.md)
