# Round 17 - AWS Deep-Dive Review: Kinesis/DynamoDB Streams + Mesh AWS Packages + a GoogleCloud pass (2026-08-30)

**Scope, per the brief:** a deeper second pass (following round 16's lighter one) at commit `4389bfb`
on `main`, targeting the two corners flagged as under-reviewed relative to the rest of the AWS surface:
(1) `Benzene.Aws.Lambda.Kinesis`/`Benzene.Aws.Lambda.DynamoDb` batch/checkpoint/partial-failure logic
under out-of-order or duplicate record delivery, (2) the AWS-specific mesh integration packages
(`Benzene.Mesh.Aws.S3`, `Benzene.Mesh.Aws.Lambda`, `Benzene.Mesh.Discovery.Aws`,
`Benzene.Mesh.Fleet.Aws.XRay`), and (3), time permitting, a fresh pass over `Benzene.GoogleCloud.*` for
the AWS-side bug classes (cancellation threading, batch partial-failure, null-result conventions) that
have had dedicated attention on the AWS side across many rounds but haven't been re-checked on the
Google Cloud side since round 10-11 (`#159`-`#165`).

**Method:** read every file in scope end to end against the design docs it cites
(`work/archive/kinesis-batch-failure-handling-design-2026-07.md`,
`work/archive/batch-failure-handling-2026-07.md`) and the round-16 write-up
(`work/review-round16-aws-2026-08.md`) so nothing already known/accepted gets re-reported, built
concrete failure scenarios for anything that looked suspicious, and proved or disproved them with
throwaway xUnit tests run against the real assemblies. The shared `test/` tree had concurrent review
agents mid-edit in unrelated files with pre-existing compile errors (same situation round 16 hit), so
every red test below was built and run in an **isolated scratch xUnit project** referencing the real
`src/*.csproj` projects directly (under the session scratchpad, not under the repo), rather than by
adding files to the shared test project. `git status`/`git diff` against `/workspace/benzene-dotnet`
are clean of any change from this review (no source or test files modified) as this document is
written - confirmed after every test run.

Three findings below cleared the bar; each is backed by a red test that fails against the code as it
stands at `4389bfb`.

---

## Worth-fixing

### 1. `KinesisStreamCheckpointer`'s index-based watermark silently marks an earlier, still-failed record as "done" once a later-index record in a different partition has been checkpointed - a real, demonstrable case of `PartitionBy` (a pattern this same package's own doc comment recommends) causing silent data loss

`src/Benzene.Aws.Lambda.Kinesis/KinesisStreamCheckpointer.cs` computes the Kinesis resume point purely
from the **original batch position** of the last-checkpointed record (`_records.IndexOf(lastProcessed)`,
monotonically advancing only). `KinesisStreamApplication`'s own class doc explicitly recommends
`StreamOperators.PartitionBy` for exactly this transport - *"handlers can window and re-order it (e.g.
`PartitionBy(r => r.Kinesis.PartitionKey)`), rather than fanning out per record and losing shard
ordering."*

`PartitionBy` (`src/Benzene.Core.Middleware/Streaming/StreamOperators.cs`) buffers the whole input
stream and yields **groups in first-seen-key order, each group fully before the next** - not
interleaved by original position. Consider a batch (shard order, as Kinesis guarantees within one
invocation):

```
index 0: A-1 (partition A)
index 1: B-1 (partition B)   <- fails
index 2: A-2 (partition A)
index 3: B-2 (partition B)
```

