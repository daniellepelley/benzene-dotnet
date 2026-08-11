# Benzene.Kafka.Streaming

## What this package does
Windowed, partitioned and **checkpointed** Kafka stream processing for Benzene: a self-hosted worker
(`BenzeneKafkaStreamWorker<TKey,TValue>`) that consumes records, accumulates them into batches, and
runs each batch through a Benzene **streaming** pipeline as one
`StreamContext<ConsumeResult<TKey,TValue>>` with real per-partition offset checkpointing. One of the
"self-hosted worker" startup modes documented in `docs/hosting.md`, alongside `BenzeneKafkaWorker`,
`BenzeneEventHubWorker` and `BenzeneCosmosChangeFeedWorker`.

This closes the gap the cross-language spec's `docs/patterns/streaming-processing.md` named: Kinesis,
Event Hubs and the Cosmos change feed all had windowed/checkpointed stream bindings, and Kafka only
had a plain per-record consumer.

## What this adds over `Benzene.Kafka.Core`'s `UseKafka`
`UseKafka` fans **out**: one context, one DI scope and one message-handler route *per record*,
dispatched concurrently through `BoundedConcurrentDispatcher`. That is the right shape for
command/event handling and the wrong one for stream processing — once a batch has been fanned out
into isolated per-record contexts you can no longer window it, aggregate it, or re-order it by key.

`UseKafkaStream` fans **in**: a window of records becomes one `StreamContext`, one pipeline run, one
DI scope, consumed with `UseStream(...)` plus the stream operators (`Window(n)`,
`PartitionBy(r => r.TopicPartition)`). Handlers port unchanged between this and
`UseKinesisStream`/`UseCosmosDbChangeFeed`.

`Benzene.Kafka.Core` is **reused, not forked**: `BenzeneKafkaConfig`, `IKafkaConsumerFactory`/
`KafkaConsumerFactory`, `AddKafka<TKey,TValue>()` and `AddKafkaDependencyHealthCheck(config)` all come
from there. `BenzeneKafkaWorker` is untouched.

```csharp
worker.UseKafkaStream<Ignore, string>(
    new BenzeneKafkaConfig
    {
        ConsumerConfig = new ConsumerConfig { BootstrapServers = "localhost:9092", GroupId = "aggregator" },
        Topics = new[] { "market-data" },
    },
    stream => stream.UseStream<ConsumeResult<Ignore, string>>(async context =>
    {
        await foreach (var partition in context.Items.PartitionBy(r => r.TopicPartition, context.CancellationToken))
        {
            foreach (var window in partition.Value.Chunk(50))
            {
                await Aggregate(window);
                await context.Checkpointer.CheckpointAsync(window[^1]);
            }
        }
    }),
    new KafkaStreamOptions { MaxBatchSize = 1000, MaxBatchWait = TimeSpan.FromMilliseconds(250) });
```

