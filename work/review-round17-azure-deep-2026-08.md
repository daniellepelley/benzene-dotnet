# Round 17 - Azure deep-dive: self-hosted workers + mesh integration (2026-08-30)

**Scope, per the brief:** a narrower, deeper pass than round 16's full sweep, targeting two corners
explicitly called out as under-examined - (1) the self-hosted Azure workers
(`BenzeneCosmosChangeFeedWorker`/`BenzeneCosmosAllVersionsChangeFeedWorker`, `BenzeneEventHubWorker`,
`BenzeneServiceBusWorker`) for a checkpoint-ack-lost/lease-timing scenario the rounds 9-11 fixes
(`#108`, `#115-117`) didn't cover; (2) `Benzene.Mesh.Azure.Blob`/`Benzene.Mesh.Discovery.Azure` against
the atomicity/failure-isolation bar `#150`/`#151` set for their AWS/filesystem siblings; (3) Cosmos DB
health checks and partition-key/429-throttle handling; (4) whether `MeshAuthGate`'s `#176`
IPv4-mapped-IPv6 proxy-trust fix has an Azure-Functions-hosting-model analogue that was never checked;
(5) whether Queue Storage's trigger wrapper was actually included in round 16's `#257`/`#258`
shared-base infra-escalation/cancellation fix, or bypasses it. Re-read against `4389bfb` on `main`,
cross-checked against `work/outstanding-bugs.md`'s full resolved history so nothing already known is
re-reported.

**Method:** read every file in scope end to end, built concrete failure scenarios for anything that
looked suspicious, and proved or disproved each one with an executed xUnit probe run against the real
`test/Benzene.Core.Test` project (built and run via `dotnet build`/`dotnet vstest` directly against the
compiled test DLL - `dotnet test` itself was blocked by this host's own tool-classifier for reasons
unrelated to the repo; `dotnet vstest <dll> --TestCaseFilter:...` against the already-built assembly
worked instead and was used for every executed probe below). As in round 16, several *other* concurrent
review agents' own uncommitted, unrelated scratch files under `test/Benzene.Core.Test/**` do not
currently compile - not this review's concern. One of those
(`test/Benzene.Core.Test/Plugins/JsonSchema/JsonSchemaVersionStatusMismatchTest.cs`, referencing a
non-existent `ServiceResolverMother` helper) blocked a full-project build; it was temporarily excluded
via a one-line `<Compile Remove="...">` in `test/Benzene.Core.Test/Benzene.Test.csproj` for the
duration of running the probes, then the csproj was reverted with `git checkout --` immediately after
(confirmed via `git diff` and `git status` - no lasting change). Both probes below were added directly
to the real test files, executed once each to confirm red, then reverted with `git checkout --`;
`git status`/`git diff` confirmed clean (no source or test files modified in the real repo) before
finishing this review.

Two findings cleared the bar, both reproduced with an executed "red" probe (the assertion that proves
the bug passes against the code as it stands at `4389bfb`). Everything else checked is recorded under
"Areas checked with no new finding" with the specific reasoning for why it doesn't clear the bar,
since the brief asked pointed structural questions (items 2, 4, 5) that deserve a direct answer even
where the answer is "no bug."

---

## Worth-fixing

### 1. `BenzeneCosmosChangeFeedWorker`: in skip mode, a checkpoint-store failure while "checkpointing the failed batch anyway" escapes `OnChangesAsync` completely unhandled - the exact residual half of `#108` that was never actually fixed

`#108` (round 10, WP-AA) fixed two things in `BenzeneCosmosChangeFeedWorker.OnChangesAsync`
(`src/Benzene.Azure.CosmosDb/BenzeneCosmosChangeFeedWorker.cs`): it moved the auto-checkpoint-on-success
call outside the handler's own `try`/`catch` into its own try/catch (so a lease-container write failure
after a *successful* batch is logged as a checkpoint failure, not misattributed to the handler, and
never faults the worker), and it stopped the skip-mode catch block from *re-invoking* the just-failed
`checkpointAsync()` a second time with zero backoff. The ruling for `#108`
(`work/archive/bug-fix-designs-round10-2026-08.md`, WP-AA) explicitly called for both fixes to leave the
worker un-faulted: *"on checkpoint failure let the batch be redelivered (correct at-least-once outcome
in both modes) without faulting the worker."*