A handler that partitions by key and checkpoints as it finishes each record (a natural, idiomatic use
of the documented pattern) processes partition A's group first - A-1 (index 0), then A-2 (index 2) -
checkpointing the watermark up to index 2, all **before ever looking at partition B's B-1** (index 1),
even though B-1 sits *earlier* in the real stream. When B-1 then fails, the checkpointer's watermark is
already at index 2 (only-ever-advances, never rewinds - by design, per the `#`-fix comment on
`CheckpointAsync`). `FirstUncheckpointedSequenceNumber` reports the record *after* index 2 - i.e. B-2 -
as the resume point. B-1, which never succeeded, is silently reported to AWS as already handled and is
**never retried, ever** - the exact "silent data loss" class this codebase's own bar calls out, not the
"safe over-retry" tradeoff the design doc discusses in its §4 caveats (which is about a handler that
simply never checkpoints enough, not about one whose checkpoint watermark actively lies about an
in-flight failure elsewhere in the batch).

This is not a hypothetical misuse: `PartitionBy` is the API this package's own class doc names as the
way to restore per-key ordering on this exact transport, and "checkpoint as you finish each record" is
the natural way to drive it - there is no other checkpoint-placement idiom offered anywhere in the
package or its design doc for a partitioned handler.

**Verified** with a temporary test (`PartitionByCheckpointRedTest`, run in an isolated scratch project
against the real `Benzene.Aws.Lambda.Kinesis` assembly - no mocking at the boundary under test, only a
minimal hand-rolled `IMiddlewarePipeline`/`IServiceResolver` stand-in for the DI plumbing
`MiddlewareApplication<TEvent,TContext,TResult>` needs):

```
var records = [A-1, B-1, A-2, B-2];  // original batch order
// PartitionBy -> process A-1, A-2 (checkpointing each), THEN B-1 -> throws.
var response = await application.HandleAsync(event, resolverFactory);

Assert.Single(response.BatchItemFailures);
Assert.Equal("B-1", response.BatchItemFailures[0].ItemIdentifier);   // FAILS
```

Actual: `response.BatchItemFailures[0].ItemIdentifier == "B-2"` - the checkpointer reports the batch as
resumable from B-2, skipping B-1 (the record that actually failed) and treating it as processed.

**Suggested direction**: the checkpointer needs a notion of "the lowest index across all in-flight
partitions/groups that hasn't yet been confirmed," not a single global monotonic watermark over one
flat index space - e.g. requiring a handler using `PartitionBy` to checkpoint via a per-group cursor
that the checkpointer combines by taking the *minimum* outstanding position across every group touched
so far, or (simpler, safer default) documenting plainly that `PartitionBy` + per-item checkpointing is
unsound for partial-batch-failure reporting today and that a `PartitionBy`-using handler must either
checkpoint nothing until the whole batch succeeds (rely on `AutoCheckpointOnSuccess`) or accept
at-least-once/no-partial-checkpoint semantics. As it stands, the class doc's own recommendation to use
`PartitionBy` is silently unsafe for anyone who follows it and also relies on per-record checkpointing.

### 2. `XRayTraceSource.GetCorrelationAsync` has no de-duplication of trace ids returned across window chunks - the correlation view can show the same physical trace twice

`XRayTraceSource.FetchTraceSummariesAsync` chunks a correlation-lookback window into contiguous
`MaxTraceSummariesWindow`-sized (6h) sub-queries via `ChunkWindow`, and the existing test suite
(`GetCorrelationAsync_ChunksAWideWindow_IntoSubQueriesNoWiderThanTheConservativeBound`) explicitly
asserts the chunks **touch** at the boundary - `seenWindows[i-1].End == seenWindows[i].Start` - i.e.
two adjacent `GetTraceSummariesAsync` calls are issued with the exact same instant as one call's
`EndTime` and the next call's `StartTime`. Nowhere in this codebase is it established (or defensively
handled either way) whether X-Ray's `GetTraceSummaries` treats that shared instant as closed on both
sides; the class's own doc comment on `MaxTraceSummariesWindow` already flags the *width* of the bound
as "not a verified API limit," and the same uncertainty applies to its *edges*.

Regardless of which way that inclusivity question resolves, `GetCorrelationAsync` has **no
deduplication anywhere** between the summary-plane fetch and building `CorrelationView.Traces`:

