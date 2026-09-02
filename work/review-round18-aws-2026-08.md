# Round 18 - AWS Transports and Adapters Review (2026-09-01)

**Scope, per the brief:** the full AWS transports/adapters surface at commit `7f642b2` on `main` —
every `Benzene.Aws.Lambda.*` package, the self-hosted `Benzene.Aws.Sqs` consumer, every
`Benzene.Clients.Aws.*` outbound client, `Benzene.ClaimCheck.Aws.S3`, the AWS mesh packages
(`Benzene.Mesh.Aws.Lambda`, `Benzene.Mesh.Aws.S3`, `Benzene.Mesh.Discovery.Aws`,
`Benzene.Mesh.Fleet.Aws.XRay`, `Benzene.Mesh.Usage.CloudWatch`), and the DynamoDB-backed stores
(`Benzene.HealthChecks.DynamoDb`, `Benzene.EventSourcing.DynamoDb`, `Benzene.Idempotency.DynamoDb`,
`Benzene.Outbox.DynamoDb`).

**Method:** read `work/review-round17-aws-deep-2026-08.md` in full first (this territory's immediately
prior deep pass) plus the relevant slices of `work/outstanding-bugs.md`, to avoid re-litigating
anything already found, fixed, or ruled a deliberate tradeoff. Rounds 12-17 have already put
substantial, repeated attention on this exact territory — Kinesis/DynamoDB Streams checkpointing,
SQS/SNS/S3/EventBridge batch partial-failure handling, the DynamoDB stores' transact-item accounting
and expiry boundaries, cancellation-token propagation across every `Benzene.Clients.Aws.*` client, the
null-`MessageResult` convention unification (`#229`), and the AWS mesh packages' pagination/dedup — and
this round confirmed all of that work is intact in current source. Given that, this pass leaned toward
corners with comparatively little prior dedicated attention per `outstanding-bugs.md` (grepped for
every package name in scope): `Benzene.Mesh.Usage.CloudWatch` (never previously mentioned in any round's
findings), `Benzene.Outbox.DynamoDb`, `Benzene.ClaimCheck.Aws.S3`, `Benzene.HealthChecks.DynamoDb`, the
self-hosted `Benzene.Aws.Sqs` consumer (explicitly named as this round's focus in the brief), and the
`Benzene.Aws.Lambda.Core`/`.Hosting`/`.HttpBridge`/`.AspNet`/`.XRay` foundation packages. Every finding
below was traced by hand against the real source (no `dotnet build`/`dotnet test` available in this
environment) and cross-checked against the package's own `CLAUDE.md` and existing test coverage to
confirm the failure path is untested, not merely unread.

One finding cleared the bar.

---

## Worth-fixing

### 1. `CloudWatchUsageSource.FetchUsageAsync` doesn't merge `GetMetricData` results across `NextToken` pages by query `Id` - a metric whose window needs more than one page of datapoints is reported as two (or more) separate, dimensionally-identical `MeshUsageEntry` rows, each holding only a fragment of the true count

`src/Benzene.Mesh.Usage.CloudWatch/CloudWatchUsageSource.cs`'s `GetMetricDataAsync` (the private
pagination helper) issues one `GetMetricData` call per page and flattens every page's
`MetricDataResults` into one list with no merge step:

```csharp
private async Task<List<MetricDataResult>> GetMetricDataAsync(
    List<MetricDataQuery> queries, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken)
{
    var results = new List<MetricDataResult>();
    string? nextToken = null;
    do
    {
        var response = await _cloudWatch.GetMetricDataAsync(new GetMetricDataRequest
        {
            MetricDataQueries = queries,
            StartTimeUtc = startUtc,
            EndTimeUtc = endUtc,
            NextToken = nextToken,
        }, cancellationToken);

        if (response.MetricDataResults != null)
        {
            results.AddRange(response.MetricDataResults);   // <- no merge by Id
        }

        nextToken = response.NextToken;
    }
    while (!string.IsNullOrEmpty(nextToken));

    return results;
}
```

