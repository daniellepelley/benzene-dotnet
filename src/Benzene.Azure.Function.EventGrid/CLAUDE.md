# Benzene.Azure.Function.EventGrid

## What this package does
Inbound Azure Event Grid adapter for the Azure Functions `EventGridTrigger` binding (isolated
worker): routes delivered events to message handlers **by event type** — the direct Azure
counterpart of `Benzene.Aws.Lambda.S3`/`EventBridge` routing on the event name. Handles both wire
schemas Event Grid can deliver: the Event Grid schema (`eventType`/`topic`) and CloudEvents 1.0
(`type`/`source`, detected by `specversion`).

## Failure handling: a returned failure result is retried by default (safe-by-default)
`EventGridOptions.RaiseOnFailureStatus` defaults to `true` (flipped 2026-07-21 — see
`work/archive/settlement-contract-1.0-2026-07.md`): if a handler returns a non-exception failure result (e.g.
`BenzeneResult.ServiceUnavailable(...)`), it is escalated into a thrown
`EventGridMessageProcessingException` so the invocation fails and Event Grid's own delivery retry
(backoff, up to 24h) + optional dead-letter destination take over — the same treatment an unhandled
exception already got. Set `EventGridOptions.RaiseOnFailureStatus = false` (via
`UseEventGrid(action, configure)`) for at-most-once (a failure result reports success, no retry).
`EventGridOptions.CatchExceptions` (default `false`) conversely swallows/logs handler exceptions.
Because a returned failure is now retried by default, the handler must be idempotent.

**Null-outcome policy (flipped 2026-08-25):** an event whose `MessageResult` is never set - typically
an unrouted event, no handler matched the event type - is escalated the same as an explicit failure,
not accepted as success. Event Grid's own delivery retry + optional dead-letter destination gives an
escalated-and-retried event somewhere to land. Enforced via the
`AzureFunctionBatchApplicationBase.EscalateUnestablishedOutcome` hook (default `true`, not overridden
here - see `Benzene.Azure.Function.Core/CLAUDE.md`). See `work/settlement-consistency-fix-plan.md`.