```csharp
var summaries = await FetchTraceSummariesAsync(start, end, filter, hardCap: null, "GetCorrelationAsync", cancellationToken);
var traceIds = summaries.Select(s => s.Id).Where(id => !string.IsNullOrEmpty(id)).ToList();   // no Distinct()
...
foreach (var batch in Chunk(traceIds, BatchGetTracesMax))
{
    var response = await _xray.BatchGetTracesAsync(new BatchGetTracesRequest { TraceIds = batch }, cancellationToken);
    foreach (var trace in response.Traces ?? new List<Trace>())
    {
        ...
        traces.Add(new TraceView { TraceId = trace.Id, Events = events });   // no de-dup guard
    }
}
```

If the same trace id is present twice in `traceIds` (whether from the boundary-inclusivity case above,
or simply because `GetTraceSummaries` pagination legitimately re-surfaces a trace across two calls
under concurrent indexing - a normal characteristic of eventually-consistent trace-indexing backends),
`BatchGetTraces` looks it up twice (it is a straightforward per-id batch lookup, not a dedup-aware
query), and the loop appends **two identical `TraceView` entries** for one physical trace into
`CorrelationView.Traces`. A user searching a correlation/ticket id in the fleet UI would see the exact
same flow listed twice.

**Verified** with a temporary test (`BoundaryDuplicateRedTest`, isolated scratch project against the
real `Benzene.Mesh.Fleet.Aws.XRay` assembly, `Moq`-based `IAmazonXRay`): a 12h correlation window (two
6h chunks) where the mocked `GetTraceSummariesAsync` returns the same trace id from both chunk calls
(simulating the boundary case), and `BatchGetTracesAsync` echoes back one `Trace` per id actually
requested (matching X-Ray's real per-id lookup semantics, not a dedup-on-the-backend assumption):

```
Assert.Single(view!.Traces);   // FAILS - collection contains 2 identical TraceView entries
```

The same missing-dedup gap applies to `GetRecentFlowsAsync`'s `top = summaries.OrderByDescending(...).Take(limit)`
selection (no `Distinct()` on `s.Id` there either) - a duplicated summary would occupy two slots in the
top-N instead of one, silently displacing a genuinely-different trace from the fleet's recent-flows
list.

**Suggested direction**: de-duplicate `summaries`/`traceIds` by `Id` once, immediately after
`FetchTraceSummariesAsync` returns, in both `GetCorrelationAsync` and `GetRecentFlowsAsync` - cheap,
unconditionally safe regardless of whether X-Ray's own boundary/pagination semantics ever actually
produce a duplicate in practice, and removes the dependency on an assumption this codebase has already
flagged (for the window width) as unverified.

### 3. `PubSubMiddlewareApplication` still uses the pre-`#229` "null `MessageResult` == success" convention every AWS single-context transport moved away from - a Pub/Sub message whose pipeline completes without setting a result is silently, permanently acked

`src/Benzene.GoogleCloud.Functions.PubSub/PubSubMiddlewareApplication.cs:71`:

```csharp
if (_options.RaiseOnFailureStatus && context.MessageResult?.IsSuccessful == false)
{
    throw new PubSubMessageProcessingException(context.Message?.MessageId);
}
```

When `context.MessageResult` is `null` (the pipeline completed without any middleware setting an
outcome - e.g. an unroutable topic, or a pipeline that short-circuits before `MessageRouter` runs, the
*exact* scenario `Benzene.Aws.Lambda.Core.SingleContextEscalatingApplicationBase`'s own doc comment
calls out), `context.MessageResult?.IsSuccessful` is `null`, and `null == false` is `false` - so
`RaiseOnFailureStatus` never fires. The message is treated as an implicit success.

This is precisely the bug class round 16 found in `Benzene.Idempotency.IdempotencyMiddleware.WasSuccessful`
(finding #1 of `work/review-round16-aws-2026-08.md`) - a null-result branch that reads as success,
contradicting the "null = not proven successful, escalate" convention `#229` unified across every
AWS single-context transport (`SingleContextEscalatingApplicationBase`'s current
`context.MessageResult?.IsSuccessful != true` check, used by SNS/S3/EventBridge, matching SQS/DynamoDb's
pre-existing `!= true`/`?.IsSuccessful != true` checks). `PubSubOptions.RaiseOnFailureStatus`'s own doc
comment makes the identical promise for this transport - *"Defaults to true (safe-by-default: a
returned failure is escalated and redelivered)"* - but the implementation only escalates an **explicit**
failure, not an unset one.

The consequence here is worse than the AWS case: Google Cloud Functions delivers exactly **one** Pub/Sub
message per invocation with no partial-failure/batch-item-failure channel at all. Completing without
throwing means the Cloud Functions Framework returns 2xx, Pub/Sub acks the message, and it is
**gone forever** - not "converges to success after one wasted redelivery round-trip" (round 16's
characterization of the AWS case, which still has other messages/records in the same batch to fall back
on), but an outright silent drop of that one message, no log line, no retry, nothing. This package
(`Benzene.GoogleCloud.Functions.PubSub`) hasn't had dedicated review attention since round 10-11
(`#159`-`#165`), before `#229`'s AWS-side convention unification existed to diverge from.

**Verified** with a temporary test (`NullResultRedTest`, isolated scratch project against the real
`Benzene.GoogleCloud.Functions.PubSub` assembly, mirroring the existing
`PubSubFailureHandlingTest.HandleAsync_RaiseOnFailureStatusTrue_HandlerReturnsFailureResult_ThrowsPubSubMessageProcessingException`
test but with a pipeline that never touches `context.MessageResult` at all, instead of setting an
explicit failure):

```
var mockPipeline = ...; // HandleAsync returns Task.CompletedTask, never sets context.MessageResult
var application = new PubSubMiddlewareApplication(mockPipeline.Object, new PubSubOptions { RaiseOnFailureStatus = true });

await Assert.ThrowsAsync<PubSubMessageProcessingException>(
    () => application.HandleAsync(CreateData("msg-null-result"), resolverFactory.Object));   // FAILS - no exception thrown
```

**Suggested direction**: change the check to `context.MessageResult?.IsSuccessful != true` (matching
`SingleContextEscalatingApplicationBase` and `IdempotencyMiddleware`'s corrected convention once round
16's finding #1 lands), so a null result escalates the same way an explicit failure does.

---

## Areas reviewed and found solid (no new finding)

- **`DynamoDbApplication`** (`src/Benzene.Aws.Lambda.DynamoDb/DynamoDbApplication.cs`): sequential,
  stop-at-first-failure, correct `SequenceNumber ?? EventId` fallback, and (unlike Kinesis) has no
  reordering operator equivalent to `PartitionBy` available to it, so it isn't exposed to finding #1's
  failure mode - re-confirmed this is a structurally different, simpler shape (design decision DS5,
  already documented) rather than an oversight.
- **`KinesisStreamCheckpointer`'s reference-equality/foreign-record guard** (the round-15/16-era
  `IndexOf` rewind fix): re-confirmed solid for its stated purpose (a projected/transformed copy of a
  record, or literal duplicate-by-content records at different positions, can't rewind the watermark) -
  finding #1 above is a distinct failure mode (correct-record, wrong-assumption-about-index-implies-
  earlier-completion) that guard doesn't and can't address.
- **`Benzene.Mesh.Aws.S3` (`S3MeshArtifactStore`)**: re-checked against `#151`'s
  `FileSystemMeshArtifactStore` temp-file+rename atomicity fix for an S3-specific equivalent. Found none
  needed: a single `PutObjectAsync` (no `TransferUtility`/multipart split - the SDK only chunks
  automatically for much larger uploads than a JSON manifest) is already atomic at the object level (S3
  has offered strong read-after-write consistency since Dec 2020, and a GET in flight during an
  overwrite completes against the version it started reading, never a torn/partial body) - there is no
  analogous "truncate then write" hazard for `TryReadAsync` to race against. The manifest-published-last
  ordering fix in `MeshAggregator.RunOnceCoreAsync` (the actual defense against a reader seeing
  `manifest.json` reference an artifact that hasn't landed yet) is storage-backend-agnostic and already
  applies uniformly to both `FileSystemMeshArtifactStore` and `S3MeshArtifactStore`. Also checked
  `S3MeshArtifactStore.Key`'s lack of `FileSystemMeshArtifactStore.ResolveWithinRoot`'s traversal guard -
  not a gap: S3 keys are opaque flat strings with no `..`-segment resolution (unlike a filesystem path),
  so a key literally containing `"../"` characters cannot escape a prefix the way `Path.Combine` +
  `Path.GetFullPath` can on disk.
- **`Benzene.Mesh.Discovery.Aws.AwsLambdaDiscoveryProvider`**: `ListFunctions` pagination (`Marker`/
  `NextMarker` loop) is correct and unbounded-safe. The per-function `ListTags` fan-out is bounded by an
  8-way `SemaphoreSlim` specifically to avoid tripping the Lambda control-plane's rate limit across a
  large fleet, with per-function fetch-isolation (a `ListTags` failure drops only that function, doesn't
  fail the whole `Task.WhenAll`) and order-preserving results. No new pagination or throttling gap found
  beyond what's already handled; a throttled `ListTags` call would in practice first hit the AWS SDK's
  own built-in exponential-backoff retry before ever reaching this code's catch-and-drop, so the
  documented "drop, don't fail the run" behavior for a persistently-failing function is a reasonable,
  already-considered tradeoff, not an oversight.
- **`Benzene.Mesh.Aws.Lambda.LambdaMeshServiceSource`**: the `IAwsLambdaClient.SendMessageAsync` call
  has no `CancellationToken` parameter (same family as round 16's finding #2), but this call site already
  mitigates it via `.WaitAsync(cancellationToken)` racing the awaited call against the caller's token, so
  `MeshAggregator`'s `PerServiceFetchTimeout` is honored from the caller's perspective even though the
  underlying Lambda Invoke can't itself be aborted mid-flight - a documented, already-considered
  limitation, not a new gap.
- **`XRayTraceSource`'s `#77` hard-pagination-cap heuristic** under a burst of near-simultaneous traces:
  re-traced: the cap is a generous multiple (`limit * 20`) applied order-agnostically across the full
  page set before a client-side newest-first `Take(limit)`, specifically designed (per its own doc
  comment) to not bias by page order the way the old early-stop heuristic did. A burst large enough to
  hit the cap mid-window is logged (not silent) as a documented, honest truncation - found no new edge
  case beyond what `#77` already closed.
- **`Benzene.GoogleCloud.Functions.Http`/`Benzene.GoogleCloud.Functions.Core`**: thin, ASP.NET-style
  request/response adapters (`GoogleCloudFunctionHost<TStartUp>.HandleAsync` is a direct pass-through to
  the shared entry-point application) - no batch/partial-failure surface and no bespoke null-result
  convention to diverge from finding #3's fix; not a locus for the bug classes this pass was looking for.

No other findings cleared this codebase's bar (genuine correctness bug, race, resource leak, silent
data corruption, or spec-contract violation) after this pass. `Benzene.Aws.Lambda.Sqs`/
`Benzene.Aws.Lambda.Sns`/`Benzene.Aws.Lambda.S3`/`Benzene.Aws.Lambda.EventBridge` batch handling itself
was not re-read line-by-line here (round 16 already did that pass with no interaction gaps found beyond
its own findings); this round's time went to the two explicitly-flagged under-reviewed corners plus the
GoogleCloud sweep, per the brief.
