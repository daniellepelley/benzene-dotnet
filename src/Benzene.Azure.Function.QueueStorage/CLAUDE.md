# Benzene.Azure.Function.QueueStorage

## What this package does
Inbound Azure Queue Storage adapter for the Azure Functions `QueueTrigger` binding (isolated
worker): delivers a queue message to a Benzene middleware pipeline. The Azure counterpart of
`Benzene.Aws.Lambda.Sqs` in spirit, but structurally closer to `Benzene.Azure.Function.EventHub` —
see "Routing" below for why.

## Gap: no self-hosted worker counterpart
Unlike Service Bus/Event Hub/Cosmos DB, which each ship both a self-hosted worker
(`Benzene.Azure.ServiceBus`/`.EventHub`/`.CosmosDb`, for `UseWorker(...)`) *and* this kind of
Functions trigger adapter, there is no `Benzene.Azure.QueueStorage` self-hosted worker — only the
Functions trigger adapter in this package. This is a currently-unbuilt gap, not a deliberate design
decision (see `docs/hosting.md`'s "Worker concurrency" section). Building it is a real feature
addition (a polling worker around the Queue Storage SDK), not something to bolt on as a fix.

## Failure handling: a returned failure result is retried by default (opt out via `QueueStorageOptions`)
Safe-by-default (`QueueStorageOptions.RaiseOnFailureStatus` defaults to `true`, flipped 2026-07-21 —
see `work/archive/settlement-contract-1.0-2026-07.md`): if a handler returns a non-exception failure result (e.g.
`BenzeneResult.ServiceUnavailable(...)`), it is escalated into a thrown
`QueueStorageMessageProcessingException` so the host's `maxDequeueCount` retry/poison-queue handling
takes over — the same treatment an unhandled exception already got. Set
`QueueStorageOptions.RaiseOnFailureStatus = false` (via `UseQueueStorage(action, configure)`) for
at-most-once (a failure result deletes the message like a success, no retry).
`QueueStorageOptions.CatchExceptions` (default `false`) conversely swallows/logs handler exceptions.
Because a returned failure is now retried by default, the handler must be idempotent.

This holds for **both** routing paths. The escalation reads `QueueStorageContext.MessageResult`; the
preset-topic path records it directly, and on the envelope path (`UseBenzeneMessage`, response
suppressed) `BenzeneMessageQueueStorageHandler` surfaces the inner handler's recorded result onto the
outer context (via `BenzeneMessageResultApplication`), so a failure inside the envelope pipeline
escalates too rather than being silently deleted.

**Null-outcome policy (flipped 2026-08-25):** a message whose `MessageResult` is never set - no
handler matched, or (envelope path) nothing recorded on the inner context - is escalated the same as
an explicit failure, not accepted (deleted) as success. The host's `maxDequeueCount` retry/poison-queue
handling gives an escalated-and-retried message somewhere to land. Enforced via the
`AzureFunctionBatchApplicationBase.EscalateUnestablishedOutcome` hook (default `true`, not overridden
here - see `Benzene.Azure.Function.Core/CLAUDE.md`). See `work/settlement-consistency-fix-plan.md`.

## Zero dependencies — deliberately
References only `Benzene.Azure.Function.Core` + `Benzene.Core.MessageHandlers` — no storage SDK,
no Functions extension package (same approach as `Benzene.Azure.Function.CosmosDb` and
`Benzene.Aws.Lambda.Kinesis`'s hand-rolled event model). The consumer's Function App project
references `Microsoft.Azure.Functions.Worker.Extensions.Storage.Queues` itself for the
`[QueueTrigger]` attribute, then calls `HandleQueueMessage(...)` with what the binding delivered.
Do not add SDK packages here without asking first (repo NuGet policy).

## Routing — the body is the entire message
A Queue Storage message has **no properties/attributes** (unlike Service Bus application
properties or SQS message attributes) — just a body. So `QueueStorageMessageTopicGetter` always
returns null, and routing comes from exactly two places:

1. **A Benzene message envelope in the body** — `queue.UseBenzeneMessage(direct =>
   direct.UseMessageHandlers())`, via `BenzeneMessageQueueStorageHandler` (mirrors
   `BenzeneMessageEventHubHandler`: deserializes the text into a `BenzeneMessageRequest`, defers
   to the next middleware if it isn't one). **Forwards the outer scope's ambient cancellation token
   into the inner envelope pipeline's own DI scope (#225, fixed 2026-08)**, by overriding
   `MiddlewareRouter`'s new cancellation-aware `HandleFunction` overload and passing the token into
   `BenzeneMessageResultApplication`'s 3-arg `HandleAsync(request, factory, token)` overload - see
   `Benzene.Core.Middleware/CLAUDE.md`'s `MiddlewareRouter` entry for the shared mechanism. Before this
   fix the inner pipeline always ran with `CancellationToken.None`, even though the outer per-message
   scope had the real host cancellation token seeded.
2. **A fixed per-queue topic** — `queue.UsePresetTopic("orders.created").UseMessageHandlers()`,
   for queues whose producer isn't a Benzene client (a queue usually carries one message type
   anyway). Works because `AddAzureQueueStorage` wraps the null topic getter in
   `PresetTopicMessageTopicGetter` + registers the full mapper set (empty headers, text body,
   result setter, version getter) — so `.UseMessageHandlers()` resolves cleanly.

## Declared triggers (source-generated)
Instead of hand-writing the `[Function]`/`[…Trigger]` class, declare the trigger and let
Benzene's source generator (shipped in `Benzene.Azure.Function.Core`) emit it:
`[assembly: BenzeneQueueTrigger(Name = "orders", QueueName = "orders")]`.
`BenzeneQueueTriggerAttribute` (assembly-scoped, `AllowMultiple`) lives in this package; you own every
binding value. Still reference this transport's `Microsoft.Azure.Functions.Worker.Extensions.*`
package directly, and note `FunctionsEnableWorkerIndexing=false` (auto via Core's
buildTransitive). The hand-written form still works. See `docs/azure-functions.md`.

## Key types
- `QueueStorageMessage` — Benzene's own dependency-free message model: `MessageText` (the common
  `[QueueTrigger] string` binding) plus optional `MessageId`/`DequeueCount`/`InsertedOn` for
  callers who bind the SDK's `QueueMessage` and want the metadata carried across.
- `QueueStorageContext : IHasMessageResult` — wraps one message; `MessageResult` is
  diagnostics-only (the trigger has no per-message settlement — success deletes the message,
  an exception retries it and eventually the host moves it to `<queue>-poison`).
- `QueueStorageApplication` — `EntryPointMiddlewareApplication<QueueStorageMessage[]>` fanning
  out via `MiddlewareMultiApplication`, transport-tagged `"queue-storage"`. The array event shape
  exists for tests/multi-dispatch; the trigger itself delivers one message per invocation.
- `UseQueueStorage(action, maxDegreeOfParallelism = null)` (both `IAzureFunctionAppBuilder` and
  platform-neutral `IBenzeneApplicationBuilder`, no-op off-Azure), `AddAzureQueueStorage()`,
  `QueueStorageRegistrations`, `HandleQueueMessages(params QueueStorageMessage[])`,
  `HandleQueueMessage(string messageText)`. `maxDegreeOfParallelism` optionally bounds fan-out
  concurrency (routed through `Benzene.Core.Middleware`'s `BoundedFanOut`); it only bites on
  multi-message dispatch - the trigger's default one-message-per-invocation delivery has nothing to
  bound.

## Failure handling
A pipeline exception propagates to the Functions host, whose own retry (`maxDequeueCount`,
visibility timeout in `host.json`) and poison-queue machinery is the per-message failure story.
`QueueStorageOptions.RaiseOnFailureStatus` additionally escalates a non-exception failure result
into a throw so the host's retry/poison handling engages for those too (see the top-of-file
section). There is still no per-message settlement dimension (the host owns delete/retry).

## TestHelpers package
`Benzene.Azure.Function.QueueStorage.TestHelpers` (`MessageBuilderExtensions.AsQueueStorageBenzeneMessage`)
turns an `IMessageBuilder<T>` into a `QueueStorageMessage` whose text is the Benzene message envelope
(`{ "topic": ..., "headers": ..., "body": ... }`) serialized with the same serializer the pipeline
deserializes it with (default `JsonSerializer`, or a supplied one) — the shape a `UseBenzeneMessage(...)`
pipeline expects. Not an identity function: it builds the envelope via `AsBenzeneMessage(serializer)`
*and* serializes it into the message text, so a component test can push the demo message straight
through a built Azure Function app's `HandleQueueMessages(...)` entry point exactly as the trigger
would deliver it.

## Tests
- `test/Benzene.Core.Test/Azure/QueueStoragePipelineTest.cs` — envelope routing, preset-topic
  routing of raw payloads (also proves the `AddAzureQueueStorage` registration set is complete
  for `.UseMessageHandlers()`), non-envelope deferral, exception propagation, metadata flow, and
  envelope-path `RaiseOnFailureStatus` escalation + opt-out (`EnvelopeHandlerReturnsFailure_*`).
- `test/Benzene.Core.Test/Azure/QueueStorageGettersTest.cs` (added round 14-15, coverage seeding
  alongside #235) — the three getters in isolation, including a malformed-input case:
  `QueueStorageMessageBodyGetter` passes malformed/truncated JSON text straight through unparsed and
  unrejected, since it does no parsing of its own (the body is opaque text at this layer - any
  envelope validation happens downstream). Already correct, no bug found, gap closed.

## Claim-check hydration
Not wired here yet: `Benzene.ClaimCheck`'s hydrate middleware needs an
`IMessageBodySetter<QueueStorageContext>` registered — the same 5-line pattern as
`Benzene.Aws.Lambda.Sqs`/`.Sns`/`.EventBridge` ship (see `work/archive/claim-check-plan-2026-08.md` Phase 2 step 4).
Note Azure Queue Storage's 64 KB message limit makes this transport the most likely to actually need
the pattern.