**A malformed delivery is an ordinary per-event failure too (round 14-15, #235).** `EventGridTriggerEvent
.Parse` used to run eagerly - as a method argument in `Extensions.HandleEventGridEvent(string)` -
before dispatch even started, so a `JsonException` from malformed JSON was an unguarded throw that
bypassed `EventGridOptions.CatchExceptions` entirely (and the base class's own catch/escalate/log
machinery, since it was never reached). `EventGridContext` now has a **raw-JSON constructor** that
defers `Parse` to the first `Event` property access - which happens once that context's item reaches
the pipeline, inside `AzureFunctionBatchApplicationBase.ProcessItemAsync`'s own guarded try, not
before it. The result (success or failure) is cached, so a retried access after a failed parse throws
the *same* exception instance rather than reparsing or, worse, silently succeeding on a second
attempt. `HandleEventGridEvent(string)` now dispatches raw JSON to a **second entry point** -
`EventGridBatchApplication` implements `IMiddlewareApplication<string[]>` alongside its existing
`IMiddlewareApplication<EventGridTriggerEvent[]>`, and `UseEventGrid(...)` registers both over the
*same* `EventGridBatchApplication`/`EventGridOptions` instance (mirroring
`Benzene.Azure.Function.ServiceBus`'s two-request-shape pattern for `ServiceBusReceivedMessage[]` +
`ServiceBusTriggerBatch`) - so a malformed delivery is now caught/logged under `CatchExceptions = true`
or left to cascade (Event Grid's own retry/dead-letter engages) under the default `false`, exactly
like any other per-event failure, matching this transport's retain-on-failure settlement default
above. `GetLogId`/`CreateProcessingException` are defensive against `Event` itself throwing (falling
back to `null`/`"unknown"`) so logging a malformed-payload failure can't itself throw a second time
and defeat `CatchExceptions`.

## Zero dependencies — deliberately
References only `Benzene.Azure.Function.Core` + `Benzene.Core.MessageHandlers` — no
`Azure.Messaging.EventGrid`, no Functions extension package; the event payload rides as a BCL
`JsonElement`. The consumer's Function App project references
`Microsoft.Azure.Functions.Worker.Extensions.EventGrid` itself for the attribute, binds the event
as `string`, and calls `HandleEventGridEvent(json)` — `EventGridTriggerEvent.Parse` does the
schema detection/mapping. Do not add SDK packages here without asking first (repo NuGet policy).

## Routing
- **Topic = the event type** (`Microsoft.Storage.BlobCreated`, or your own custom type) via
  `EventGridMessageTopicGetter`, wrapped in `PresetTopicMessageTopicGetter` so
  `UsePresetTopic(...)` can override per pipeline as everywhere else.
- **Body = the event's `data` payload** as raw JSON (`{}` when absent, so empty request types
  bind), deserialized by the standard request mapper into the handler's request type.
- **Headers = the envelope**: `id`, `subject`, `source` (the Event Grid schema's `topic` /
  CloudEvents' `source` — named `Source` on the model to avoid colliding with Benzene's routing
  notion of topic).

```csharp
app.UseEventGrid(eventGrid => eventGrid.UseMessageHandlers());
// [Message("Microsoft.Storage.BlobCreated")] handlers receive the event's data payload
```

## Declared triggers (source-generated)
Instead of hand-writing the `[Function]`/`[…Trigger]` class, declare the trigger and let
Benzene's source generator (shipped in `Benzene.Azure.Function.Core`) emit it:
`[assembly: BenzeneEventGridTrigger(Name = "events")]`.
`BenzeneEventGridTriggerAttribute` (assembly-scoped, `AllowMultiple`) lives in this package; you own every
binding value. Still reference this transport's `Microsoft.Azure.Functions.Worker.Extensions.*`
package directly, and note `FunctionsEnableWorkerIndexing=false` (auto via Core's
buildTransitive). The hand-written form still works. See `docs/azure-functions.md`.

## Key types
- `EventGridTriggerEvent` — Benzene's own dependency-free model (`Id`, `EventType`, `Subject`,
  `Source`, `EventTime`, `DataVersion`, `Data` as `JsonElement?`) + `Parse(string)` covering both
  schemas.
- `EventGridContext : IHasMessageResult` — result is diagnostics-only; a thrown exception is what
  drives Event Grid's own retry/dead-letter machinery. Two constructors: `EventGridContext
  (EventGridTriggerEvent)` for an already-parsed event (`Event` never throws), and
  `EventGridContext(string rawJson)` for a not-yet-parsed raw delivery (`Event` parses - and caches
  the result or the failure - on first access; see "A malformed delivery is an ordinary per-event
  failure too" above).
- `EventGridApplication` — `EntryPointMiddlewareApplication<EventGridTriggerEvent[]>`, fan-out,
  transport tag `"event-grid"`; array shape covers batched ("many"-cardinality) triggers and tests.
  Kept for API-surface compatibility (like `Benzene.Azure.Function.ServiceBus.ServiceBusApplication`)
  but not what `UseEventGrid(...)` actually wires up any more - see `EventGridBatchApplication` below.
- `EventGridBatchApplication` — implements both `IMiddlewareApplication<EventGridTriggerEvent[]>`
  (already-parsed events) and `IMiddlewareApplication<string[]>` (raw JSON, round 14-15 #235) over one
  shared instance, registered as two entry points by `UseEventGrid(...)`.
- `UseEventGrid(action, Action<EventGridOptions> configure = null, string name = null)` (both
  builders, no-op off-Azure), `AddAzureEventGrid()`, `EventGridRegistrations`,
  `HandleEventGridEvents(params ...)`, `HandleEventGridEvent(string)`. Fan-out concurrency is bounded
  via `EventGridOptions.MaxDegreeOfParallelism` (the positional `maxDegreeOfParallelism` arg was
  folded into the options object, same change Kafka made), routed through `Benzene.Core.Middleware`'s
  `BoundedFanOut`; it only bites on a batched ("many"-cardinality) trigger - the default
  one-event-per-invocation delivery has nothing to bound.
- `EventGridOptions` (`CatchExceptions`, `RaiseOnFailureStatus`, `MaxDegreeOfParallelism`) +
  `EventGridMessageProcessingException` — the failure-handling knobs and the escalation exception
  (see "Failure handling: a returned failure result is retried by default" above).

## Tests
- `test/Benzene.Core.Test/Azure/EventGridPipelineTest.cs` — end-to-end routing for both schemas,
  `Parse` field mapping for both schemas, headers surface, empty-data body fallback, and (round 14-15
  #235) malformed JSON through the real trigger-dispatch path (`app.HandleEventGridEvent(json)`)
  under both `CatchExceptions` settings, a well-formed-JSON regression guard on the same raw-JSON
  dispatch path, and a focused `EventGridContext` unit test proving a failed parse is cached (the same
  exception instance on every subsequent `Event` access, not reparsed or silently different).
- `test/Benzene.Core.Test/Azure/EventGridFailureHandlingTest.cs` — `EventGridOptions`'
  `CatchExceptions`/`RaiseOnFailureStatus` combinations against `EventGridBatchApplication` directly
  (via the already-parsed-events overload, with a mocked pipeline).

## Claim-check hydration
Not wired here yet: `Benzene.ClaimCheck`'s hydrate middleware needs an
`IMessageBodySetter<EventGridContext>` registered — the same 5-line pattern as
`Benzene.Aws.Lambda.Sqs`/`.Sns`/`.EventBridge` ship (see `work/archive/claim-check-plan-2026-08.md` Phase 2 step 4).
