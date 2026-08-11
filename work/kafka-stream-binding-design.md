# Kafka Windowed/Checkpointed Stream Binding — Design Notes (2026-08-11)

**Status:** Implemented — `src/Benzene.Kafka.Streaming` (`UseKafkaStream<TKey,TValue>`). This
document records the two design forks the package turns on, so they can be revisited deliberately
rather than reverse-engineered. Companion to `work/kinesis-batch-failure-handling-design.md`, whose
§2/§3.2 this repeatedly contrasts with.

## 1. The gap

The cross-language spec's `docs/patterns/streaming-processing.md` names Kinesis, Azure Event Hubs and
the Cosmos DB change feed as having windowed/checkpointed stream bindings. Kafka had only a plain
per-record consumer (`BenzeneKafkaWorker`), which fans **out** — one context, one DI scope and one
message-handler route per record. Once a batch has been fanned out into isolated per-record contexts,
windowing, aggregation and re-ordering by key are all structurally impossible, so the streaming
engine in `Benzene.Core.Middleware/Streaming` was simply unreachable from Kafka.

`Benzene.Kafka.Streaming` is additive: `BenzeneKafkaWorker` is untouched, and the new package
project-references `Benzene.Kafka.Core` and reuses `BenzeneKafkaConfig`, `IKafkaConsumerFactory`/
`KafkaConsumerFactory`, `AddKafka<TKey,TValue>()` and `AddKafkaDependencyHealthCheck(config)` rather
than reproducing any of them.

## 2. Why this needs its own worker loop, not just a new application type

The three existing stream bindings all receive batches from something else:

| Binding | Who produces the batch |
| --- | --- |
| `Benzene.Aws.Lambda.Kinesis` | AWS's Lambda event source mapping (no worker at all) |
| `Benzene.Azure.Function.EventHub` | The Functions host's trigger |
| `Benzene.Azure.CosmosDb` | The Cosmos SDK's Change Feed Processor (a worker, but the SDK batches) |

Kafka has no batch trigger and no batching client — `IConsumer.Consume()` returns one record. So
`Benzene.Azure.CosmosDb` is the right *architectural* sibling (a long-running `IBenzeneWorker`,
started/stopped like `BenzeneKafkaWorker`) and `Benzene.Aws.Lambda.Kinesis` is the right *API-shape*
sibling (`StreamContext<TItem>`, `IStreamCheckpointer<TItem>`, `AutoCheckpointOnSuccess`), but the
batching loop itself is new code with no precedent in the repo.

## 3. Fork 1 — the windowing trigger

**Decision:** flush on `MaxBatchSize` records **or** `MaxBatchWait` elapsed since the batch's *first*
record, whichever comes first. The wait is a per-batch deadline anchored to the first record; it is
**not** extended by later arrivals.

**Rejected:** a rolling idle timer ("flush once nothing has arrived for `MaxBatchWait`"). It bounds
nothing under sustained load — a topic delivering a record every few milliseconds never goes idle, so
the batch would only ever flush on size and the oldest record's latency would be unbounded. That is
the opposite of what a latency-sensitive aggregator (the motivating use case: a market-data
aggregator) needs.

The first-record deadline gives the guarantee worth stating in the docs — *no record is buffered
longer than `MaxBatchWait` before its batch is flushed* — and degrades gracefully in both directions:
flush on size when busy, on age when quiet.

**Mechanics:** `Consume(TimeSpan)` rather than `Consume(CancellationToken)`, because the token-based
overload blocks until a record arrives and so can't honor a deadline. `PollTimeout` (default 250ms)
caps how long the loop can sit inside `Consume` without noticing shutdown, since the timeout overload
takes no token; the last poll of a batch is shortened to land exactly on the deadline.

## 4. Fork 2 — what "checkpoint this record" means for a multi-partition batch

**Decision:** one monotonic, never-rewinding watermark **per topic-partition**.
`CheckpointAsync(record)` advances only that record's own partition, and says nothing about any other.

Kinesis keeps a *single* watermark because a Lambda Kinesis batch comes from one shard and AWS reads
only the first reported failure, retrying every record from that sequence number on
(`kinesis-batch-failure-handling-design.md` §2). Kafka's commit unit is `(topic, partition) → offset`,
and records on different partitions are by definition unordered relative to one another — there is no
cross-partition order to preserve. Kinesis's single shard-order frontier therefore *collapses* into
one independent frontier per partition; this is not a weakening of the model but the same model
applied to a transport with N ordered sequences instead of one.

**Why not a batch-order frontier** (checkpointing batch index *i* marks 0..*i* done, Kinesis-style):
it would mark records on *other* partitions as done purely because they happened to be consumed
earlier, even though the handler said nothing about them. A `PartitionBy` handler — the expected shape
here — processes partitions in some arbitrary order, so this would silently lose data on the very
usage pattern the binding exists for. Kinesis has to live with this hazard (its CLAUDE.md documents it
as an unavoidable consequence of the shard contract); Kafka does not, so it shouldn't.

**Inherited caveat, unchanged from Kinesis:** a committed offset is a watermark with no gap tracking,
so committing offset 10 marks 0–10 done even if 7 failed. A handler must checkpoint a partition's
*frontier*, not the last record it touched. Processing each partition in offset order makes that
automatic; backwards checkpoints are ignored so an out-of-order or projected-copy checkpoint can't
rewind the resume point, but forward gaps stay the handler's responsibility. Documented, not
enforced — enforcing it would require tracking per-record completion the streaming abstraction
doesn't model.