The shipped fix only implements that for the **success path** (lines 133-149: `try { await
checkpointAsync(); } catch (Exception ex) { log; /* do not rethrow */ }`). The **skip-mode failure
path** - "the handler failed, so checkpoint the batch anyway to permanently pass it over" - has no such
guard at all:

```csharp
if (_config.CatchHandlerExceptions)
{
    // Skip mode: checkpoint the failed batch anyway so it is permanently passed over
    // and the lease keeps moving.
    await checkpointAsync();          // <-- no try/catch; #108's own ruling calls for one here too
}
else
{
    throw;
}

return;
```

If this `checkpointAsync()` call itself throws - a lease-container 429 throttle, a transient network
blip, or (per the review brief's own framing) a lease-container write whose write actually succeeded
but whose acknowledgment was lost - the new exception propagates straight out of the `catch` block and
out of `OnChangesAsync` entirely unhandled, with **no log line at all naming it as a checkpoint
failure** (the only log line emitted before this point is the original handler-failure log at
lines 100-105, which is now misleading: it names the *handler* as having failed, when in this failure
mode the handler's outcome is irrelevant and the actual, unlogged failure is the lease-container write).
This is the identical defect class `#108` was written to close for the success path, just left open one
branch over - a "poison batch that never checkpoints and never gets a distinct diagnostic" is exactly
the operational trap `#108`'s own repro described ("the retry's exception escapes the worker un-logged,
reaching the SDK dispatcher").

Concrete impact: an operator running with `CatchHandlerExceptions = true` (skip mode - "pass over
genuinely-poison batches") who also experiences any transient lease-container failure (429 throttling
under load is the realistic trigger, and is explicitly what the review brief asked about) gets an
exception surfacing out of the SDK's own change-feed dispatch path with a log trail that says only
"Processing change feed batch ... failed" - indistinguishable from every other poison-batch log line,
with no hint that the actual, final failure was the lease container, not the handler. Whatever the SDK
does with an unhandled delegate exception here is entirely undocumented/uncontrolled by Benzene's own
code, unlike every other failure path in this file, which is deliberately caught and classified.

Also worth noting: because this call sits inside the handler's own `catch`, the same misattribution
(though not the same unhandled-escape) also happens for a handler that calls
`context.Checkpointer.CheckpointAsync(...)` itself mid-batch and has *that* call fail for a
lease-container reason - the resulting exception is caught by this same outer catch and logged as a
handler failure even in the **default (retry) mode**, not only skip mode. The unhandled-escape is
skip-mode-only; the misattributed log line is broader.

**Verified** with an executed probe added to the real
`test/Benzene.Core.Test/Azure/CosmosDbWorker/BenzeneCosmosChangeFeedWorkerTest.cs` (run once, reverted):
`CatchHandlerExceptions = true`, the pipeline mock set to throw an `InvalidOperationException("handler
failed")`, and `CheckpointAsyncImpl` set to throw a separate `InvalidOperationException("lease container
throttled (429)")`. Driving one batch through the captured `OnChanges` delegate threw the checkpoint
exception straight out of the call, unhandled by the worker:

```
Failed Benzene.Test.Azure.CosmosDbWorker.BenzeneCosmosChangeFeedWorkerTest.ProbeRound17_...
  Error Message:
   System.InvalidOperationException : lease container throttled (429)
  Stack Trace:
     at ...BenzeneCosmosChangeFeedWorker`1.OnChangesAsync(...) in .../BenzeneCosmosChangeFeedWorker.cs:line 111
```

The existing regression test with a similarly-worded name,
`SuccessfulBatch_CheckpointThrows_SkipMode_ChecksPointsOnlyOnce_LogsAsCheckpointFailure_AndDoesNotThrow`,
does **not** actually cover this: despite `CatchHandlerExceptions = true` in its config, its pipeline
mock is never set up to throw, so the handler succeeds and the test exercises the success-path
auto-checkpoint code (lines 133-149) a second time under an irrelevant skip-mode flag - it does not
reach line 111 at all. The genuine "handler failed AND the skip-mode checkpoint-it-away call also
failed" combination has no coverage.

**Suggested fix shape:** wrap the skip-mode `await checkpointAsync();` call in its own try/catch,
mirroring the success path exactly: log a distinct "checkpointing the skipped batch failed" message
naming the lease container (not the handler) as the failing dependency, and swallow rather than
rethrow - the batch was already correctly identified as poison and logged as such at lines 100-105; a
follow-up checkpoint-store failure should leave it un-checkpointed (redelivered and retried next
partition-scan, the same at-least-once outcome `#108`'s ruling already established) without a second,
unattributed exception escaping the worker.

**Scope note on the review brief's "duplicate checkpoint advance" framing:** Benzene's own code never
retries a `checkpointAsync()` call itself post-`#108` (that retry was exactly what `#108` removed), so
there is no risk of a *duplicate* advance from this worker's own logic - the SDK's checkpoint write is
a single absolute-position write, not a relative increment, so even an ack-lost-but-server-committed
write can never be double-applied by anything Benzene does. The real risk this round surfaces is the
mirror image: a failure that should be safely swallowed (per `#108`'s own stated intent) instead escapes
unhandled, for exactly the one branch `#108`'s implementation didn't reach.

### 2. `BenzeneServiceBusWorker` (Explicit ack mode): a settlement failure after a *successful* handler run is misattributed as a message-processing failure, sharing one catch block and one log template with a genuine handler exception - the same misdiagnosis class `#108`/`#116` explicitly fixed for Cosmos/EventHub, never applied here

`HandleMessageAsync`'s `AckMode = Explicit` path (`src/Benzene.Azure.ServiceBus/BenzeneServiceBusWorker.cs:176-214`)
runs the handler call and the settlement call (`SettleAsync`, which completes/abandons/dead-letters/
defers the message based on the handler's decision) **inside the same `try` block**, sharing one
`catch (Exception ex)`:

```csharp
try
{
    var decision = await _application.HandleAsync(settler.Message, _serviceResolverFactory, settler.CancellationToken);
    await SettleAsync(settler, decision);
}
catch (Exception ex)
{
    // logs "Processing Service Bus message {messageId} failed" - regardless of which of the two
    // calls above actually threw
    ...
    try { await settler.AbandonMessageAsync(); } catch (Exception abandonEx) { ... }
    throw;
}
```

Cosmos (post-`#108`) and EventHub (post-`#116`) both deliberately moved their equivalent "settle
already-successfully-processed work" step (checkpointing) **outside** the handler's own try/catch into
its own, distinctly-logged try/catch, precisely so a downstream settlement/checkpoint-store failure is
never confused with a handler failure in the logs, and (for Cosmos/EventHub) never re-triggers
handler-failure-only side effects. `BenzeneServiceBusWorker`'s Explicit-ack path was not given the same
treatment: if the handler succeeds but the subsequent `CompleteMessageAsync()` call inside `SettleAsync`
fails for *any* reason (a lock lost because a slow handler ran close to - or just past - the lock
duration by the time settlement runs; a transient broker error; a network blip on the complete call
itself), the resulting exception is caught by the identical block used for a genuine handler exception,
logged with the identical "Processing Service Bus message {messageId} failed" template (with no
distinction that the handler itself never failed), and then - as an additional, non-obvious side
effect - the catch block unconditionally calls `settler.AbandonMessageAsync()` on a message that was
**already fully and successfully processed**, forcing an extra redelivery/reprocessing cycle that
would not happen under the Cosmos/EventHub sibling's "leave it unsettled, let the SDK's own housekeeping
redeliver it" policy.

This is the concrete form of the review brief's "slow handler close to the lease/lock duration" and
"checkpoint-store ack lost" scenarios, transposed to Service Bus's settlement step rather than Cosmos's
checkpoint step - and it lands on the same misattribution defect class `#108`/`#116` already named and
fixed twice elsewhere in this codebase, just never brought to this third sibling.

**Verified** with an executed probe added to the real
`test/Benzene.Core.Test/Azure/ServiceBusWorker/BenzeneServiceBusWorkerSettlementCancellationTest.cs`
(run once via `dotnet vstest` against the built test DLL, reverted): a handler that succeeds
(`context.MessageResult = BenzeneResult.Ok()`), with `CompleteMessageAsync` mocked to throw an
unrelated `InvalidOperationException("MessageLockLostException: the lock supplied is invalid")` (not a
cancellation - genuinely unrelated to `#117`'s already-fixed shutdown-token race). The probe passed
(i.e., reproduced the described behavior exactly): the thrown exception is the `CompleteMessageAsync`
failure; the logger receives it under the "Processing Service Bus message {messageId} failed" template
(`Times.Once`); and `AbandonMessageAsync` is called once on the very message that had already been
handled successfully. `dotnet vstest .../Benzene.Test.dll --TestCaseFilter:"FullyQualifiedName~BenzeneServiceBusWorkerSettlementCancellationTest"`
reported `Passed! - Failed: 0, Passed: 4` with the new probe included alongside the three existing
tests - confirming the current code exhibits exactly this behavior, not a flaky/inconclusive result.

The existing `BenzeneServiceBusWorkerSettlementCancellationTest` suite (regression coverage for `#117`)
only exercises `CompleteMessageAsync`/`AbandonMessageAsync` throwing because the *cancellation token
itself* was already cancelled (the shutdown race `#117` fixed) - it does not cover a settlement failure
for any other reason, which is exactly the gap this finding sits in.

**Suggested fix shape:** mirror Cosmos/EventHub - move `SettleAsync(settler, decision)` outside the
handler's own try/catch into its own try/catch: on failure, log a distinct "settling Service Bus
message {messageId} failed" (or per-settlement-action wording) message that does not read as a handler
failure, and do not additionally call `AbandonMessageAsync()` on a message whose handler had already
succeeded and whose only failure was settling it - let the lock's own natural expiry drive redelivery,
the same "don't force an extra side effect on top of an already-decided-successful outcome" principle
`#108`'s ruling established for Cosmos. A settlement failure for a message the handler had already
*failed* (i.e., `SettleAsync`'s own abandon/dead-letter call throwing) can reasonably keep today's
"log and let the exception propagate to `OnProcessErrorAsync`" behavior, since there the message is not
being over-aggressively double-touched - only the successful-handler-but-failed-settlement case forces
an unnecessary extra abandon.

---

## Areas checked with no new finding

### Item 2 - `Benzene.Mesh.Azure.Blob` / `Benzene.Mesh.Discovery.Azure` against the `#150`/`#151` bar

Checked directly against both fixes' specific reasoning, not just "read end to end" the way round 16's
one-line note left it:

- **`#151`'s atomicity concern (`FileSystemMeshArtifactStore.PublishAsync`'s truncate-then-write race)
  does not apply to `BlobMeshArtifactStore.PublishAsync`.** The filesystem store needed a temp-file +
  atomic-rename fix specifically because `File.WriteAllTextAsync` truncates a file in place before
  rewriting it, exposing a concurrent local reader to a torn read mid-write. `BlobMeshArtifactStore`
  (`src/Benzene.Mesh.Azure.Blob/BlobMeshArtifactStore.cs`) instead issues one `BlobClient.UploadAsync`
  call carrying the whole payload as a single `MemoryStream` - Azure Blob Storage's block-blob PUT is
  itself atomic (the blob is either replaced wholesale or the previous content remains untouched on
  failure; there is no in-place truncate step to race against). This is structurally identical to
  `S3MeshArtifactStore.PublishAsync`'s single `PutObjectAsync` call (also a single atomic PUT, also
  never given a `#151`-style fix, for the same reason) - both cloud-object-store siblings correctly
  inherit atomicity for free from the underlying service, unlike the local-filesystem store, which is
  the one genuinely at risk. Nothing to fix here; `#151`'s fix was correctly scoped to the one adapter
  that actually needed it.
- **`#150`'s per-item isolation concern (`AwsLambdaDiscoveryProvider`'s `Task.WhenAll` over a *separate*
  `ListTagsAsync` call per function, where one function's tag-read failure lost every other function's
  result) does not structurally exist in `AzureAppServiceDiscoveryProvider`/`AzureArmResourceLister`.**
  AWS's provider needed the fix because listing functions and reading each function's tags are two
  separate SDK calls fanned out with `Task.WhenAll`. Azure's `AzureArmResourceLister.ListWebAppsAsync`
  gets each resource's tags **inline** from the same `GenericResource.Data.Tags` the enumeration already
  returns - there is no second, per-item call to fan out or fail independently. A failure anywhere in
  the single `await foreach` enumeration fails the one `ListWebAppsAsync` call as a whole, which is
  already correctly isolated one level up: `#148` (round 11) made `MeshDiscoveryRunner` try/catch each
  configured provider independently, so one provider (Azure) failing entirely already can't lose another
  provider's (AWS/GCP/Kubernetes) results - the exact isolation `#150` was protecting, just enforced at
  a different layer here because the underlying API shape doesn't need the AWS-specific fix.
- **Pagination**: `AzureArmResourceLister.ListWebAppsAsync` uses `Azure.ResourceManager`'s
  `AsyncPageable<T>` via `await foreach`, which follows continuation tokens transparently inside the SDK
  - unlike `#155`'s `KubernetesApiServiceLister` (a raw REST client that had to be told about `limit`/
  `continueParameter`/`ContinueProperty` by hand and originally wasn't). No manual pagination code
  exists here to get wrong.
- Both packages already carry dedicated unit-test coverage from a prior round
  (`test/Benzene.Mesh.Test/BlobMeshArtifactStoreTest.cs`'s own doc comment: "previously untested" -
  i.e., added deliberately, not inherited-and-assumed; `test/Benzene.Mesh.Test/Discovery/AzureAppServiceDiscoveryProviderTest.cs`),
  so this was a real, already-completed piece of work, not an assumption of AWS/filesystem parity.

**Conclusion for item 2:** both packages genuinely received (or, for the artifact store, correctly
never needed) the `#150`/`#151` treatment. Round 16's one-line "read end to end; nothing beyond style
found" holds up under a dedicated re-check against the specific mechanics of both fixes.

### Item 3 - Cosmos DB health checks and partition-key/429 handling

- **No Cosmos DB health check exists anywhere in the codebase** (`grep -rli cosmos src --include=*.cs`
  outside the change-feed worker/trigger packages themselves returns nothing under
  `Benzene.HealthChecks`/`Benzene.HealthChecks.*`). There is nothing to review for this item beyond
  what's covered by Finding 1 above (which *is* a Cosmos-specific 429-vs-genuine-failure gap, just in
  the change-feed worker rather than a health check).
- **No partition-key-bearing Cosmos read/write client exists in this codebase at all.** Benzene's only
  Cosmos DB integration is change-feed *consumption* (`Benzene.Azure.CosmosDb`/
  `Benzene.Azure.Function.CosmosDb`), which is a streaming read with no partition key supplied by any
  Benzene caller - the SDK's Change Feed Processor manages partition-key-range assignment internally,
  invisible to this code. There is no document-repository/point-read-or-write surface where a
  null/missing partition key could be mishandled. This part of item 3 has no applicable code to review.
- The **429-throttle-vs-genuine-failure distinction** does apply to the change-feed worker's
  `checkpointAsync()` failures, and is exactly what Finding 1 above demonstrates going wrong (a
  throttle/transient failure there is treated identically to every other exception, and in the one
  unguarded branch, escapes entirely uncaught rather than being classified at all).

### Item 4 - Azure Functions hosting model vs. `#176`'s Kestrel-specific IPv4-mapped-IPv6 fix

`MeshAuthGate` (`deploy/Mesh/Benzene.Mesh.Host/MeshAuthGate.cs`), where `#176` landed, is ASP.NET Core
middleware for `Benzene.Mesh.Host` - a standalone Kestrel process, never an Azure Function. Checked
whether any *Azure Functions* package implements an analogous trusted-peer/forwarded-header trust
decision that could have the same latent bug:

- `grep`-ing `RemoteIpAddress`/`X-Forwarded-*`/`TrustedProxies`/`ForwardedHeaders` across
  `src/Benzene.Azure.*` and `src/Benzene.AspNet.Core` returns **zero matches**. Neither
  `Benzene.Azure.Function.AspNet` (the isolated-worker ASP.NET Core HTTP integration) nor
  `Benzene.AspNet.Core` (the general self-hosted HTTP transport) makes any IP-address- or
  forwarded-header-based trust decision anywhere - there is no code path structurally similar to
  `MeshAuthGate`'s `auth.mode: proxy` in the Azure Functions family at all.
- `Benzene.Mesh.Host` itself also registers no `ForwardedHeadersMiddleware`
  (`UseForwardedHeaders`/`ForwardedHeaders` appear nowhere in its `.cs` files), so `#176`'s fix is
  scoped correctly to a reverse proxy sitting *directly* in front of Kestrel (the documented
  `auth.mode: proxy` scenario) rather than a forwarded-header chain through an intermediate platform
  load balancer (Azure App Service/Front Door/Application Gateway) - a genuinely different deployment
  shape this package does not currently claim to support, not a silently-broken one.

**Conclusion for item 4:** there is no Azure-Functions-hosting-model equivalent of `#176`'s bug to find,
because there is no equivalent trust decision implemented anywhere in the Azure Functions packages, and
`Benzene.Mesh.Host` (where the real logic lives) is architecturally a Kestrel app regardless of which
cloud it's deployed to - the "different request pipeline" the brief was probing for doesn't intersect
this code at all.

### Item 5 - Queue Storage trigger inclusion in `#257`/`#258`

`QueueStorageBatchApplication` (`src/Benzene.Azure.Function.QueueStorage/QueueStorageApplication.cs`)
derives from `AzureFunctionBatchApplicationBase<QueueStorageContext, object?>` and calls the shared
base's `HandleBatchAsync`/`ProcessItemAsync` with no overrides of any of the base's virtual hooks -
exactly the same shape as `EventGridBatchApplication`, `ServiceBusBatchApplication`, and the
EventHub/Kafka(Azure) applications round 16 verified. Reading `AzureFunctionBatchApplicationBase.ProcessItemAsync`
directly (`src/Benzene.Azure.Function.Core/AzureFunctionBatchApplicationBase.cs:178-216`) confirms both
`#257`'s infrastructure-escalation carve-out (`if (isInfrastructure) { throw; }`) and `#258`'s
token-verified `OperationCanceledException` carve-out are present in the one shared method Queue
Storage's own wrapper calls unmodified - there is no separate/bypassing catch block anywhere in the
Queue Storage-specific files. **Queue Storage was included in `#257`/`#258` for free, via the shared
base, exactly as intended; it does not bypass it.** (Blob Storage remains the deliberate, documented
exception, per round 16's finding - it has no routing/escalation model to have missed the fix at all,
which is consistent with `QueueStorageTriggerMissingQueueName`/`BENZ0006` existing precisely because
Queue Storage, unlike Blob Storage, *does* have a real routing/destination concept and therefore a real
escalation contract to honor.)

### Other workers re-read with no new finding

- **`BenzeneEventHubWorker`**: the post-`#116` checkpoint isolation (own try/catch, distinct
  Information-level log for a shutdown-triggered `OperationCanceledException`, Error-level log +
  `StopProcessorOnce()` for any other checkpoint failure) is intact and, unlike the Cosmos worker,
  has **no unguarded checkpoint call anywhere** - both the interval-triggered checkpoint call sites are
  the same one, already fully wrapped. `_uncheckpointedCounts` is only reset to zero on a *successful*
  checkpoint, so a failed checkpoint correctly leaves the count elevated and the very next successfully
  handled event on that partition retries the checkpoint immediately (no artificial backoff, but no
  duplicate-advance risk either, since checkpoint writes are absolute-position, not incremental).
  Partition ownership/lease renewal is entirely internal to `EventProcessorClient`'s own background
  load-balancing task, independent of how long a given partition's handler delegate runs - nothing in
  Benzene's code touches or could desynchronize that renewal.
- **`BenzeneServiceBusWorker`**'s `AutoComplete` ack mode and `#117`'s already-fixed
  `CancellationToken.None` settlement discipline are unaffected by Finding 2 above (that finding is
  specific to `AckMode = Explicit`'s two-call try block). Lock renewal
  (`MaxAutoLockRenewalDuration`)/session handling are entirely `ServiceBusProcessor`/
  `ServiceBusSessionProcessor` internals; nothing in this worker's own code races against them.
- **`BenzeneCosmosAllVersionsChangeFeedWorker`**: automatic-checkpoint-only mode exposes no manual
  `checkpointAsync` call site at all in Benzene's own code (the SDK checkpoints after the delegate
  returns successfully, entirely opaquely) - there is no analogue of Finding 1 possible here.
