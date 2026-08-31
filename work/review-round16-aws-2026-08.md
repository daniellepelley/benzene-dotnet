# Round 16 - AWS Package Review (2026-08-30)

**Scope, per the brief:** every `src/Benzene.Aws.*` and `src/Benzene.Clients.Aws*` package - Lambda
hosting bridges, SQS/SNS/S3/DynamoDB/Kinesis/EventBridge/StepFunctions/API Gateway adapters, the AWS
Lambda mesh discovery provider, X-Ray tracing - re-reviewed at commit `28473b0` on `main`, with
particular attention to (a) interaction gaps between the round-15 `#227`/`#228`/`#229` fixes (all
landing in the same commit, `1f622c6`, touching the shared `SingleContextEscalatingApplicationBase`
and the S3/DynamoDb/Kafka preset-topic wiring), (b) batch/partial-failure handling across
SQS/Kinesis/DynamoDB Streams/Kafka, (c) ambient-cancellation-token threading into AWS SDK calls, and
(d) the lesser-used `Benzene.Clients.Aws.Sqs` client-send path and Lambda Core's event-stream parsing.

**Method:** read every trigger/client package in scope against the round 1-15 fix record
(`work/outstanding-bugs.md`) so nothing already known/accepted gets re-reported, cross-referenced the
AWS SDK's actual method signatures (reflected against the installed `AWSSDK.*` NuGet packages, not
assumed) where a CLAUDE.md comment made a specific claim about SDK overloads, then built concrete
failure scenarios for anything that looked suspicious and proved or disproved them with throwaway
xUnit tests run against the real assemblies. Both findings below are backed by a red test that fails
against the code as it stands at `28473b0`.

