# Benzene.Azure.Function.Timer

## What this package does
Azure Functions `TimerTrigger` adapter (isolated worker): delivers scheduled ticks into a Benzene
middleware pipeline, so scheduled jobs get the same pipeline composition
(correlation/metrics/exception handling) as every other entry point — and, via a preset topic, can
invoke the *same message handlers* as any messaging transport.

## Zero dependencies — deliberately
References only `Benzene.Azure.Function.Core` + `Benzene.Core.MessageHandlers`. The consumer's
Function App project references `Microsoft.Azure.Functions.Worker.Extensions.Timer` itself for the
attribute; `TimerTriggerInfo`/`TimerScheduleStatus` property names match the isolated worker's
`TimerInfo` JSON, so the trigger parameter can be bound directly as Benzene's type. Do not add SDK
packages here without asking first (repo NuGet policy).

## Two consumption modes
1. **Direct** — `UseTick(...)` terminal sugar (context and info overloads), for scheduled work
   that doesn't need routing.
2. **Message-handler dispatch** — `UsePresetTopic("nightly-cleanup").UseMessageHandlers()`: the
   tick routes to the handler declaring that topic, making a scheduled job just another message
   handler (testable/portable like any other). The tick's body is the serialized
   `TimerTriggerInfo` (via `TimerMessageBodyGetter`, ctor-injected `JsonSerializer`), so a handler
   request type mirroring its properties receives the schedule info, and an empty request type
   binds cleanly.

## Naming caution
The builder extension is **`UseTimerTrigger`** — NOT `UseTimer`, which already exists in
`Benzene.Diagnostics` (`Timers/Extensions.cs`) as the timing middleware. Keep it that way.

## Declared triggers (source-generated)
Instead of hand-writing the `[Function]`/`[…Trigger]` class, declare the trigger and let
Benzene's source generator (shipped in `Benzene.Azure.Function.Core`) emit it:
`[assembly: BenzeneTimerTrigger(Name = "nightly", Schedule = "0 0 2 * * *")]`.
`BenzeneTimerTriggerAttribute` (assembly-scoped, `AllowMultiple`) lives in this package; you own every
binding value. Still reference this transport's `Microsoft.Azure.Functions.Worker.Extensions.*`
package directly, and note `FunctionsEnableWorkerIndexing=false` (auto via Core's
buildTransitive). The hand-written form still works. See `docs/azure-functions.md`.

## Key types
- `TimerTriggerInfo` / `TimerScheduleStatus` — dependency-free models (`IsPastDue`,
  `Last`/`Next`/`LastUpdated`).
- `TimerContext : IHasMessageResult` — diagnostics-only result; a tick has no caller to answer.
- `TimerApplication` — `EntryPointMiddlewareApplication<TimerTriggerInfo>` wrapping
  `TimerTickApplication`, transport tag `"timer"`, one DI scope per tick.
- `TimerTickApplication` — runs one tick through the pipeline and applies `TimerOptions`.
- `TimerOptions` / `TimerMessageProcessingException` — see "Failure handling" above.
- `UseTimerTrigger(action)` / `UseTimerTrigger(action, configure)` (both builders, no-op off-Azure),
  `AddAzureTimer()`, `TimerRegistrations`, `UseTick(...)`, `HandleTimer(TimerTriggerInfo)` / `HandleTimer()`.

## Failure handling
`TimerOptions` mirrors every sibling Azure Function trigger package's `*Options` type, applied here
to Timer's single tick rather than a batch: `RaiseOnFailureStatus` (default `true`, safe-by-default)
escalates a message handler's *explicit* failure result on `TimerContext.MessageResult`
(`IsSuccessful == false` — a `UsePresetTopic(...).UseMessageHandlers()` tick whose handler returned
`BenzeneResult.UnexpectedError()` rather than throwing) into a thrown
`TimerMessageProcessingException`, so the Functions host records a failed invocation instead of
completing silently. **Deliberately `== false`, not the `!= true` convention** the message-routed
batch triggers use (round 15, WP-C): those transports run every item through `MessageRouter`, which
unconditionally records a result, so an unset result there only ever means the router never got to
run. A timer tick has no such guarantee — the **direct** `UseTick(...)` consumption mode never
touches `MessageResult` at all — so treating an unset result as failure would escalate every plain
tick by default; only a message handler that actually ran and reported failure triggers it.
`CatchExceptions` (default `false`, matching every sibling) optionally contains that exception — or
any exception the pipeline itself threw — logging it instead of letting it cascade. One carve-out
(#257, mirroring `SingleContextEscalatingApplicationBase`'s #228 fix): an infrastructure/DI-wiring
failure (`BenzeneFailure.IsInfrastructure`, e.g. a missing container registration) is not this tick's
fault and will fail identically for every tick, so it's logged with a distinguishing message and then
**rethrown regardless of `CatchExceptions`** — swallowing it would mean the invocation reports success
while every tick fails the same way, forever. Configure via the ctor's optional `TimerOptions?`
parameter or `UseTimerTrigger(pipeline, configure)`'s `Action<TimerOptions>` overload.
Note the platform reality either way: the timer trigger does **not** retry a failed tick — the next
occurrence just runs on schedule — so a job needing at-least-once semantics should enqueue work
(queue/Service Bus) rather than doing it inline in the tick. `RaiseOnFailureStatus` only affects
whether the failure is visible (failed-invocation telemetry), not whether it is retried.

## Tests
- `test/Benzene.Core.Test/Azure/TimerPipelineTest.cs` — tick delivery with schedule info,
  preset-topic dispatch to a real message handler, exception propagation, platform-neutral no-op.

## No egress package — deliberately (release plan §5.2)
There is no `Benzene.Clients.Azure.Timer`. A timer trigger is a **scheduler**, not a transport —
there is nothing to publish to; a tick is purely inbound. Egress only exists for transports a
service can send *to* (queues, topics, event streams).
