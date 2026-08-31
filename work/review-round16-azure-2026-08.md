# Round 16 - Azure Packages Review (2026-08-30)

**Scope, per the brief:** every `src/Benzene.Azure.*` package - the Azure Functions hosting bridges
and source generator, Event Hub/Service Bus/Cosmos DB/Blob/Queue Storage/Timer triggers, and the Azure
mesh integration - re-reviewed at commit `28473b0` on `main`, with particular attention to (a) the
interaction between round-15's `#230` (`BoundedFanOut` cancellation) and `#231` (`TimerApplication`
escalation), (b) any remaining trigger-type-specific source-generator validation gap beyond `#9-11`/
`#38-40`, (c) Cosmos DB/Event Hub/Service Bus worker cancellation/settlement edge cases beyond
`#108`/`#115-117`/`#63`, and (d) Blob/Queue Storage triggers and the Azure mesh discovery/deployment
path, which had less dedicated attention in prior rounds.

**Method:** read every file in scope against the rounds 1-15 fix record (`work/outstanding-bugs.md`)
so nothing already known/accepted is re-reported, paying particular attention to shapes the codebase
has fixed once in one transport family and never audited for the sibling families (the `#228`/`#229`
AWS-Lambda escalation fixes, the `#39` source-generator required-field checks). Built concrete failure
scenarios for anything that looked suspicious and proved or disproved them with throwaway xUnit probe
tests run against the real, already-built assemblies - via small standalone scratch test projects
under `/tmp/.../scratchpad` referencing the relevant `src/*.csproj`s directly (the shared
`test/Benzene.Core.Test` project would not build in isolation: several *other* concurrent review
agents' own uncommitted, unrelated scratch files under `test/Benzene.Core.Test/**` and
`test/Benzene.Mesh.Test/**` do not currently compile - not this review's concern, and left untouched).
Two of the three findings below were also independently reproduced with a temporary edit inside the
real `test/Benzene.Core.Test/Azure/TimerFailureHandlingTest.cs` and
`test/Benzene.Core.Test/Autogen/AzureFunctions/AzureFunctionTriggerGeneratorTest.cs`, each run once to
confirm red, then reverted with `git checkout --`. `git status`/`git diff` confirmed clean (no source
or test files modified in the real repo) before finishing this review.

Three findings cleared the bar, all reproduced with a passing "red" probe (the assertion that proves
the bug passes against the code as it stands at `28473b0`).

---

## Worth-fixing

### 1. Every Azure Functions trigger's `CatchExceptions=true` silently swallows an infrastructure/DI-wiring failure forever - the `#228` bug, unfixed outside AWS Lambda

`#228` (round 15) fixed exactly this defect in `Benzene.Aws.Lambda.Core.SingleContextEscalatingApplicationBase.ProcessAsync`
(shared by SNS/S3/EventBridge) and it already existed in `Benzene.Aws.Lambda.Sqs.SqsApplication`: under
`CatchExceptions = true`, a `BenzeneResolutionException` (or anything with one in its `InnerException`
chain - `BenzeneFailure.IsInfrastructure`) is not this message's fault, will fail identically for
*every* message, and must escape the per-message containment and fail the whole invocation loudly -
otherwise "the whole invocation reports success while every message fails the same way, forever" (the
fix's own words). The fix added an explicit `if (isInfrastructure) { throw; }` carve-out inside the
`catch (Exception ex) when (_catchExceptions)` block.

**`Benzene.Azure.Function.Core.AzureFunctionBatchApplicationBase<TContext, TState>.ProcessItemAsync`**
(`src/Benzene.Azure.Function.Core/AzureFunctionBatchApplicationBase.cs:178-189`) has the structurally
identical `catch (Exception ex) when (_catchExceptions)` block, uses `BenzeneFailure.IsInfrastructure(ex)`
to pick a different *log message* ("this service is mis-wired; the message is not at fault"), and then
**never rethrows** - the exception is swallowed exactly the way `#228` fixed for SNS/S3/EventBridge.
This base class backs **every** Azure Functions batch trigger that doesn't override
`CatchExceptions`'s default containment: `ServiceBusBatchApplication`, `EventHubApplication` (via
`Benzene.Azure.Function.EventHub`), `KafkaApplication` (`Benzene.Azure.Function.Kafka`),
`QueueStorageApplication`, and `EventGridBatchApplication`. `Benzene.Azure.Function.Timer`'s
`TimerTickApplication.HandleAsync` (`src/Benzene.Azure.Function.Timer/TimerApplication.cs:110-117`)
has the same shape independently (it doesn't derive from the shared base) and the same gap.

**Concrete impact:** for QueueStorage/EventHub/Kafka(Azure)/Timer, the Functions host checkpoints/
deletes/dequeues on a *successful* invocation - and with `CatchExceptions=true` an infra failure never
throws past `ProcessItemAsync`, so the invocation *is* reported successful. Every message processed
while the service is mis-wired (a missing DI registration, e.g. after a bad deploy) is silently
dropped - genuine data loss, with only a log line (easy to miss under normal per-message failure noise)
as the trail. For `ServiceBusBatchApplication` under `AckMode = Explicit`, `OnExceptionCaughtAsync`
does abandon the not-yet-acked message on any exception (including this one), so the message is at
least redelivered rather than lost - but the invocation still never fails, so the operator gets no
"failed invocation" signal and the same message loops (abandon -> redeliver -> abandon...) forever
with the service silently reporting itself healthy the whole time, which is the exact operational
failure mode `#228`'s fix (and its log wording) was written to prevent.

**Verified** two ways, both passing (i.e. reproducing the bug) against `28473b0`:
- A standalone probe project referencing `Benzene.Azure.Function.Timer` directly:
  `TimerTickApplication.HandleAsync` with `TimerOptions { CatchExceptions = true }` and a pipeline
  mock that throws `BenzeneResolutionException("Unable to resolve ISomeService")` - `HandleAsync`
  completes without throwing.
- The same shape against `Benzene.Azure.Function.EventGrid.EventGridBatchApplication` (representative
  of every `AzureFunctionBatchApplicationBase` consumer, since none of the others override the base's
  catch behavior) - `EventGridOptions { CatchExceptions = true }`, pipeline mock throws
  `BenzeneResolutionException`, `HandleAsync` completes without throwing.
- Also independently reproduced by temporarily adding
  `HandleAsync_CatchExceptionsTrue_InfrastructureFailure_ProbeForMissingRethrowCarveOut` to the real
  `test/Benzene.Core.Test/Azure/TimerFailureHandlingTest.cs`, run once (green/"the bug reproduces"),
  then reverted (`git checkout --`).

**Suggested fix shape:** mirror `SingleContextEscalatingApplicationBase.ProcessAsync` exactly - compute
`isInfrastructure` once, keep the existing differentiated log line, then `if (isInfrastructure) throw;`
after logging, in `AzureFunctionBatchApplicationBase.ProcessItemAsync` and independently in
`TimerTickApplication.HandleAsync`. For `ServiceBusBatchApplication`'s `AckMode = Explicit` path this
composes cleanly with the existing `OnExceptionCaughtAsync` abandon-on-exception hook (which already
runs before the log/rethrow), so the message is still abandoned *and* the invocation now fails loudly.

### 2. The same catch-all also silently absorbs a genuine ambient-cancellation `OperationCanceledException`, inconsistent with `#230`'s own queued-item behavior

This is the concrete form of the review brief's "does `#230`'s cancellation-awareness and `#231`'s
escalation combine correctly" question - the answer is: not quite, and the inconsistency sits in the
same catch block as Finding 1, not in `BoundedFanOut` itself. This affects the batch-trigger family
(`AzureFunctionBatchApplicationBase`'s consumers), not `TimerApplication` itself - a single timer tick
never calls `BoundedFanOut`, so the literal "Timer-triggered batch" scenario in the brief doesn't exist
as a first-class code path; the underlying defect class does, in every sibling batch trigger.

`#230`'s fix deliberately made a batch item **still queued** behind `BoundedFanOut`'s
`MaxDegreeOfParallelism` semaphore observe cancellation and throw `OperationCanceledException` that
"surfaces...here only once every already-started item has settled" - i.e. that cancellation always
propagates and fails the whole batch invocation, *regardless of `CatchExceptions`*, because the
semaphore wait sits outside the per-item `body` (`ProcessItemAsync`) that `CatchExceptions` guards.
That is the correct, documented "drain-abort" behavior.

But an item that has **already started** (past the semaphore) and observes the *same* ambient
cancellation token mid-pipeline - e.g. an `HttpClient` call seeded with the scope's cancellation token
via `scope.SeedCancellationToken(cancellationToken)` throwing `OperationCanceledException` when the
host cancels the invocation - throws from *inside* `ProcessItemAsync`'s `try`, and is caught by the
same unqualified `catch (Exception ex) when (_catchExceptions)` as any ordinary business exception.
There is no `ex is not OperationCanceledException` (or `ex.CancellationToken.IsCancellationRequested`)
exclusion, unlike the established convention elsewhere in this codebase for exactly this distinction:
`Benzene.Resilience.RetryMiddleware.DefaultShouldRetry`, `Benzene.MapReduce.ScatterGatherExtensions`
(`ex is not OperationCanceledException`, with its own comment explaining why), `JaegerTraceSource`/
`TempoTraceSource`'s per-service fan-out isolation, and `Benzene.Resilience.Polly.CancellationSafePredicateBuilderExtensions`.

**Concrete impact:** under `CatchExceptions=true`, a batch item that is mid-flight when the Functions
host asks the invocation to stop (graceful shutdown, consumption-plan timeout) is logged as an ordinary
per-message failure and the batch is reported as having completed - not as cancelled/aborted - while a
sibling item still *queued* at that exact same moment correctly aborts the whole invocation per `#230`.
Two items affected by the identical host-level cancellation event are treated with opposite severity
purely based on scheduling luck (had it been dequeued from the semaphore yet). This is a real
consistency gap in the "combined cancellation+escalation story" the review brief asked about.

**Verified** with the same standalone `EventGridBatchApplication` probe harness: `EventGridOptions
{ CatchExceptions = true }`, a `CancellationTokenSource` cancelled and then a pipeline mock that throws
`new OperationCanceledException(cts.Token)` (i.e. genuinely tied to the token passed as this call's
`cancellationToken`, not a bare/unrelated one) - `HandleAsync` completes without throwing.

**Suggested fix shape:** either exclude `OperationCanceledException` from `CatchExceptions`'s
containment entirely in `AzureFunctionBatchApplicationBase.ProcessItemAsync` (matching
`ScatterGatherExtensions`'s `ex is not OperationCanceledException` convention), or - more precisely,
matching `MessageHandler.cs`'s existing pattern elsewhere in this codebase - only let through an
`OperationCanceledException` whose `CancellationToken` actually matches the ambient one, so an
application-level cancellation the pipeline itself legitimately produces (e.g. a deliberate
business-level "abandon this one message" signal unrelated to host shutdown, if any transport ever
grows one) isn't accidentally over-escalated.

### 3. Source generator: `BenzeneCosmosDbTrigger`'s `DatabaseName`/`ContainerName` are never validated, unlike every sibling transport's destination field

`#39` (round "WP-C") gave every non-HTTP transport reader in
`Benzene.Azure.Function.SourceGenerators.Transports.MessagingTransports` a required-field check for
the one attribute value without which the binding is meaningless: `ServiceBusTriggerMissingDestination`
(`BENZ0003`, queue-or-topic), `EventHubTriggerMissingEventHubName` (`BENZ0004`),
`KafkaTriggerMissingTopic` (`BENZ0005`), `QueueStorageTriggerMissingQueueName` (`BENZ0006`),
`BlobStorageTriggerMissingPath` (`BENZ0007`) - each reports a build-time error and emits nothing rather
than a broken `XxxTrigger("", ...)` binding. The round's own test-file comment
(`test/Benzene.Core.Test/Autogen/AzureFunctions/AzureFunctionTriggerGeneratorTest.cs:257`) frames the
whole change as: *"only CosmosDb (BENZ0002) validated its required field; extended to the other five
transports with a required binding value."*

That framing is the gap: `BENZ0002` only validates `DocumentType` (`CosmosDb.Read`,
`src/Benzene.Azure.Function.SourceGenerators/Transports/MessagingTransports.cs:299-356`).
`DatabaseName` and `ContainerName` - Cosmos DB's own binding-destination fields, exactly analogous to
`EventHubName`/`Topic`/`QueueName`/`Path` on the four siblings that got `#39`'s treatment - are read
with an empty-string default and never checked. A `[assembly: BenzeneCosmosDbTrigger(Name = "c",
DocumentType = typeof(OrderDoc))]` with no `DatabaseName`/`ContainerName` set compiles cleanly (zero
diagnostics) and emits:

```csharp
[global::Microsoft.Azure.Functions.Worker.CosmosDBTrigger(databaseName: "", containerName: "", Connection = "CosmosDbConnection", LeaseContainerName = "leases")]
```

- silently shipping a change-feed trigger bound to an empty database/container name, which fails only
at Azure Functions host startup/deployment (a `System.ArgumentException`-class runtime error, far from
the point of the mistake), instead of failing the build with a clear message the way the identical
class of mistake does for the five sibling transports.

**Verified** with a standalone scratch test project referencing
`Benzene.Azure.Function.SourceGenerators` directly and driving `AzureFunctionTriggerGenerator` via
`CSharpGeneratorDriver` (the same harness the real generator test file uses): generating
`[assembly: BenzeneCosmosDbTrigger(Name = "c", DocumentType = typeof(App.OrderDoc))]` (no
`DatabaseName`/`ContainerName`) produces zero diagnostics and output containing both
`databaseName: ""` and `containerName: ""` literally. Also independently reproduced by temporarily
adding `CosmosDb_MissingDatabaseNameAndContainerName_ProbeForMissingValidation` to the real
`test/Benzene.Core.Test/Autogen/AzureFunctions/AzureFunctionTriggerGeneratorTest.cs`, run once
(green/"the bug reproduces"), then reverted.

**Suggested fix shape:** a new `CosmosDbTriggerMissingDestination` diagnostic (`BENZ0010`), reported
when `database.Length == 0 || container.Length == 0`, following the exact shape of
`ServiceBusTriggerMissingDestination` - checked alongside (not instead of) the existing
`DocumentType` check, before the binding string is built.

---

## Areas checked with no new finding

- **Blob Storage trigger's lack of `CatchExceptions`/`RaiseOnFailureStatus`/escalation** (it uses the
  bare `MiddlewareApplication<TEvent, TContext>` with no options at all, and `BlobStorageContext`
  doesn't even implement `IHasMessageResult`) looked suspicious at first (compare `#231`'s framing:
  "unlike every sibling Azure Function trigger") but is deliberate, documented architecture, not a gap:
  `DependencyInjectionExtensions.cs`'s own doc comment states "There is no `UseMessageHandlers()`-style
  routing on this transport - a blob is a file, not a message envelope" - the same reasoning `#227`'s
  scope correction already applied to excluding Kinesis from the AWS-side topic-routing fix. Nothing to
  escalate a failure *result* from, since there's no result-bearing context to begin with; an
  unhandled exception in `.UseBlob(...)` cascades unconditionally already (equivalent to every sibling's
  default `CatchExceptions=false`).
- **Cosmos DB change-feed worker** (`BenzeneCosmosChangeFeedWorker`/`BenzeneCosmosAllVersionsChangeFeedWorker`)
  cancellation handling: both already correctly scope their `catch (OperationCanceledException) when
  (cancellationToken.IsCancellationRequested)` to the caller's own token (the pattern Finding 2 above
  says is missing from the Functions-trigger family) - no regression found here.
- **Event Hub self-hosted worker** (`BenzeneEventHubWorker`) checkpoint/cancellation path: consistent
  with `#108`/`#115-117`'s fixes; no new race found under a re-read.
- **Azure mesh discovery/deployment** (`Benzene.Mesh.Discovery.Azure`, `Benzene.Mesh.Azure.Blob`): read
  end to end; nothing beyond style found.

## Interaction between `#230` and `#231` specifically (the review brief's opening question)

`TimerApplication`/`TimerTickApplication` never call `BoundedFanOut` - a timer tick is a single
context, not a batch - so the literal scenario ("a Timer-triggered batch using `BoundedFanOut`
internally") doesn't exist in this codebase; `#230` and `#231` don't directly compose with each other
in the same code path. The substantive version of the brief's question - does the batch-trigger
family's cancellation-awareness (`#230`) combine correctly with its escalation/`CatchExceptions`
contract (the same contract `#231` gave Timer) - is Finding 2 above, and the answer is no, for the
reason described there.