Because the shared checkout had concurrent review agents mid-edit in the primary
`test/Benzene.Core.Test` project (an unrelated file there had a pre-existing compile error from
another agent's in-progress work), both red tests were built and run in an isolated scratch xUnit
project referencing the real `src/*.csproj` projects directly, rather than by adding files to the
shared test project - so this review never touched the shared test tree. `git status`/`git diff`
against `/workspace/benzene-dotnet` are clean (no source or test files modified) as this document is
written.

---

## Worth-fixing

### 1. `IdempotencyMiddleware`'s "null `MessageResult` == success" convention directly contradicts the "null == failure, redeliver" convention `#229` just extended to SNS/S3/EventBridge (and SQS/DynamoDb already had)

`src/Benzene.Idempotency/IdempotencyMiddleware.cs:143-153` (`WasSuccessful`):

```csharp
private static bool WasSuccessful(TContext context)
{
    // Prefer the pipeline's own result signal when the transport sets one; otherwise treat
    // "the handler did not throw" as success.
    if (context is IHasMessageResult { MessageResult: not null } hasResult)
    {
        return hasResult.MessageResult.IsSuccessful;
    }

    return true;
}
```

When the downstream pipeline completes without throwing but also without ever setting
`MessageResult` (the exact scenario `#229`'s own doc comment on
`SingleContextEscalatingApplicationBase` calls out: "a non-standard pipeline that omits
`MessageRouter` or short-circuits before it runs"), `WasSuccessful` returns `true` and
`IdempotencyMiddleware` permanently marks the idempotency claim `Completed`. But `#229` (SNS/S3/
EventBridge) and the pre-existing SQS/DynamoDb convention both treat that identical
null-`MessageResult` condition as a **failure** requiring redelivery ("err toward redelivery, never
toward loss"). `IdempotencyMiddleware` is generic (`IMiddleware<TContext>`) and its own doc comment
recommends placing it early in any transport's pipeline - it is not an SNS-only or SQS-only concern.

Concretely, for a pipeline built without `MessageRouter` (or one where an earlier middleware
short-circuits before it runs) and with `.UseIdempotency()` wired in:

1. **First attempt**: the transport-level escalation check (`SingleContextEscalatingApplicationBase`
   for SNS/S3/EventBridge, or the equivalent inline check in `SqsApplication`/`DynamoDbApplication`)
   sees `MessageResult == null` and reports/throws for redelivery - but `IdempotencyMiddleware`,
   running *inside* that same pipeline invocation, has already called `_store.CompleteAsync(key,
   claimToken, true, ...)`, recording the message **Completed** (default 24h TTL in
   `InMemoryIdempotencyStore`) at the exact moment the transport is telling AWS to redeliver it.
2. **The redelivery** (which the transport itself just requested) hits `IdempotencyMiddleware` again
   with the same key, finds the claim already `Completed`, and short-circuits via `HandleDuplicate` -
   setting `MessageResult = BenzeneResult.Ok()` **unconditionally**, without ever re-invoking `next()`
   (the real handler). The transport sees an explicit success and acks/deletes the message.

The two conventions were each independently correct in isolation before `#229` (when SNS/S3/
EventBridge also treated null as success, they agreed with `IdempotencyMiddleware` by coincidence);
`#229` closed the SNS/S3/EventBridge gap without anyone re-checking cross-cutting middleware built
against the old, now-inconsistent, convention. The net effect isn't necessarily silent message loss
(the redelivery converges to an explicit success rather than looping), but it means: an idempotent
pipeline that hits this null-result edge case gets an extra, wasted full round-trip through AWS's
retry mechanism (for SNS/S3 today - which have no per-record partial-failure channel - that round
trip re-invokes the **entire batch**, not just the one record) before silently and permanently never
re-running the real handler logic for that key again, even if the underlying condition that produced
the null result was itself transient.

**Verified** with a temporary test (`SnsIdempotencyInteractionRedTest`, run against the real
`Benzene.Idempotency` + `Benzene.Aws.Lambda.Sns` assemblies, not mocked at the boundary under test):
`SnsApplication` wraps a pipeline whose only step is `IdempotencyMiddleware<SnsRecordContext>` around
a `next` that never sets `MessageResult`. First call: `application.HandleAsync(...)` throws
`SnsMessageProcessingException` (the transport's own "redeliver this" signal) - and in the very same
call, `store.TryClaimAsync("key-1")` already reports `IdempotencyStatus.Completed`. Second call (the
redelivery SNS was just told to perform): `application.HandleAsync(...)` returns normally (a success)
and the handler's invocation counter is still `1` - the "redelivered" message was never actually
reprocessed.

**Suggested direction**: `WasSuccessful`'s null branch should match the same "null = not proven
successful" convention `#229` unified everywhere else - i.e. return `false` (release the claim,
don't record completion) rather than `true`, unless a transport explicitly opts out the way Kafka's
own null-skip convention does (which `IdempotencyMiddleware` has no way to know about today, since it
is transport-agnostic).

### 2. Every outbound AWS SDK client middleware (SQS/SNS/EventBridge/Lambda/StepFunctions) calls its `*Async` SDK method with no `CancellationToken`, unlike the sibling HTTP/gRPC clients - `UseTimeout(...)` around an AWS send is silently a no-op

`src/Benzene.Clients.Aws.Sqs/SqsClientMiddleware.cs:36-39`, `src/Benzene.Clients.Aws.Sns/SnsClientMiddleware.cs:34-37`,
`src/Benzene.Clients.Aws.EventBridge/EventBridgeClientMiddleware.cs:24-27`,
`src/Benzene.Clients.Aws.Lambda/AwsLambdaClientMiddleware.cs:36-39` and `AwsLambdaClient.cs:58`,
`src/Benzene.Clients.Aws.StepFunctions/StepFunctionsClient.cs:71,121`, the three AWS batch clients
(`SqsBatchMessageClient.cs:80`, `SnsBatchMessageClient.cs:85`, `EventBridgeBatchMessageClient.cs`),
and `src/Benzene.Aws.Sqs/Client/SqsMessageClient.cs:88` all call their underlying `IAmazonSQS`/
`IAmazonSimpleNotificationService`/`IAmazonEventBridge`/`IAmazonLambda`/`IAmazonStepFunctions` method
with **no `CancellationToken` argument at all** - e.g.:

```csharp
public async Task HandleAsync(SqsSendMessageContext context, Func<Task> next)
{
    context.Response = await _amazonSqs.SendMessageAsync(context.Request);
}
```

This is the same bug class as `#1`/`#2`/`#104` (ambient-cancellation-token threading), but on the
outbound client side, and it is directly disprovable against the codebase's own established fix
pattern for the exact same problem on the exact same interface family:
`HttpClientMiddleware`(`src/Benzene.Clients.Http/HttpClientMiddleware.cs:23-27,33-37`) and
`GrpcBenzeneMessageClient`(`src/Benzene.Grpc.Client/GrpcBenzeneMessageClient.cs:84`) - both
implementations of the same `IBenzeneMessageClient`/pipeline-terminal-middleware shape as the AWS
clients above - resolve `ICancellationTokenAccessor` (from DI or the injected `IServiceResolver`) and
pass `.CancellationToken` into their own outbound call. None of the AWS client types even have a
constructor overload that accepts an `ICancellationTokenAccessor` to thread through, unlike
`HttpClientMiddleware`'s two constructors.

`src/Benzene.Clients.Aws.Lambda/CLAUDE.md:31-33` documents this gap for Lambda specifically as
apparently unfixable - *"The Active-mode ping (via `AwsLambdaBenzeneMessageClient`, which has no
`CancellationToken` overload) is the one path here that still can't forward the token into its own SDK
call."* This is not accurate for the underlying SDK call itself. Reflecting the actual installed
`AWSSDK.Lambda`/`AWSSDK.SQS`/`AWSSDK.SimpleNotificationService`/`AWSSDK.EventBridge`/
`AWSSDK.StepFunctions` packages confirms every one of these methods has a `CancellationToken
cancellationToken = default` overload, matching the universal AWS .NET SDK code-generation pattern:

```
IAmazonLambda.InvokeAsync(InvokeRequest, CancellationToken)
IAmazonSQS.SendMessageAsync(SendMessageRequest, CancellationToken)
IAmazonSimpleNotificationService.PublishAsync(PublishRequest, CancellationToken)
IAmazonEventBridge.PutEventsAsync(PutEventsRequest, CancellationToken)
IAmazonStepFunctions.StartExecutionAsync/DescribeExecutionAsync(..., CancellationToken)
```

(The `IBenzeneMessageClient.SendMessageAsync<TRequest,TResponse>` *interface* method has no
`CancellationToken` parameter - true, and shared with the HTTP/gRPC clients too - but that has never
stopped those two from still reaching for the *ambient* token via `ICancellationTokenAccessor`
exactly as `TimeoutMiddleware`/`RetryMiddleware`/the rest of `Benzene.Resilience` expect every
downstream call to do.)

**Concrete, user-visible symptom**: `UseTimeout(...)` (`Benzene.Resilience.TimeoutMiddleware`) wrapped
around any outbound SQS/SNS/EventBridge/Lambda/StepFunctions send does not enforce its configured
timeout at all. `TimeoutMiddleware` only ever cancels the *ambient* `ICancellationTokenAccessor`
token; if the terminal middleware never reads it, the timer firing does nothing observable - the
pipeline keeps awaiting the real network call for however long it actually takes (which, for a
stalled connection, is governed by the AWS SDK's own default retry/socket timeouts - tens of seconds
to minutes - not the configured `UseTimeout` value). The same gap means a host-level shutdown signal
(anything else that cancels the ambient token, e.g. a graceful-drain path) also fails to abort an
in-flight AWS send.

**Verified** with a temporary test (`SqsTimeoutRedTest`, run against the real `Benzene.Resilience` +
`Benzene.Clients.Aws.Sqs` assemblies): `TimeoutMiddleware<SqsSendMessageContext>` configured with a
30ms timeout wraps `SqsClientMiddleware` backed by a mocked `IAmazonSQS.SendMessageAsync` that never
completes (standing in for a stalled/slow call). After waiting **ten times** the configured timeout
(300ms), the pipeline task is still `WaitingForActivation` - `TimeoutException` never fires, and the
underlying "SDK call" is never touched:

```
Assert.Same() Failure: Values are not the same instance
Expected: Task { Status = RanToCompletion }              // the 300ms Task.Delay "loses" as expected...
Actual:   Task<...> { Status = WaitingForActivation }     // ...but the pipeline task never completes either
```

By contrast, the same setup against `HttpClientMiddleware` (not re-tested here, but by code
inspection) would observe the linked `CancellationToken`, and the underlying `HttpClient.SendAsync`
call would actually be cancelled at ~30ms.

**Suggested direction**: give `SqsClientMiddleware`/`SnsClientMiddleware`/`EventBridgeClientMiddleware`/
`AwsLambdaClientMiddleware` (and the three batch clients / `StepFunctionsClient` / the standalone
`SqsMessageClient`) the same `ICancellationTokenAccessor`-resolving constructor overload
`HttpClientMiddleware` already has, and pass `.CancellationToken` into the existing SDK overload at
each call site - a purely additive change (the SDK already supports it; nothing needs to change on
the wire or in the public `IBenzeneMessageClient`/`IStepFunctionsClient` interfaces).

---

## Areas reviewed and found solid (no new finding)

- **`#227`/`#228`/`#229` themselves** (`SingleContextEscalatingApplicationBase`, the S3/DynamoDb/Kafka
  `PresetTopicHolder` registrations): re-read against `SnsApplication`/`S3Application`/
  `EventBridgeApplication`/`SqsConsumerApplication`. The per-record/per-context DI scoping is correct -
  `PresetTopicHolder` is resolved fresh per scope, and `BoundedFanOut`'s concurrent fan-out gives each
  record its own scope, so there's no cross-record leakage of a preset topic. The infra-rethrow
  (`#228`) and null-result escalation (`#229`) logic is consistent across SNS/S3/EventBridge/SQS/
  DynamoDb given the finding above is really about a *different* middleware's assumption, not a flaw
  in the base class itself.
- **SQS/Kinesis/DynamoDB Streams/Kafka batch/partial-failure reporting**: `SqsApplication` (concurrent
  fan-out, per-record `BatchItemFailure`, race-free via per-task return values rather than a shared
  mutable list - already hardened), `DynamoDbApplication` (sequential, stop-at-first-failure, correct
  `SequenceNumber`/`EventId` fallback), `KinesisStreamCheckpointer` (monotonic watermark, already
  guards the by-reference `IndexOf` mismatch case), and `KafkaApplication` (per-partition sequential +
  cross-partition fan-out, correct offset-resume reporting) were all re-traced end to end and are
  internally consistent with their own documented conventions.
- **`Benzene.ClaimCheck.Aws.S3`**: already threads `CancellationToken` correctly into
  `PutObjectAsync`/`GetObjectAsync` - the interface (`IClaimCheckStore`) carries an explicit
  `CancellationToken` parameter, unlike the `IBenzeneMessageClient` family finding #2 is about.
- **`AwsEventStreamContext`/`AwsLambdaMiddlewareRouter`** (Lambda Core's event-stream parsing): the
  `Handled` claim-detection logic and the try/catch around `JsonSerializer.Deserialize<TRequest>` for
  a malformed/truncated payload are sound; each router in the chain falls through to the next on a
  failed/mismatched deserialize.
- **API Gateway v1/v2 header handling**: `ApiGatewayHttpRequestAdapter`'s case-insensitive,
  first-wins-on-collision header dictionary (the `#105` fix) was re-checked against `ApiGatewayV2`'s
  equivalent and found consistent; no new v1/v2 divergence found beyond what `#89`/`#90`/`#105`
  already closed.

No other findings cleared this codebase's bar (genuine correctness bug, race, resource leak, silent
data corruption, or spec-contract violation) after this pass.