CloudWatch's own `GetMetricData` pagination contract (this is the documented reason `NextToken` exists
at all, distinct from the outer "more metrics than fit in one call" chunking this file already handles
correctly via `MaxQueriesPerCall`/`Chunk`) is that when a **single query's own datapoint series** is too
large to return in one response, AWS returns as much of that series as fits plus a `NextToken`, and the
*next* call - with the *same* `MetricDataQueries`/time range - returns the **same query `Id`** again,
now carrying the remaining `Values`/`Timestamps` for that series. This is exactly the case a wide window
at fine granularity produces: this adapter's default `PeriodSeconds` is 60 over a default 24h
`TimeWindow`, i.e. 1440 datapoints per live (topic, transport, status) combination, and CloudWatch's own
`GetMetricData` cap is 100,800 total datapoints per call - so once a mesh has more than roughly 70
concurrently-live dimension combinations in its default 24h window (not a large or unusual fleet:
several topics x a couple of transports x the bounded `result` vocabulary easily clears that), pagination
via `NextToken` is a normal, expected path here, not an edge case.

Because `GetMetricDataAsync` returns the raw flattened list, the caller in `FetchUsageAsync` treats each
`MetricDataResult` it sees as a separate, complete answer:

```csharp
foreach (var result in await GetMetricDataAsync(chunk, start.UtcDateTime, end.UtcDateTime, cancellationToken))
{
    if (!byId.TryGetValue(result.Id, out var metric)) { continue; }
    var count = (long)Math.Round((result.Values ?? new List<double>()).Sum());
    if (count <= 0) { continue; }
    ...
    entries.Add(new MeshUsageEntry(topic: topic, ..., count: count, ..., source: MeshUsageSource.CloudWatch));
}
```

When a query's series is split across two pages, this loop runs twice for the same `Id`/dimension
combination, once per page, and appends **two** `MeshUsageEntry` objects for the exact same (topic,
transport, status) triple - each summing only the fraction of `Values` that landed on its own page,
neither one the true total. This is a direct violation of this package's own documented feed contract
(`docs/mesh-usage-feed.md` §2): *"An entry is a count at exactly the dimensions it states ...
**Entries from one source never overlap**; consumers aggregate by grouping over whichever stated
dimensions they need."* Two entries sharing every stated dimension (topic/version/service/transport/
status all identical) is exactly the overlap that rule promises never happens.

**Blast radius, honestly assessed.** `Benzene.Mesh.Aggregator.MeshAggregator.AttributeTopicToEdge`
(`src/Benzene.Mesh.Aggregator/MeshAggregator.cs:457`), the one first-party consumer read in this
review, sums `relevantEntries.Sum(entry => entry.Count)` when computing a structural edge's usage-derived
request rate - so for *that* specific consumer, two same-dimension fragments summed together still
happen to add back up to the correct total (the fragmentation is invisible there, purely by luck of the
aggregation shape). What is not safe is anything that trusts the feed's own documented invariant instead
of re-summing from scratch: the `usage.json` artifact itself carries the duplicate rows verbatim (it is
published straight from `MeshUsage.Entries`, no dedup step in `MeshAggregator` either), so any other
reader of that artifact - the Mesh UI's own per-topic usage table/"by transport"/"by status" breakdown
panels (`docs/mesh-usage-feed.md` §3, "the UI renders what's present"), a third-party dashboard, or a
future consumer that (reasonably, per the documented contract) does a 1:1 render of entries instead of a
defensive group-and-sum - would show the same (topic, transport, status) row **twice**, each with a
fraction of the real count, which reads as two different, both-wrong numbers rather than one correct one.