**Store *and* commit:** `CheckpointAsync` → `StoreOffset(offset + 1)`, keeping librdkafka's own
auto-commit and rebalance-time commit in step with real progress; end of batch → explicit
`Commit(offsets)` of the exact watermark set, so a batch boundary is a durable acknowledgement rather
than something waiting on the 5-second auto-commit timer. `EnableAutoOffsetStore` is forced `false`
unconditionally at `StartAsync`, which is why there is no `CommitOnlyOnSuccess` equivalent — that
behavior *is* the binding.

## 5. Fork 3 — failure policy for a batch that can't cleanly resume

Kinesis catches the exception and reports a resume sequence number; Cosmos offers config-driven
catch-and-skip vs propagate-and-retry. Kafka can do something neither can: **rewind each partition
independently**.

| `CatchHandlerExceptions` | Behavior |
| --- | --- |
| `false` (default) | Commit whatever the handler checkpointed, then `Seek` each partition back to *its own* first unprocessed record (watermark + 1, or its first offset in the batch) and retry after `FailedBatchRetryDelay`. Nothing lost; partial progress kept; a reliably-failing batch retries forever. |
| `true` | Log, checkpoint the whole batch anyway, move on — the poison window is permanently skipped and the partitions keep moving. |

The default matches `BenzeneCosmosChangeFeedConfig.CatchHandlerExceptions` (also `false`) for the same
reason: Kafka, like the change feed, can genuinely redeliver, so no-loss is the honest default and
skipping should be opted into. It deliberately does **not** match
`BenzeneKafkaConfig.CatchHandlerExceptions` (`true`) — the per-record worker has no watermark to seek
back to, so catching is its only way to keep a partition moving. The two workers make different offset
promises, so the knob is not shared.

The `Seek` is why the mid-partition resume Kinesis structurally can't express is available here: its
resume point is one sequence number for the whole batch, so a multi-shard batch cannot resume
mid-shard-set. `FailedBatchRetryDelay` (default 1s) exists so a batch that fails immediately — a
downstream outage — can't spin as fast as the pipeline can throw; same rationale as
`BenzeneKafkaConfig.ConsumeExceptionRetryDelay` on the consume side.

**Three paths deliberately refuse to acknowledge work that wasn't done:**
1. Shutdown holding an unflushed partial batch → abandon it. Nothing was checkpointed, so it's
   redelivered on restart. Flushing it would risk a hung shutdown for records that cost nothing to
   re-read.
2. Shutdown mid-flush → commit only explicit checkpoints; never auto-checkpoint a partially-processed
   batch. (`BenzeneCosmosChangeFeedWorker` makes the same call.)
3. A handler returning successfully having checkpointed only part of the batch → keep exactly that.
   The uncheckpointed tail isn't committed and is redelivered after a restart or rebalance. A
   successful return isn't by itself a reason to rewind and re-run.

## 6. Rebalances

`SetPartitionsRevokedHandler` (wired through `IKafkaConsumerFactory.Create(config, configureBuilder)`)
`Commit()`s already-stored offsets before releasing partitions. Safe *because* auto-offset-store is
off: the stored offsets are the checkpointer's watermarks, never the consumer's raw read position —
the precise hazard `BenzeneKafkaWorker`'s revoke handler documents for its auto-store path.

The in-progress batch is **not** flushed during a revoke: that would run a whole pipeline inside the
rebalance callback and risk blowing `max.poll.interval.ms`. Its records are redelivered to the next
owner instead — at-least-once, at the cost of repeating that window's work. `StoreOffset`/`Commit`/
`Seek` against a revoked partition are each caught and logged, since a mid-batch revoke makes all
three legitimately fail.

**Open to revisit:** a bounded flush-on-revoke (flush if the batch can be processed within some
fraction of the rebalance budget) would cut duplicate work at the cost of real complexity and a new
failure mode. Not worth it until someone measures the duplication.

## 7. Deviations from the sibling packages, and why

- **The checkpointer is public**, where `KinesisStreamCheckpointer` and
  `CosmosChangeFeedStreamCheckpointer` are internal. It carries the trickiest logic in the package and
  this repo has no `InternalsVisibleTo` wiring — Kinesis's CLAUDE.md explicitly notes its checkpointer
  is only reachable in tests through the application's public surface. Making it public buys direct
  unit tests for the never-rewind and per-partition rules, which are exactly the rules worth pinning.
- **The application doesn't catch pipeline exceptions**, matching `CosmosChangeFeedApplication` rather
  than `KinesisStreamApplication`. Only the worker holds the consumer, so only the worker can seek;
  the retry-vs-skip decision has to live there.
- **`ConsumeResult.TopicPartitionOffset` is never null** (Confluent.Kafka computes it from
  `Topic`/`Partition`/`Offset`), so the "real consumed record vs. projected copy" guard tests `Topic`.
  A null topic throws out of `TopicPartition.GetHashCode()` the moment it's used as a dictionary key —
  found by `KafkaStreamCheckpointerTest.CheckpointAsync_IgnoresAnItemWithNoTopicPartitionOffset`.