## Key types
- `BenzeneKafkaStreamWorker<TKey,TValue> : IBenzeneWorker` — the batching consume loop. Same lifecycle
  contract as `BenzeneKafkaWorker`/`BenzeneCosmosChangeFeedWorker`: `StartAsync` starts a background
  loop and returns immediately, `StopAsync` signals shutdown and waits (bounded by
  `BenzeneKafkaConfig.DrainTimeout`) for the in-flight batch and the consumer close. Unlike Kinesis
  (AWS hands Lambda a batch) and Cosmos (the SDK's Change Feed Processor delivers one), **nothing
  hands Kafka a batch** — the loop assembles one itself out of `Consume(timeout)` calls.
- `KafkaStreamApplication<TKey,TValue> : StreamMiddlewareApplication<KafkaStreamBatch<…>, ConsumeResult<…>, bool>`
  — maps a batch to one `StreamContext`, tags the transport `"kafka"`, and returns whether the handler
  checkpointed so the worker can decide auto-checkpoint. **Does not catch pipeline exceptions**
  (matching `CosmosChangeFeedApplication`, unlike `KinesisStreamApplication`): only the worker can seek
  the consumer, so only the worker can own the retry-vs-skip decision. Publishes the batch's distinct
  `TopicPartition`s on `StreamContext.Metadata` under `TopicPartitionsMetadataKey` = `"kafka.topicPartitions"`
  (the Kafka counterpart of Cosmos's lease token).
- `KafkaStreamBatch<TKey,TValue>` — the raw "event": records, checkpointer, cancellation token.
- `KafkaStreamCheckpointer<TKey,TValue> : IStreamCheckpointer<ConsumeResult<TKey,TValue>>` — the offset
  watermarks. **Public**, unlike Kinesis's and Cosmos's internal checkpointers: it carries the trickiest
  logic in the package (see below) and this repo has no `InternalsVisibleTo`, so making it public buys
  direct unit-testability and lets a handler that resolves it do its own bookkeeping.
- `KafkaStreamOptions` — `MaxBatchSize` (500), `MaxBatchWait` (1s), `PollTimeout` (250ms),
  `AutoCheckpointOnSuccess` (`true`), `CatchHandlerExceptions` (`false`), `FailedBatchRetryDelay` (1s).
  `Validate()` throws at `StartAsync` for out-of-range values, so a degenerate window fails at boot.
- `UseKafkaStream<TKey,TValue>(config, action, options?, consumerFactory?, healthCheck = true)` — the
  worker wiring, matching `UseKafka`'s signature shape. Registers `AddKafka<TKey,TValue>()` and (by
  default) the same Kafka dependency health check `UseKafka` registers. **No `AddBenzeneMessage()` and
  no `UseMessageHandlers()` routing** — a stream carries no message envelope, exactly like
  `UseCosmosDbChangeFeed`.

## Design tradeoffs

### Windowing: two triggers, and the wait is a per-batch deadline, not a rolling idle timer
A batch flushes on whichever comes first: `MaxBatchSize` records, or `MaxBatchWait` elapsed **since
the batch's first record**. The clock starts when the first record lands in an empty batch and is
*not* extended by later arrivals.

The rejected alternative — "flush once no new record has arrived for `MaxBatchWait`" — bounds nothing
under load: a topic delivering a record every few milliseconds never goes idle, so the batch would
only ever flush on size and the oldest record's latency would be unbounded. A first-record deadline
gives the property a latency-sensitive aggregator actually wants ("no record waits longer than
`MaxBatchWait`") and degrades gracefully: flush on size when busy, on age when quiet.

`PollTimeout` exists because Confluent.Kafka's timeout-based `Consume` takes no cancellation token —
it caps how long the loop can sit inside `Consume` without noticing shutdown. The final poll of a
batch is shortened to land exactly on the deadline rather than overshooting it.

### Checkpointing: one monotonic watermark **per topic-partition**
This is the design fork the package turns on. Kinesis's checkpointer keeps a *single* watermark
because a Lambda Kinesis batch comes from one shard and AWS's retry contract is one resume sequence
number. A Kafka batch can span several topic-partitions, and Kafka's commit unit is
`(topic, partition) → offset`.

So `CheckpointAsync(record)` here means **"everything up to and including this record *on this
record's own partition* is processed"**, and says nothing about any other partition. Kinesis's single
shard-order frontier collapses into one independent frontier per partition. This is both the safe
reading (checkpointing a later-in-batch record can never mark an untouched record on a *different*
partition as done — the mistake a naive batch-order watermark would make) and the Kafka-native one
(it maps directly onto the offset that gets committed).

What it inherits from Kafka, and shares with Kinesis: a committed offset is a watermark with **no gap
tracking**, so committing offset 10 on a partition marks 0–10 done even if 7 failed. A handler must
therefore checkpoint a partition's *frontier* — the highest offset with every earlier offset on that
partition complete — not merely the last record it happened to touch. Processing each partition's
records in offset order (what `PartitionBy(r => r.TopicPartition)` gives you) makes that automatic.
Backwards checkpoints are ignored rather than honored, so an out-of-order or projected-copy checkpoint
can't rewind the resume point; forward gaps remain the handler's responsibility, exactly as on Kinesis.

Mechanically: `CheckpointAsync` calls `StoreOffset(offset + 1)` (Kafka commits the offset to resume
*from*), which keeps librdkafka's own auto-commit and rebalance-time commit in step with real
progress; the end of every batch then issues an explicit `Commit(offsets)` of the exact watermark set,
so a batch boundary is a durable acknowledgement rather than something waiting on a 5-second timer.
`EnableAutoOffsetStore` is forced to `false` at `StartAsync` — unconditionally, which is why there is
no `CommitOnlyOnSuccess` equivalent here: that behavior *is* the binding.

Note `ConsumeResult.TopicPartitionOffset` is a *computed* property in Confluent.Kafka and is never
null, so the "is this a real consumed record or a projected copy?" guard tests `Topic` — a null topic
would otherwise throw out of `TopicPartition.GetHashCode()` on first use as a dictionary key.

### Failure handling: retry by seeking per partition (default), or skip
When the pipeline throws mid-batch:

- **Retry (default, `CatchHandlerExceptions = false`)** — commit whatever the handler checkpointed,
  then `Seek` each partition in the batch back to *its own* first unprocessed record (one past its
  watermark, or its first offset in the batch if it has none) and retry after `FailedBatchRetryDelay`.
  Nothing is lost, partial progress is kept, and a reliably-failing batch retries forever — the same
  honest at-least-once default `BenzeneCosmosChangeFeedConfig.CatchHandlerExceptions` picks, for the
  same reason (Kafka, like the change feed, can genuinely redeliver).
- **Skip (`CatchHandlerExceptions = true`)** — log, checkpoint the batch to the end anyway, move on.
  The poison window is permanently passed over and the partitions keep moving. Cosmos's skip mode.

The per-partition `Seek` is the piece Kinesis structurally cannot do: its resume point is one sequence
number for the whole batch, so a multi-shard batch can't resume mid-shard-set. A Kafka batch can, and
this is why the failure policy is "resume each partition independently" rather than Kinesis's "replay
from the batch's single frontier".

Deliberate non-retry paths, both of which refuse to acknowledge work that wasn't done:
- **Shutdown with an unflushed batch** — the partial batch is abandoned, not flushed. Nothing was
  checkpointed, so it is redelivered on restart. (Flushing it would risk a hung shutdown for records
  that cost nothing to re-read.)
- **Shutdown mid-flush** — commit only what the handler explicitly checkpointed; never auto-checkpoint
  a partially-processed batch, which would silently lose its tail. Same call
  `BenzeneCosmosChangeFeedWorker` makes.
- **A handler that returns successfully having checkpointed only part of the batch** keeps exactly
  that: the uncheckpointed tail is not committed and is redelivered after a restart or rebalance. A
  successful return is not, by itself, a reason to rewind and re-run. Cosmos's manual-checkpoint
  contract.

### Rebalances
The worker registers a `SetPartitionsRevokedHandler` (via `IKafkaConsumerFactory`'s
`Create(config, configureBuilder)` overload) that `Commit()`s already-stored offsets before releasing
partitions, so the next owner resumes from what this worker actually processed. Committing there is
safe *precisely because* auto-offset-store is off — the stored offsets are the checkpointer's
watermarks, never the consumer's raw read position.

The in-progress batch is deliberately **not** flushed during a revoke: that would run a whole pipeline
inside the rebalance callback and risk blowing `max.poll.interval.ms`. Its records are simply
redelivered to the partition's next owner — at-least-once, at the cost of repeating that window's
work. `StoreOffset`/`Commit`/`Seek` against a revoked partition are each caught and logged rather than
propagated, since a mid-batch revoke makes all three legitimately fail. Prefer
`PartitionAssignmentStrategy.CooperativeSticky` (a `ConsumerConfig` passthrough) to reduce
stop-the-world rebalances.

## Which `BenzeneKafkaConfig` members apply
Honored: `ConsumerConfig`, `Topics`, `DrainTimeout`, `ConsumeExceptionRetryDelay`.

Not used: `ConcurrentRequests`, `PreserveOrderPerPartition`, `CatchHandlerExceptions`,
`CommitOnlyOnSuccess`, `DrainOnRevoke`. Those describe the per-record fan-out model — this worker
processes one batch at a time, in order, and always manages offsets manually. `KafkaStreamOptions`
carries the streaming equivalents. `CatchHandlerExceptions` is deliberately *not* shared: the two
workers make different offset promises (the per-record worker has no watermark to seek back to, so it
defaults to catching; this one does, so it defaults to retrying) and must not share a knob.

## When to use this package
- Windowed aggregation over a Kafka topic (rolling metrics, VWAP/OHLC bars, sessionization).
- Per-key/per-partition ordered processing where the handler needs the whole window at once.
- High-throughput ingestion/ETL where one write per window beats one write per record.
- Anything that needs "everything up to here is safe" checkpoint control rather than per-record commits.

Use `Benzene.Kafka.Core`'s `UseKafka` instead when you want per-record message-handler routing.

## Dependencies on other Benzene packages
- **Benzene.Kafka.Core** — `BenzeneKafkaConfig`, `IKafkaConsumerFactory`/`KafkaConsumerFactory`,
  `AddKafka`, `AddKafkaDependencyHealthCheck` (and, transitively, `Benzene.Core.Middleware` for
  `StreamContext`/`StreamMiddlewareApplication`/the stream operators, and `Benzene.SelfHost` for
  `IBenzeneWorkerStartup`). This is the package's only project reference — nothing is duplicated.
- **Confluent.Kafka** — via `Benzene.Kafka.Core`.

## Tests
`test/Benzene.Core.Test/Kafka/Streaming/`, Moq-faking `IConsumer<TKey,TValue>` the same way
`BenzeneKafkaWorkerTest`/`KafkaConsumerFactoryTest` do for the per-record worker:
- `KafkaStreamCheckpointerTest` — `StoreOffset(offset + 1)`; monotonic never-rewind; idempotence;
  per-partition independence (checkpointing a later record on partition 1 leaves partition 0 alone);
  `CheckpointAll`; commit-as-a-set; per-partition resume offsets (partition 0 restarts at its
  watermark while partition 1 restarts at its first record); `Seek`; and survival of broker rejections
  on store/commit/seek.
- `BenzeneKafkaStreamWorkerTest` — a scripted-consumer harness driving a real worker: flush-on-size,
  flush-on-age, the deadline not being extended by later arrivals, empty poll windows producing no
  empty batches, partition-EOF markers, `ConsumeException` handling with and without records in hand,
  auto-checkpoint on/off, handler-checkpointed frontiers surviving untouched, retry (commit-then-seek)
  vs skip, a failed batch succeeding on retry, `TopicPartition` metadata, option validation, and the
  start/stop lifecycle including an abandoned unflushed batch and a `DrainTimeout`-bounded stop.
- `UseKafkaStreamTest` — the `InlineSelfHostedStartUp` wiring: a batch reaching the pipeline as one
  fan-in run, offsets settling through to the consumer, the transport declared via
  `Benzene.Kafka.Core`'s `AddKafka`, and the health check on/off.

No live-broker integration test yet — `test/Benzene.Integration.Test/Kafka/BenzeneKafkaWorkerLiveTest.cs`
is the pattern to follow (the Event Hubs emulator's Kafka endpoint via `DockerEmulatorCollection`) if
one is added.
