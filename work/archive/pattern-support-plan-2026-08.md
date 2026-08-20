> ARCHIVED 2026-08-20: actioned; executed plan — pattern support shipped (see `CHANGELOG.md` citations and the pattern examples/cookbooks).

# Plan: First-Class Support for the Enterprise Patterns

## Context

The cross-language [patterns section](https://benzene.app) documents how to build enterprise systems
on Benzene — the two-tier architecture, choreography, transactional outbox, CQRS, real-time
streaming, map-reduce, and event sourcing. Writing those patterns surfaced six places where Benzene
makes you *compose* something the framework could support directly, or where a capability is present
on one transport but missing on a sibling. This plan captures all six as scoped changes.

**Guiding constraint — keep the core small.** Benzene's size is a feature. Every third-party-dependent
piece goes in its **own adapter package** (one per library, the established convention), and the core
seams stay tiny. Where a capability already has a home (the small `Benzene.Idempotency` package, the
existing transport packages), it extends that; it does not grow the core.

## Verified facts this plan relies on

- New packages inherit version/metadata/SourceLink from `src/Directory.Build.props`; a `.csproj` sets
  only `TargetFramework=net10.0`, `ImplicitUsings`, `Nullable`, `AssemblyName`, `RootNamespace`,
  `GenerateDocumentationFile`. No central package management — versions pinned inline. AWS SDK packages
  in this repo pin `3.7.301.4`.
- Registration extends `IBenzeneServiceContainer` (not `IServiceCollection`); it exposes
  `AddSingleton/AddScoped/AddTransient` (incl. a `Func<IServiceResolver,T>` factory overload) and
  `IsTypeRegistered<T>()` — there is **no `TryAdd*`**; guard with `IsTypeRegistered` for "add if absent".
- Tests live in the single `test/Benzene.Core.Test` project (xUnit + Moq, no FluentAssertions),
  organized by subfolder; a new src package adds a `ProjectReference` there.
- `IIdempotencyStore` = `TryClaimAsync(key,ct)->ClaimResult` / `CompleteAsync(key,wasSuccessful,ct)` /
  `ReleaseAsync(key,ct)`; the store owns its TTL. `InMemoryIdempotencyStore` is the only shipped store.
- Every non-HTTP inbound transport that supports preset topics registers its topic getter wrapped in
  `PresetTopicMessageTopicGetter<TContext>(inner, PresetTopicHolder)` and registers `PresetTopicHolder`.
  **SNS and EventBridge do not** — they register a bare getter.
- The Kinesis streaming binding wires a real `IStreamCheckpointer`/`KinesisBatchResponse`; the Azure
  **Event Hubs** streaming binding uses a `NullStreamCheckpointer` and reports nothing.
- `BoundedFanOut.WhenAllAsync<TSource,TResult>(source, Func<TSource,Task<TResult>> body, int? maxDop)`
  is public, results in source order — the reusable bounded parallel-map.
- Payload-schema casting decorated only the framework-default request mapper; **gRPC's** protobuf
  request mapper was not wrapped (versioning.md §4.2.1) — response-side casting is universal.
  *(Resolved — see §5: `UsePayloadVersionRequestCasting<TContext,TInner>` + `Benzene.Grpc.Versioning`.)*

---

## 1. Distributed idempotency store *(highest impact)*

**Problem.** Every at-least-once pattern (choreography, outbox, streaming, CQRS, event sourcing)
needs idempotent consumers, but `Benzene.Idempotency` ships only `InMemoryIdempotencyStore`
(single-process). A fleet of Lambdas has no shared store, so the pattern is documented but not
turnkey.

**Placement.** New adapter package **`Benzene.Idempotency.DynamoDb`** (`AWSSDK.DynamoDBv2 3.7.301.4`,
ProjectReference to `Benzene.Idempotency`). A Redis store (`Benzene.Idempotency.Redis`,
`StackExchange.Redis`) is a fast-follow with the same shape. The core `IIdempotencyStore` seam is
unchanged — this is purely a new implementation.

**Design.** `DynamoDbIdempotencyStore : IIdempotencyStore`:
- `TryClaimAsync` → `PutItem` with `ConditionExpression = "attribute_not_exists(pk) OR expiresAt < :now"`
  writing an `InProgress` item; on `ConditionalCheckFailedException`, read the live item and return
  `ClaimResult.AlreadyExists(record)`; otherwise `ClaimResult.Won()`. The condition treats a
  TTL-expired item as absent (DynamoDB TTL deletion is not immediate).
- `CompleteAsync` → `UpdateItem` setting `status=Completed`, `wasSuccessful`, refreshed `expiresAt`.
- `ReleaseAsync` → `DeleteItem`.
- TTL is a ctor `TimeSpan` (default 24h) written as an epoch-seconds `expiresAt` attribute (a real
  DynamoDB TTL attribute so the table self-cleans) plus the read-time guard above. Inject a clock
  `Func<DateTimeOffset>` for tests, mirroring `InMemoryIdempotencyStore`.
- DI: `AddDynamoDbIdempotencyStore(tableName, ttl?)` resolving `IAmazonDynamoDB` from DI via the
  factory overload (consumer registers the client, per `Benzene.HealthChecks.DynamoDb`).

**Tests.** `test/Benzene.Core.Test/Idempotency/DynamoDb/…` against a mocked `IAmazonDynamoDB` (Moq):
first-claim-wins (PutItem succeeds → Won), concurrent-claim-loses (ConditionalCheckFailed → AlreadyExists),
complete-then-claim refused with Completed, release-then-reclaim, TTL-expired-item reclaimable.

**Risk.** Low — additive package, seam unchanged. Main care: exactly modelling the conditional-write +
expiry semantics so a redelivery after TTL is reprocessed, not silently skipped.

## 2. Preset/derived topic on SNS + EventBridge *(smallest)*

**Problem.** `UsePresetTopic`/`UseTopicFrom` (routing foreign/non-Benzene events to a topic) work on
the queue-shaped transports but not SNS or EventBridge, whose `AddSns`/`AddEventBridge` register a
bare topic getter. Ingesting a raw AWS event on those transports has no clean topic mapping.

**Placement.** Existing packages `Benzene.Aws.Lambda.Sns`, `Benzene.Aws.Lambda.EventBridge` — three
lines each, no new package.

**Design.** In `AddSns`/`AddEventBridge`, register `PresetTopicHolder` and wrap the getter:
```csharp
services.AddScoped<PresetTopicHolder>();  // guard with IsTypeRegistered if needed
services.AddScoped<IMessageTopicGetter<SnsRecordContext>>(resolver =>
    new PresetTopicMessageTopicGetter<SnsRecordContext>(
        new SnsMessageTopicGetter(topicAttributeKey), resolver.GetService<PresetTopicHolder>()));
```
Same for EventBridge (`EventBridgeMessageTopicGetter`). Update the doc-comment in
`Benzene.Core.MessageHandlers/Extensions.cs` that lists supported transports, and the two patterns'
SNS/EventBridge caveats on benzene.app.

**Tests.** `Aws/Sns`, `Aws/EventBridge`: a message with no topic attribute + `UsePresetTopic("x")`
routes to `x`; `UseTopicFrom` derives per-message; falls through to the native getter when neither set.

**Risk.** Very low — mirrors five existing transports exactly.

## 3. Event Hubs streaming checkpointer parity

**Problem.** The Kinesis streaming binding tracks a checkpoint and reports resume-from-sequence on
failure; the Azure Event Hubs streaming binding uses `NullStreamCheckpointer` and reports nothing, so
a mid-batch failure has weaker progress semantics than Kinesis.

**Placement.** Existing `Benzene.Azure.Function.EventHub`.

**Design.** **RESOLVED — correct-by-design, no code change.** Investigated: the Azure Functions Event
Hubs trigger owns checkpointing itself (it advances past the whole batch when the function returns,
and not on a throw), so there is **no per-item return channel** for Benzene to drive — unlike AWS
Kinesis, whose event source mapping reads back a resume sequence number. The 2-arg
`StreamMiddlewareApplication` / `MiddlewareApplication` do **not** swallow exceptions (verified: no
`catch`), so a stream-step failure propagates out of the function and the host retries the batch — no
silent progress on failure. `NullStreamCheckpointer` is therefore the right choice, not a gap. Outcome:
documented the checkpoint/failure model on `StreamingExtensions` so it isn't mistaken for a
deficiency; dropped the code change.

**Risk.** None — doc-only.

## 4. Scatter-gather / map-reduce helper

**Problem.** The map-reduce pattern is composed by hand (`BoundedFanOut` + a hand-rolled reduce over
`SendAsync`); there is no app-facing typed scatter-gather with a reduce and a partial-failure policy.

**Placement.** New small package **`Benzene.MapReduce`** (ProjectReferences: `Benzene.Clients` for
`IBenzeneMessageSender`, `Benzene.Core.Middleware` for `BoundedFanOut`). No third-party deps.

**Design.** A thin, allocation-light helper over the sender:
```csharp
Task<TAccum> ScatterGatherAsync<TShard,TPartial,TAccum>(
    IEnumerable<TShard> shards,
    string topic,                                   // worker topic each shard is sent to
    TAccum seed, Func<TAccum,TPartial,TAccum> reduce,
    ScatterGatherOptions? options = null);          // maxDegreeOfParallelism, PartialFailureMode
```
Built on `BoundedFanOut.WhenAllAsync` (bounded map, source order) + a synchronous fold; the
`PartialFailureMode` is `FailFast` (any shard failure throws) or `BestEffort` (collect successes, expose
the failed shards on the result so a reduced-coverage answer can *say so*). Ships against
`IBenzeneMessageSender` so the workers resolve through the routing table (Lambda-to-Lambda, SQS, …).

**Tests.** N shards reduce deterministically; bounded concurrency respected; FailFast throws on a
failed shard; BestEffort surfaces failures without dropping them silently.

**Risk.** Low-medium — API-shape choices; keep it minimal (one method + options + result), resist a
framework.

## 5. gRPC request-side payload casting — DONE

**Problem.** Payload-schema casting wraps the framework-default request mapper but not gRPC's
protobuf-JSON request mapper (versioning.md §4.2.1), so request-side upcasting silently doesn't apply
on gRPC — a real gap for versioned gRPC contracts.

**What shipped.** A new small package `Benzene.Grpc.Versioning` (`AddGrpcPayloadVersioning(...)`), plus
one seam added to `Benzene.Core.Versioning`.

- **Why not the fully-generic "wrap whatever is registered" fix.** The DI abstraction
  (`IBenzeneServiceContainer`) is registration-only: it has no primitive to read or decorate the
  *previously registered* `IRequestMapper<TContext>`. A generic decorator would have to capture the
  prior descriptor, which means adding a decoration capability to all three container implementations
  (Microsoft, Autofac, null-object) — a large, cross-cutting change out of proportion to the gap. The
  `AddPayloadVersioning().ForContext<TContext>()` reflection path also can't know gRPC's concrete
  mapper type. So the generic fix was rejected as not-clean, per "prefer the generic fix *if it's
  clean*".
- **What was done instead (low-risk, faithful).** `Benzene.Core.Versioning` gained
  `UsePayloadVersionRequestCasting<TContext, TInnerRequestMapper>()`, which wraps `CastingRequestMapper`
  over a *named concrete inner mapper* rather than the hardcoded framework default. The existing
  `UsePayloadVersionCasting<TContext>` was refactored to call it with the default mapper — pure
  refactor, no behaviour change (existing 53 versioning tests still green). `Benzene.Grpc.Versioning`'s
  `AddGrpcPayloadVersioning(...)` reuses `AddPayloadVersioning` for the caster declaration + eager
  validation, then re-points the request side at the real `GrpcRequestMapper` (last-registration-wins,
  the established contract). Request side only — gRPC has no response payload mapper (it writes straight
  to protobuf via its result setter), so there is nothing to downcast.
- **Correctness verified.** `ProtobufJsonGrpcMessageAdapter.ConvertRequest<T>` formats the incoming
  protobuf message to proto3 JSON then deserializes into *any* CLR type, so the decorator's "read as the
  incoming version's CLR shape, then upcast" works through gRPC's own bridge.

**Tests (4, all green).** `test/Benzene.Grpc.Test/GrpcPayloadVersioningTest.cs`: the resolved request
mapper is the casting decorator; a V1 gRPC request is read through the protobuf bridge and upcast to the
V2 handler type; no-version and no-casters both pass straight through.

**Spec.** versioning.md §4.2.1's "Known limitation" bullet (informative, .NET) updated to describe the
`UsePayloadVersionRequestCasting` / `Benzene.Grpc.Versioning` resolution.

## 6. Event-sourcing support *(largest)*

**Problem.** Event sourcing is entirely composed today (no event store, aggregate, rehydration,
snapshot, or replay). It's a common, valuable finance pattern worth a small supported core.

**Placement.** New package **`Benzene.EventSourcing`** (core abstractions, no third-party deps) +
**`Benzene.EventSourcing.DynamoDb`** (the append log over DynamoDB, `AWSSDK.DynamoDBv2`). Keep the core
tiny and unopinionated.

**Design (core).**
- `IEventStore`: `AppendAsync(streamId, expectedVersion, events)` (optimistic concurrency),
  `ReadAsync(streamId, fromVersion=0)` → ordered events, `ReadAllAsync(fromCheckpoint)` for projections.
- `IAggregate` / a `Fold` helper: rehydrate = pure `(state, event) => state` over `ReadAsync`.
- `ISnapshotStore` (optional): store/read a folded state at a version.
- Replay = re-read + invoke a projection handler **in process** (`BenzeneMessageApplication`).
**Design (DynamoDb).** `DynamoDbEventStore : IEventStore` — item per event keyed `(streamId, version)`,
`AppendAsync` a transactional/conditional write on `version` for optimistic concurrency; the table's
stream (CDC) is the projection feed (already handled by `Benzene.Aws.Lambda.DynamoDb`).

**Tests.** Append + read round-trips in order; optimistic-concurrency conflict rejected; rehydrate
folds correctly; snapshot + read-from-snapshot; replay reconstructs a projection.

**Risk.** High surface. Ship the **core abstractions + DynamoDb store + tests** minimally; resist
adding a heavy aggregate framework. If budget is short, land the plan + core interfaces and defer the
store. Explicitly a candidate to keep small or split into a follow-up.

---

## Sequencing

Implement smallest-and-safest first, building + testing + committing each independently so any one can
land or be reverted alone:

1. **#2 preset topic** (SNS/EventBridge) — warm-up, tiny, mirrors existing transports.
2. **#1 idempotency DynamoDB store** — highest impact, seam exists.
3. **#4 map-reduce helper** — new isolated package, low risk.
4. **#5 Event Hubs checkpointer** — recon-gated; may reduce to a doc note.
5. **#6 event-sourcing** — core + DynamoDb store, kept minimal.
6. **#3 gRPC casting** — highest risk, done last with versioning tests as guardrail.

Each step: new package `.csproj` + `dotnet sln add --solution-folder src`, code, a `ProjectReference`
in `test/Benzene.Core.Test`, tests, `dotnet build` + targeted `dotnet test` green, one scoped commit.