This is the same failure *class* round 17 found in `XRayTraceSource.GetCorrelationAsync`/
`GetRecentFlowsAsync` (finding #2, `work/review-round17-aws-deep-2026-08.md`) - a paginated AWS
read whose de-duplication-by-identity is missing, so a legitimate multi-page response produces
duplicate application-level rows - just triggered by CloudWatch's per-series `NextToken` semantics
instead of X-Ray's window-chunk boundary. It is a genuinely fresh finding: grepping
`work/outstanding-bugs.md` for `CloudWatchUsage`/`GetMetricData` returns nothing from any prior round,
and the package's own test file confirms the gap is untested, not merely unnoticed -
`test/Benzene.Mesh.Test/CloudWatchUsageSourceTest.cs`'s `GetMetricDataAsync` mock
(`mock.Setup(x => x.GetMetricDataAsync(...))`, line 33) always returns a response with `NextToken`
unset, so the merge-by-`Id` path this finding describes has never been exercised by a test.

**Suggested direction:** accumulate `GetMetricDataAsync`'s results into a `Dictionary<string,
MetricDataResult>` keyed by `Id`, appending a later page's `Values`/`Timestamps` onto the entry already
collected for that `Id` (or, simpler, keep a running `Dictionary<string, double>` of per-`Id` summed
`Values` and skip materializing `MetricDataResult` objects at all, since `FetchUsageAsync` only ever
reads `.Id` and `.Values.Sum()`) rather than returning a flat list of raw per-page results. A regression
test would extend the existing mock to return a first page with `NextToken = "page2"` and a partial
`Values` list for one query id, then a second page (called with that `NextToken`) completing the same
id's `Values`, and assert `FetchUsageAsync` returns exactly one `MeshUsageEntry` for that dimension
combination whose `Count` is the sum of both pages - `Assert.Single(usage.Entries.Where(e => e.Topic ==
"..."))` is the shape that fails against the code as it stands today (two entries, not one).

---

## Areas reviewed and found solid (no new finding)

- **`Benzene.Outbox.DynamoDb`** (`DynamoDbOutboxStore`/`DynamoDbOutboxTransaction`/
  `DynamoDbOutboxItemMapper`): re-read end to end against its own `CLAUDE.md`. The claim-fencing
  (`leaseToken`-scoped `ConditionExpression` on every settle call), the atomic "free OR lapsed" claim
  condition, and the non-destructive-peek-then-drain-only-after-success commit discipline (already a
  documented round-17-era fix) all match the doc precisely; traced every settle method's
  `ConditionExpression`/`UpdateExpression` pairing by hand and found no gap. `ClaimDueAsync`'s
  `Limit = batchSize` query against the sparse GSI is a key-condition-only query with no
  `FilterExpression`, so `Limit` bounds the query to exactly the caller's requested batch size with no
  under-return risk from post-filter truncation - not a pagination bug (the caller wants *at most*
  `batchSize` claimed, not every due item).
- **`Benzene.Aws.Sqs` (`SqsConsumer`/`SqsConsumerApplication`)** - the self-hosted worker this round's
  brief named as a specific focus area. Re-verified the null-`MessageResult` convention already reads
  `pair.Context.MessageResult?.IsSuccessful != true` (the corrected, `#229`-aligned form, not the
  `== false` bug class round 17 found live in `Benzene.GoogleCloud.Functions.PubSub`); the per-message
  task returns its failed `Message` rather than appending to a shared list specifically to avoid a
  documented race that would otherwise leak a failed message into the delete batch; the poll-loop's
  error backoff (immediate retry on the first failure, geometric growth capped at 30s from the second)
  and its `CancellationToken.None` use for the post-success delete call (so a shutdown signal firing
  between "handled" and "deleted" can never manufacture a silent double-process) are both correct and
  match their doc comments. `DeleteMessageBatchAsync` is never called with more than 10 entries (bounded
  by SQS's own `MaxNumberOfMessages<=10` receive limit), so no batching/chunking gap there either.
- **`Benzene.ClaimCheck.Aws.S3`**: `PutAsync`/`GetAsync`/`ParseAndValidate` match the CLAUDE.md exactly;
  the prefix-string mismatch check (rather than `System.Uri` parsing, deliberately, since a Benzene
  topic's `:` doesn't round-trip cleanly through `Uri`) correctly rejects a foreign bucket/prefix before
  ever calling S3, and a 404 on `GetObjectAsync` maps to `null` rather than throwing.
- **`Benzene.HealthChecks.DynamoDb`**: a small, correct wrapper around `DescribeTableAsync`; cancellation
  forwarded straight through with no extra layer, `OperationCanceledException` explicitly re-thrown
  (not swallowed into a false "unhealthy") so `ExceptionHandlingHealthCheck` can classify it as
  `Cancelled` rather than a connectivity failure.
- **`Benzene.Aws.Lambda.Core`** (`AwsLambdaMiddlewareRouter`, `AwsLambdaEntryPoint`,
  `AwsEventStreamContext`, `BenzeneMessageLambdaHandler`): the `Handled` claim-detection flag (a
  documented round-era fix - a plain null check on `Response` was previously unreachable dead code) is
  intact and correctly `OR`s an explicit `MarkHandled()` call with "some binding actually wrote bytes";
  the shared static `SharedJsonSerializer`/per-router source-gen serializer split is exactly as
  documented, with no per-invocation reflection-metadata rebuild.
- **`Benzene.Aws.Lambda.XRay`** (`XRayMiddlewareDecorator`): the `TryBeginSubsegment`/`Safe`
  try/catch pairing around every recorder call correctly treats `EntityNotAvailableException` as
  "no active segment, run untraced" rather than letting an observability concern fault the real pipeline;
  begin/end are correctly paired (a subsegment is only ever closed in the `finally` when it was actually
  opened).
- **`Benzene.Clients.Aws.StepFunctions`** (`StepFunctionsClient`): the idempotency-name sanitizer's
  hash-suffix collision guard (hashing the *original*, pre-sanitized name so two distinct names that
  happen to sanitize/truncate alike still land on different execution names) and the
  `ExecutionAlreadyExistsException` → byte-exact-input-comparison → `Accepted`/`Conflict` resolution path
  both check out against their own doc comments and the `[DECISION]`-recorded rejected alternative
  (`work/bug-fix-designs-2026-08.md` WP-6b) they cite.
- **`Benzene.Mesh.Aws.Lambda.AwsLambdaMeshServiceDispatcher`**: the write-side counterpart to
  `LambdaMeshServiceSource` (round 17 already cleared the source's own `.WaitAsync(cancellationToken)`
  cancellation mitigation for `IAwsLambdaClient.SendMessageAsync`'s missing native `CancellationToken`
  parameter) uses the identical mitigation and reuses the same lazily-constructed client - no new gap.
- **`Benzene.Clients.Aws.Lambda.AwsLambdaHealthCheck`**: re-checked against a stale note in this
  package's own `CLAUDE.md` claiming the Active-mode ping "still doesn't forward the token" - that is
  no longer true of the actual source (`AwsLambdaHealthCheck`'s `(lambdaName, amazonLambda, logger, mode,
  cancellation)` constructor threads the accessor into `AwsLambdaBenzeneMessageClient`, matching
  `work/outstanding-bugs.md`'s `#268`/WP-D resolution entry). A documentation staleness note, not a
  functional bug - flagging here only so a future round doesn't waste time re-deriving the same
  discrepancy from the same stale prose.
- **`Benzene.Aws.Lambda.HttpBridge`/`Benzene.Aws.Lambda.AspNet`/`Benzene.Aws.Lambda.Hosting`**: read the
  bootstrap/bridge composition end to end (`AwsLambdaBootstrap`, `BenzeneLambdaServer`,
  `HttpBridgeLambdaHandler`) against their `CLAUDE.md`s; the entry-point disposal split (build-and-own
  vs. caller-owns overloads) and the documented ALB-vs-REST registration-order gotcha are both correctly
  implemented and already covered by `HttpBridgeAlbTest` per the doc's own citation.

No other findings cleared this codebase's bar (genuine correctness bug, race, resource leak, silent
data corruption, or spec-contract violation) in this pass. `Benzene.Aws.Lambda.Sqs`/`.Sns`/`.S3`/
`.EventBridge`/`.Kafka`/`.Kinesis`/`.DynamoDb` batch/checkpoint logic, the DynamoDB event-store/
idempotency-store transact-item accounting, and the AWS mesh discovery/artifact-store packages were not
re-read line-by-line here beyond confirming their round-16/17 findings are still fixed in current source
(all were) - rounds 15-17 already did exhaustive passes over exactly that territory with no
newly-reopened gaps found on re-check.
