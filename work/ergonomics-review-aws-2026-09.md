# Ergonomics review: AWS hosting and the AWS adoption path (2026-09-02)

**Reviewer:** cross-language ergonomics champion (spec repo), enforcing
`docs/specification/design-principles.md` §4.1 "The shorthand ladder".
**Commit reviewed:** `f3f1be5` on `benzene-dotnet`.
**Territory:** `docs/getting-started-aws.md`, `docs/aws-iam-permissions.md`, the AWS cookbooks
(`aspnet-with-sqs-and-sns`, `deploy-with-serverless-framework`, `testing-lambda-functions`,
`handling-sqs-failures`, `sns-fan-out`, `lambda-cold-start-optimization`, `transactional-outbox`,
`idempotency`), `docs/claim-check.md`, `docs/terraform.md`, `docs/testing-benzene.md`;
`examples/Aws`, `examples/AwsMesh` (service side), `examples/Outbox`, `examples/Saga`,
`examples/Cloudflare`, `templates/content/aws-*`; `src/Benzene.Aws.Lambda.*`, `Benzene.Aws.Sqs`,
every `*.TestHelpers`/`TestPayloads`, `Benzene.Outbox.DynamoDb`, `Benzene.Idempotency.DynamoDb`,
`Benzene.EventSourcing.DynamoDb`, `Benzene.ClaimCheck.Aws.S3`, `Benzene.HealthChecks.DynamoDb`.
**Verification status:** no `dotnet` SDK in this sandbox. Every "compiles"/"runs" claim below is
**trace-only** against source; the one exception is where the repo's own CI-run test already proves
the claim, which is cited.

## Executive verdict

1. **Go-live blockers: 1 - should-fix: 14 - polish: 8.**
2. The core ladder is sound: `AwsLambdaHost<T>` / `UseAwsLambda` / `UseXxx(...)` / `UseMessageHandlers()` is composed from public API, every rung is reachable, and the start-up-check phase (`RunStartUpChecks`, run by the host *and* the test host) is the best implementation of §4.1 rule 3 I have seen in any port.
3. The blocker is the **local-testing story**: `AwsLambdaBenzeneTestHost` serialises with Newtonsoft while the EventBridge/DynamoDB/Kinesis event models are System.Text.Json-attributed, so the shipped `AsEventBridge()`/`AsDynamoDb()`/`AsKinesis()` builders cannot be sent through the shipped host; the flagship Minimal example hand-rolls a dictionary to work around it, and the getting-started guide names a `SendSnsAsync` that does not exist.
4. The should-fixes are mostly **ceremony the framework already knows how to remove**: 7 copies of the Lambda bootstrap while `AwsLambdaBootstrap` exists, 5 copies of `AddLogging(AddConsole)` because the host ships no provider, 6 copies of a `GetConfiguration()` override that equals the default, a 309-line hand-rolled framework seam in `AwsMesh/Shared`, and 7 hand-registered AWS SDK clients - plus one real rule-3 gap (handler constructor dependencies are not checked at start-up) and one contradictory deploy story (`dotnet10` vs `dotnet8` vs `provided.al2023`).
5. Verdict: **NEEDS CHANGES** before go-live for finding 1 and the runtime-story contradiction (finding 4, which is a blocker if the `dotnet10` managed runtime the guide deploys to does not exist - unverifiable from here); everything else can land as a backlog in severity order.

---

## Findings, in severity order

Grades: **BLOCKER** (a newcomer following the docs is stopped or misled), **SHOULD-FIX** (a
routine capability costs ceremony, or a convention fails on the message path), **POLISH**.

### 1. The Lambda test host cannot carry three of the eight event sources, and the guide promises a `SendSnsAsync` that does not exist  [BLOCKER - ladder-broken, invisible-ladder]

**§4.1 clause:** "A shorthand MUST be composed from the public explicit form" (test 2: from any
rung you can drop exactly one level and keep going) and "The ladder MUST be visible from the top".

**Evidence.**

- `src/Benzene.Aws.Lambda.Core.TestHelpers/AwsLambdaBenzeneTestHost.cs:21-24` - the only way in
  is Newtonsoft:
  ```csharp
  private static Stream ObjectToStream(object obj)
  {
      return StringToStream(JsonConvert.SerializeObject(obj));
  }
  ```
- The routers deserialise with System.Text.Json (`Amazon.Lambda.Serialization.SystemTextJson`,
  `src/Benzene.Aws.Lambda.Core/AwsLambdaMiddlewareRouter.cs:25`) and Benzene's own event models are
  STJ-attributed: `src/Benzene.Aws.Lambda.EventBridge/EventBridgeEvent.cs:22`
  `[JsonPropertyName("detail-type")]`, `src/Benzene.Aws.Lambda.Kinesis/KinesisEvent.cs:32`
  `[JsonPropertyName("eventSource")]`, `src/Benzene.Aws.Lambda.DynamoDb/DynamoDbEvent.cs:15`
  `[JsonPropertyName("Records")]`. Newtonsoft ignores those attributes, so `AsEventBridge()` goes
  over the wire as `DetailType`, is never claimed, and the invocation throws the "event type has not
  been recognized" `BenzeneException` (`AwsLambdaEntryPoint.cs:53`).
- The repo already knows. `src/Benzene.Aws.Lambda.DynamoDb.TestHelpers/MessageBuilderExtensions.cs:17-22`:
  "The Newtonsoft-based `AwsLambdaBenzeneTestHost.SendEventAsync(object)` path cannot round-trip the
  raw `JsonElement` images this event carries." And the flagship example's own test helper,
  `examples/Aws/Benzene.Examples.Aws.Minimal.Tests/Helpers/AwsEventBuilder.cs:74-78`: "the test
  host serializes the event with Newtonsoft, which ignores that type's System.Text.Json
  `[JsonPropertyName("detail-type")]` attributes - so a plain object would arrive with the wrong keys
  and go unrecognised" - followed by a hand-built `Dictionary<string, object>`.
- No in-repo test sends `AsEventBridge()`, `AsDynamoDb()` or `AsKinesis()` through
  `AwsLambdaBenzeneTestHost`; every use drives `EventBridgeApplication`/`DynamoDbApplication`
  directly (`test/Benzene.Core.Test/Aws/EventBridge/EventBridgeMessagePipelineTest.cs:36,64,94`,
  `test/Benzene.Core.Test/Aws/DynamoDb/*`).
- `docs/getting-started-aws.md` §6: "`SendSqsAsync`/`SendSnsAsync` come from the matching
  `Benzene.Aws.Lambda.Sqs.TestHelpers`/`Benzene.Aws.Lambda.Sns.TestHelpers` packages". There is no
  `SendSnsAsync` anywhere in `src/` (grep); `Benzene.Aws.Lambda.Sns.TestHelpers` contains only
  `AsSns()`. The only `Send*Async` extensions that exist are `SendSqsAsync` and `SendApiGatewayAsync`.

**What the user experiences.** They follow the guide's "one host, every event source" test story,
which is proven (by `test/Benzene.Core.Test/Docs/AwsQuickstartRunsTest.cs`) for API Gateway and SQS
only. The moment they add EventBridge - the fourth source the Minimal example itself advertises -
`MessageBuilder.Create(...).AsEventBridge()` compiles, is sent, and throws at invocation with a
message about "the JSON for the event is not complete". They conclude the helper is broken and
hand-roll a dictionary, exactly as the example did. A user who types the guide's `SendSnsAsync` gets
a compile error.

**Proposed change.**

1. Make `AwsLambdaBenzeneTestHost` serialise with the same serializer family the routers read with
   (System.Text.Json), or accept an already-serialised `string`/`Stream` overload and have each
   `As*()` helper hand back wire JSON. The `Amazon.Lambda.*Events` POCOs (SQS/SNS/API Gateway) carry
   no serializer-specific attributes, so they are unaffected either way.
2. Ship the missing rungs for parity with `SendSqsAsync`/`SendApiGatewayAsync`: `SendSnsAsync`,
   `SendEventBridgeAsync`, `SendS3Async`, `SendDynamoDbAsync`, `SendKinesisAsync`, each one line over
   `SendEventAsync<TResponse>(builder.AsXxx())`.
3. Add one test per event source that goes through `BuildAwsLambdaTestHost()` - the construction the
   guide recommends - so this cannot regress.
4. Fix `docs/getting-started-aws.md` §6 to name only methods that exist.

Before (`examples/Aws/Benzene.Examples.Aws.Minimal.Tests/AwsMinimalTests.cs:76` +
`Helpers/AwsEventBuilder.cs:79-85`):
```csharp
await _host.SendEventAsync(AwsEventBuilder.EventBridge(Topic, AnOrder("ORD-5")));
// ...where AwsEventBuilder.EventBridge is a 7-line Dictionary<string, object> built by hand
```
After:
```csharp
await _host.SendEventBridgeAsync(MessageBuilder.Create(Topic, AnOrder("ORD-5")));
```
and the 85-line `AwsEventBuilder.cs` is deleted (see finding 10).

---

### 2. A handler whose constructor dependency is unregistered passes every start-up check and fails on the first message  [SHOULD-FIX - magic / late failure]

**§4.1 clause:** "The price of a convention is a start-up check ... verified before any message is
handled ... A convention that can first fail on the message path has not paid for itself."

**Evidence.**

- Handlers are discovered by convention (`[Message]` scan) and constructed from DI per message:
  `src/Benzene.Core.MessageHandlers/MessageHandlerFactory.cs:13-16` "resolves the handler instance
  described by an `IMessageHandlerDefinition` from DI", called from `MessageRouter` on dispatch.
- The start-up phase constructs *middleware*, not handlers:
  `src/Benzene.Core.MessageHandlers/StartUpChecks/PipelineResolutionStartUpCheck.cs:41-47` iterates
  `PipelineDescriptor.Constructors`; `MessageRouter<TContext>` is constructed but never asks the
  factory for a handler.
- The container is built without validation: `src/Benzene.Aws.Lambda.Core/AwsLambdaHost.cs:41`
  `new MicrosoftServiceResolverFactory(services)` and
  `src/Benzene.Microsoft.Dependencies/MicrosoftServiceResolverFactory.cs:21`
  `bool validateOnBuild = false`.
- The nine `IStartUpCheck` implementations in `src/` (duplicate-topic, empty-handler-registry,
  pipeline-resolution, terminal-middleware, outbound-routing, in-process-route, http-route,
  unmapped-response-handler) contain no handler-resolution check. `AddBenzeneWarmUp` pre-builds
  serializer metadata and validators (`examples/AwsMesh/Shared/MeshServiceWiring.cs:94-98`), not
  handler instances.

**Concrete misconfiguration missed.** Delete
`services.AddSingleton<IProcessedLog, InMemoryProcessedLog>();` from
`examples/Aws/Benzene.Examples.Aws.Minimal/StartUp.cs:45`. `PlaceOrderMessageHandler(IProcessedLog)`
still compiles, `AwsLambdaHost`'s constructor completes, all start-up checks pass, and the first
`POST /orders` fails with a DI resolution exception from inside `MessageHandlerFactory` - on the
message path, in production, for a mistake that was fully knowable at INIT. (Trace-only; the
`EmptyHandlerRegistryStartUpCheck.cs:13-17` remarks describe the sibling "wrong assembly" case that
was found exactly this way.)

**What the user experiences.** A green build, a green test run if no test happens to hit that
handler, a green cold start, and a 500/redriven message in production whose stack trace starts in
Benzene internals.

**Proposed change.** Add `HandlerResolutionStartUpCheck` (`Name => "handler-resolution"`) that, on
the same throwaway scope the other checks use, resolves `definition.HandlerType` for every
`IMessageHandlerDefinition` the finder returns and reports every failure at once in the
`pipeline-resolution` style, naming the handler and the innermost missing type:
```
  - handler-resolution: PlaceOrderMessageHandler cannot be constructed: Unable to resolve service
    for type 'IProcessedLog'. Register it in ConfigureServices (services.AddSingleton<IProcessedLog, ...>()).
```
This is composed from public API (`IMessageHandlersFinder`, `IServiceResolver`) and costs one
resolve per handler at INIT. No service code changes. A cheaper alternative exists because
`AddMessageHandlers(Type[])` already registers every discovered handler as a scoped concrete type
(`src/Benzene.Core.MessageHandlers/DI/Extensions.cs:248-252` `services.AddScoped(handler.HandlerType)`):
`AwsLambdaHost` could pass `validateOnBuild: true` to `MicrosoftServiceResolverFactory`, which the
container would then validate. That option is documented as off by default in
`MicrosoftServiceResolverFactory.cs:21-43` (it also turns on `ValidateScopes`), so the dedicated
check is the safer first step; either way the failure must land at INIT.

---

### 3. The Lambda host ships no logging provider, so every AWS service re-adds one and a broken SQS pipeline is silent by default  [SHOULD-FIX - ceremony x5, late failure]

**§4.1 clause:** "What a steer should cost: declaration, not wiring" and rule 3 (finding out late).

**Evidence.**

- `src/Benzene.Aws.Lambda.Core/AwsLambdaHost.cs:34` `services.AddLogging();` - no provider.
- Every AWS template writes the same four lines with the same comment:
  `templates/content/aws-apigateway/StartUp.cs:28-32`, `aws-sqs/StartUp.cs:27-31`,
  `aws-sns/StartUp.cs:27-31`:
  ```csharp
  // AddConsole() so ILogger output reaches CloudWatch (a Lambda host wires no provider by
  // default). ...
  services.AddLogging(x => x.AddConsole());
  ```
  `examples/AwsMesh/Shared/MeshServiceWiring.cs:66` `services.AddLogging(logging => logging.AddJsonConsole());`
  `examples/Aws/Benzene.Examples.Aws/DependenciesBuilder.cs:69` `services.AddLogging(x => x.AddConsole().AddSerilog());`
  Five copies in territory; 14 `AddLogging(` calls across examples + templates.
- The cost of forgetting is documented as a silent failure:
  `docs/cookbooks/aspnet-with-sqs-and-sns.md` Troubleshooting: "with no logging provider configured,
  a completely broken pipeline is byte-identical on the wire to a message with no matching handler".
  `src/Benzene.Aws.Lambda.HttpBridge/CLAUDE.md` says the same.

**What the user experiences.** The Minimal example and the getting-started `StartUp` (which have no
`AddLogging` at all) run with a logger that goes nowhere; the first SQS handler exception is logged
to `NullLogger` and the record is redriven with no trace of why.

**Proposed change.** `AwsLambdaHost`, `InlineAwsLambdaStartUp.Build` and `BuildAwsLambdaHost` add a
console provider by default, in a way a user's own `AddLogging(...)` (which runs later, in
`ConfigureServices`) can replace or clear - Lambda captures stdout to CloudWatch, so this is the
platform's own steer. Document the explicit form in one sentence: "the host registers a console
provider; call `services.AddLogging(x => x.ClearProviders()...)` to replace it."

Before (template `ConfigureServices`, 4 lines of comment + call): as quoted above.
After: nothing - the block is deleted from all three templates and the two examples.

---

### 4. The deployment runtime story contradicts itself across the guide, the templates, the cookbooks and the examples  [SHOULD-FIX - honesty; BLOCKER if `dotnet10` is not a real managed runtime]

**§4.1 clause:** "Examples are where this is proved" - the ceremony must be honest; a snippet that
does not match the example it points at is a broken rung.

**Evidence (five different answers to "what runtime does a .NET 10 Benzene Lambda deploy on").**

| Source | Says |
|---|---|
| `docs/getting-started-aws.md` §7 (`template.yaml`) and `examples/Aws/Benzene.Examples.Aws/template.yaml:31-33` | `Runtime: dotnet10` - "AWS added a managed .NET 10 Lambda runtime in Jan 2026" |
| `templates/content/aws-apigateway/template.yaml:11-13` | `Runtime: dotnet8` - ".NET 10 has no AWS-managed Lambda runtime yet" |
| `docs/cookbooks/deploy-with-serverless-framework.md` (`serverless.yml`) | `runtime: dotnet8 # managed .NET 8 runtime; runs a net10.0 project fine` |
| `docs/cookbooks/aspnet-with-sqs-and-sns.md` §3 | ".NET has no managed Lambda runtime, so the function ships as a self-contained executable on `provided.al2023`" |
| `src/Benzene.Aws.Lambda.Hosting/AwsLambdaBootstrap.cs:16`, `examples/AwsMesh/*/Program.cs:4-5`, `examples/AwsMesh/README.md` "Cold-start tuning" | ".NET has no managed Lambda runtime from .NET 8 onward" / ".NET 10 has no managed Lambda runtime; deploy self-contained on `provided.al2023`" |

**What the user experiences.** They generate `benzene.aws.apigateway` (dotnet8), read the guide it
links to (dotnet10), then open the "fuller example" it links to (`examples/Aws`, dotnet10) and the
"real deployed example" (`AwsMesh`, provided.al2023 + a hand-written bootstrap loop). Whichever they
pick, two of the four documents tell them it is wrong.

**Proposed change.** Decide the answer once (I cannot verify AWS's runtime catalogue from this
sandbox), state it in `getting-started-aws.md` §7 only, and have the templates, the SAM example, the
serverless cookbook and `AwsLambdaBootstrap`'s remarks link to that sentence rather than restate it.
If the managed `dotnet10` runtime exists, `AwsMesh`'s seven bootstrap `Program.cs` files become
unnecessary; if it does not, §7 and `examples/Aws/template.yaml` currently deploy to a runtime that
does not exist and this is a go-live blocker.

---

### 5. The Lambda bootstrap loop is hand-written 7 times while `AwsLambdaBootstrap.RunAsync(IAwsLambdaEntryPoint)` exists for exactly that  [SHOULD-FIX - duplication x7, invisible-ladder]

**§4.1 clause:** "Duplicated plumbing across examples is a framework bug ... copying it a fourth time
is choosing not to fix it" - and rule 4 (the shorthand exists but is invisible from the top).

**Evidence.**

- `examples/AwsMesh/Orders/Program.cs:6-9` (identical in Payments, Shipping, Inventory,
  Notifications, Analytics, and Mesh - 7 copies, grep `new LambdaBootstrap`):
  ```csharp
  var function = new Function();
  using var handlerWrapper = HandlerWrapper.GetHandlerWrapper(function.FunctionHandlerAsync);
  using var bootstrap = new LambdaBootstrap(handlerWrapper);
  await bootstrap.RunAsync();
  ```
- `src/Benzene.Aws.Lambda.Hosting/AwsLambdaBootstrap.cs:68-80` - the overload whose remarks name this
  exact case: "Use this overload to host a custom `AwsLambdaHost<TStartUp>` subclass (for example one
  overriding `OnInvocationCompleteAsync` to flush telemetry): `await AwsLambdaBootstrap.RunAsync(new Function());`".
- No `AwsMesh` csproj references `Benzene.Aws.Lambda.Hosting`
  (`examples/AwsMesh/Orders/Benzene.Examples.AwsMesh.Orders.csproj` ProjectReferences).
- `Benzene.Aws.Lambda.Hosting` is not mentioned in `docs/getting-started-aws.md` at all; it appears
  once in `docs/cookbooks/aspnet-with-sqs-and-sns.md:258` and once in `docs/reference/packages.md:264`.

**What the user experiences.** The repo's only deployed AWS example shows them the explicit form
with no comment saying it is deliberate, so they copy it; the one-line rung is something they will
find only if they read a cookbook about ASP.NET.

**Proposed change.** Replace all seven with:
```csharp
await AwsLambdaBootstrap.RunAsync(new Function());
```
(`TracingLambdaHost` keeps its `OnInvocationCompleteAsync` override; the caller-owned overload does
not dispose it, which is the documented contract). Add a "Custom runtime (`provided.al2023`)"
subsection to `getting-started-aws.md` §7 that shows this line and names the three explicit calls it
collapses - `AwsLambdaBootstrap.cs:20-24` already has that text as XML doc.

---

### 6. The per-transport prelude is re-derived per transport, per example, per template; `MeshServiceWiring` is a 309-line hand-rolled framework seam  [SHOULD-FIX - duplication x6 + x5 + x3, missing shorthand]

**§4.1 clause:** "every service re-derives the same twenty lines slightly differently" (the ceremony
failure), and "when two examples hand-roll the same adapter, that is a missing seam".

**Evidence.**

- `examples/Aws/Benzene.Examples.Aws/StartUp.cs` repeats the same prelude six times inside one
  method: lines 64-71 (`BenzeneMessage`), 75-84 (API Gateway), 86-94 (SNS), 96-104 (SQS), 106-111
  (Kafka), 114-121 (EventBridge). Representative copy (SQS, 96-104):
  ```csharp
  aws.UseSqs(sqsApp => sqsApp
      .UseTimer("sqs-application")
      .UseBenzeneEnrichment()
      .UseXml()
      .UseHealthCheck(healthCheckTopic, healthChecks)
      .UseMessageHandlers(router => router
          .UseFluentValidation()
      )
  );
  ```
- `examples/AwsMesh/Shared/MeshServiceWiring.cs:303-308` solves the same problem by writing a
  private generic helper and applying it five times (238, 248, 257, 275, 282, 291):
  ```csharp
  private static IMiddlewarePipelineBuilder<TContext> Observe<TContext>(IMiddlewarePipelineBuilder<TContext> pipeline)
      => pipeline
          .UseW3CTraceContext()
          .UseBenzeneEnrichment()
          .UseBenzeneMetrics()
          .UseLogResult(log => log.WithCorrelationId());
  ```
- The three AWS templates each write `.UseBenzeneEnrichment().UseLogResult(_ => { })` (12 copies of
  that pair across examples + templates, grep).
- `MeshServiceWiring.Configure(app, serviceName, Type[] handlers, IHealthCheck[] healthChecks,
  bool enableOutboxDispatchStream, bool enableSqsIdempotency, bool enableClaimCheckHydration)`
  (lines 224-231) is what "an AWS Cloud Service" costs today when written from public API: the file
  is 309 lines, it is shared by six services, and its intent surface is three boolean flags.

**What the user experiences.** A service that hosts N transports writes its observability and
validation prelude N times, or writes its own `Observe<T>()`; there is no framework spelling for
"apply this to every transport I mount". The example that shows the fleet-scale shape has already
built the missing layer itself, which is the §4.1 signal in its purest form.

**Proposed change** (a proposal; public surface is forever). Composed entirely from public API,
because `MeshServiceWiring` proves it can be:

1. `IMiddlewarePipelineBuilder<AwsEventStreamContext>.UseTransportDefaults(Action<IMiddlewarePipelineBuilder<TContext>>)`
   - or a `UseAwsLambda(aws => ..., defaults: pipeline => pipeline.UseBenzeneEnrichment()...)`
   parameter - applied by every subsequent `UseXxx` before its own action. Generic over `TContext`
   exactly as `Observe<T>` is.
2. Longer term, promote `MeshServiceWiring.Configure` into a package-level shorthand
   (`UseAwsLambdaCloudService(serviceName, handlers, healthChecks, cloud => ...)`) whose xmldoc lists
   the six `UseXxx` calls it composes, with the flags replaced by the middleware they stand for
   (`sqs => sqs.UseIdempotency().UseClaimCheck()`).

Before: 6 x 8 lines in `examples/Aws/StartUp.cs`. After:
```csharp
app.UseAwsLambda(aws => aws
    .UseTransportDefaults(p => p.UseBenzeneEnrichment().UseXml().UseHealthCheck(healthCheckTopic, healthChecks))
    .UseApiGateway(api => api.UseHealthCheck("benzene:healthcheck", "POST", "/healthcheck", healthChecks).UseMessageHandlers(r => r.UseFluentValidation()))
    .UseSns(sns => sns.UseMessageHandlers(r => r.UseFluentValidation()))
    .UseSqs(sqs => sqs.UseMessageHandlers(r => r.UseFluentValidation()))
    .UseKafka(k => k.UseMessageHandlers(r => r.UseFluentValidation()))
    .UseEventBridge(eb => eb.UseMessageHandlers(r => r.UseFluentValidation())));
```

---

### 7. Every DynamoDB/S3-backed capability requires the user to hand-register the AWS SDK client; the failure names the type, not the fix  [SHOULD-FIX - ceremony x7, explicit-only capability]

**§4.1 clause:** "Every capability a service needs routinely MUST have a shorthand. A capability that
exists only in explicit form is unfinished, not minimal", and rule 3's "the failure names what was
looked for, where, and what to add".

**Evidence.**

- The convention is stated as policy in five `CLAUDE.md`s (`Benzene.HealthChecks.DynamoDb/CLAUDE.md`:
  "the **consumer** registers it (Benzene does not register AWS SDK clients)";
  `Benzene.ClaimCheck.Aws.S3/Extensions.cs:19-20`; `Benzene.Outbox.DynamoDb/Extensions.cs:14`;
  `Benzene.Idempotency.DynamoDb/Extensions.cs:13`; `Benzene.EventSourcing.DynamoDb/Extensions.cs:13`).
- What that costs the one deployed example: `examples/AwsMesh/Orders/Startup.cs:81`
  `services.AddSingleton<IAmazonDynamoDB>(_ => new AmazonDynamoDBClient());`, `:93`
  `services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client());`;
  `examples/AwsMesh/Payments/Startup.cs:58`, `:68` (same two again);
  `examples/AwsMesh/Shared/MeshServiceWiring.cs:112-123` (`IAmazonSQS`, `IAmazonSimpleNotificationService`,
  `IAmazonEventBridge`, each behind an `if`). Seven SDK-client registrations by hand, each with a
  comment explaining why it is there.
- Start-up coverage is partial. Inbound `UseIdempotency`/`UseClaimCheck<T>` construct the store in
  the middleware factory (`src/Benzene.Idempotency/Extensions.cs:31-36`,
  `src/Benzene.ClaimCheck/Extensions.cs:65-66`), transport pipelines are `PipelineDescriptor`s
  (`src/Benzene.Core.Middleware/MiddlewarePipelineBuilder.cs:100-102`), and outbound routes are built
  through the same `Build()` (`src/Benzene.Clients/OutboundRoutingBuilder.cs:58`), so a missing
  `IAmazonDynamoDB` behind `UseOutbox()`/`UseIdempotency()`/`UseClaimCheck()` **is** caught by
  `pipeline-resolution` at INIT - good. But the message it produces is the container's ("Unable to
  resolve service for type `Amazon.DynamoDBv2.IAmazonDynamoDB`"), not "register
  `AddSingleton<IAmazonDynamoDB>(...)` before `AddDynamoDbIdempotencyStore`". And two paths are not
  covered at all: `AddDynamoDbOutboxTransaction` (scoped, injected into the handler -
  `src/Benzene.Outbox.DynamoDb/Extensions.cs:59-64`) surfaces on the first handler run, and
  `AddDynamoDbHealthCheck` (`src/Benzene.HealthChecks.DynamoDb/Extensions.cs:14-17`, a factory)
  surfaces on the first health probe.

**What the user experiences.** `AddDynamoDbIdempotencyStore("payments-idempotency")` reads as
complete and is not; the guide (`docs/cookbooks/transactional-outbox.md` Step 1) shows the extra
`AddSingleton<IAmazonDynamoDB>` line, so they learn it from the cookbook rather than from the API.

**Proposed change** (proposal). Keep the explicit form exactly as is and add the steer: each
`AddDynamoDb*`/`AddS3*` registration `TryAdd`s the default SDK client
(`new AmazonDynamoDBClient()` - the SDK's own credential/region chain, the same thing every example
writes) so a user registration made earlier still wins, matching the `TryAdd` discipline
`AddSqs` already documents (`src/Benzene.Aws.Lambda.Sqs/DependencyInjectionExtensions.cs:43-44`).
Have the store factories resolve via `TryGetService` and throw a message naming the registration
call when absent. Document in each package: "resolves `IAmazonDynamoDB` from DI, defaulting to
`new AmazonDynamoDBClient()`; register your own first to override."

Before (`examples/AwsMesh/Payments/Startup.cs:58-60`):
```csharp
services.AddSingleton<IAmazonDynamoDB>(_ => new AmazonDynamoDBClient());
var idempotencyTableName = Environment.GetEnvironmentVariable(PaymentsIdempotencyTableNameEnvVar) ?? "payments-idempotency";
services.UsingBenzene(x => x.AddDynamoDbIdempotencyStore(idempotencyTableName));
```
After:
```csharp
services.UsingBenzene(x => x.AddDynamoDbIdempotencyStore(configuration["PAYMENTS_IDEMPOTENCY_TABLE_NAME"] ?? "payments-idempotency"));
```

---

### 8. `GetConfiguration()` is overridden six times in territory - once with a body identical to the default - while the guide says there is nothing to write  [SHOULD-FIX - ceremony x6, dishonest example]

**§4.1 clause:** "Every line is domain or intent. A line that is neither is ... a deliberate
demonstration of the explicit form, which MUST say so in a comment."

**Evidence.**

- The default: `src/Benzene.Microsoft.Dependencies/BenzeneStartUp.cs:30-31`
  `public virtual IConfiguration GetConfiguration() => new ConfigurationBuilder().AddEnvironmentVariables().Build();`
  whose own remarks (lines 17-28) cite the §4.1 reasoning: "23 of the 50 StartUps in this repo had
  this exact body ... A steer should cost a line when you want something different, not a line when
  you want the default."
- The guide: `docs/getting-started-aws.md` §4 "Configuration defaults to environment variables ...
  so there's no `GetConfiguration()` override to write."
- The guide's runnable companion: `examples/Aws/Benzene.Examples.Aws.Minimal/StartUp.cs:31-34`
  ```csharp
  public override IConfiguration GetConfiguration()
      => new ConfigurationBuilder()
          .AddEnvironmentVariables()
          .Build();
  ```
  byte-for-byte the default, in "the smallest thing that works", with no comment.
- Five more with `.SetBasePath(Directory.GetCurrentDirectory())` + `.AddEnvironmentVariables()` and
  no file provider, so the base path does nothing: `examples/Aws/Benzene.Examples.Aws/StartUp.cs:35-41`,
  `examples/Cloudflare/Benzene.Example.Cloudflare/StartUp.cs:22-28`,
  `templates/content/aws-apigateway/StartUp.cs:18-24`, `aws-sqs/StartUp.cs:17-23`,
  `aws-sns/StartUp.cs:17-23`. (Repo-wide: 20 overrides, 11 of them with `SetBasePath`.)

**What the user experiences.** The newcomer's first file contradicts the newcomer's guide, and the
template they generate ships seven lines of configuration ceremony the framework already does.

**Proposed change.** Delete the override from the Minimal example and the three templates; in
`examples/Aws` and `Cloudflare` either delete it or add the one comment §4.1 requires ("explicit
form shown on purpose; the default is identical"). `hosting.md:205` already shows the right shape
(`// GetConfiguration() not overridden: the default reads environment variables.`).

---

### 9. Two competing `ConfigureServices` shapes for the same capability, and the interaction between them has no start-up check  [SHOULD-FIX - invisible-ladder, late failure]

**§4.1 clause:** rule 2 test 2 ("drop exactly one level and keep going") and rule 3.

**Evidence.**

- Shape A (the guide): `docs/getting-started-aws.md` §4 "Notice there is **no Benzene registration
  in `ConfigureServices`** - no `AddBenzene()`, no `AddMessageHandlers()`." Proven by
  `test/Benzene.Core.Test/Docs/AwsQuickstartRunsTest.cs` (compiles the guide's snippet and sends a
  request through it in CI).
- Shape B (everything else): `templates/content/aws-apigateway/StartUp.cs:33-37`
  ```csharp
  services.UsingBenzene(x => x
      .AddBenzene()
      .AddMessageHandlers(typeof(HelloWorldMessageHandler).Assembly)
      .AddHttpMessageHandlers()
      .AddDiagnostics());
  ```
  `docs/cookbooks/testing-lambda-functions.md` (StartUp: `.AddMessageHandlers(...).AddHttpMessageHandlers()`),
  `docs/hosting.md:205`, `docs/cookbooks/sns-fan-out.md` (`.AddMessageHandlers(...)`).
- Two of Shape B's lines are no-ops on this host: `AddMessageHandlers` pulls in `AddBenzene`
  (`src/Benzene.Core.MessageHandlers/DI/Extensions.cs:175-181`), and `AddApiGateway` - called by
  `UseApiGateway` - calls `AddHttpMessageHandlers()` itself
  (`src/Benzene.Aws.Lambda.ApiGateway/DependencyInjectionExtensions.cs:66`). `.AddBenzene()` appears
  14 times across examples + templates.
- The troubleshooting advice in both AWS documents names an overload that discovers **nothing**.
  `docs/getting-started-aws.md:594` "or that you called the no-argument `AddMessageHandlers()`
  overload, which scans the calling assembly" and `docs/cookbooks/testing-lambda-functions.md:357`
  "or you used the no-argument overload, which only scans the calling assembly" - but
  `src/Benzene.Core.MessageHandlers/DI/Extensions.cs:165-173`: "Registers message-handler dispatch
  infrastructure **without registering any reflection-based handler discovery** - only handlers
  registered explicitly ... will be found." A reader who follows the 404 advice ends up with zero
  handlers; `EmptyHandlerRegistryStartUpCheck` logs (does not throw), and under finding 3 that log has
  no provider to reach. (The pipeline-level `UseMessageHandlers()` at `Extensions.cs:58-61` does
  scan every loaded assembly, which is what the guide's §4 correctly describes - the two same-named
  no-argument methods have opposite scopes.)
- The example's explanation of the explicit rung is stale. `examples/Aws/Benzene.Examples.Aws/DependenciesBuilder.cs:83-88`
  says "the finder it registers is locked in via TryAddSingleton, so a later broader
  `.UseMessageHandlers(...)` scan in Configure() can't widen it" (repeated in
  `examples/Aws/Benzene.Examples.Aws.Tests/Integration/PublishOrderCreatedTest.cs:17-21`). The
  current finder is "built **lazily** over the **deduped union** of every `MessageHandlerCandidateTypes`
  registered by any `AddMessageHandlers(Type[])` call, so a second call ... is discovered too (it was
  silently dropped when the finder was captured from the first call only)"
  (`DI/Extensions.cs:195-215`). The framework fixed the trap; the example still teaches it.

**What the user experiences.** They cannot tell which of `AddBenzene`, `AddHttpMessageHandlers`,
`AddMessageHandlers(assembly)` are load-bearing; the one comment in the corpus that tries to explain
describes behaviour that no longer exists; and the troubleshooting section, followed literally,
removes every handler from the service.

**Proposed change.**
1. Pick Shape A as canonical for every AWS doc and template; where a snippet keeps
   `AddMessageHandlers(assembly)` say why in one clause ("to scope discovery to this assembly").
   Drop `.AddBenzene()` and `.AddHttpMessageHandlers()` from the three AWS templates.
2. Correct both troubleshooting sentences to "pass the handler's assembly to
   `AddMessageHandlers(...)`, or use the pipeline's `UseMessageHandlers()`, which scans every loaded
   assembly; the no-argument `AddMessageHandlers()` registers no discovery at all". Consider renaming
   the container-level no-argument overload (`AddMessageHandlerDispatch()`) so two methods with the
   same name no longer have opposite scopes.
3. Rewrite the `DependenciesBuilder.cs:83-88` comment (and the test summary) to the current
   union-finder behaviour, or delete it now that the trap is gone.

---

### 10. Example tests hand-roll SQS/SNS/API Gateway events and the test-host wrap that the framework already ships  [SHOULD-FIX - duplication x3, invisible-ladder]

**§4.1 clause:** duplicated plumbing; rule 4.

**Evidence.**

- Hand-rolled event builders: `examples/Aws/Benzene.Examples.Aws.Minimal.Tests/Helpers/AwsEventBuilder.cs`
  (85 lines), `examples/Aws/Benzene.Examples.Aws.Tests/Helpers/AwsEventBuilder.cs` (255 lines, of
  which lines 158-202 and 212-253 are unreachable code after a `return`),
  `examples/Aws/Benzene.Examples.Aws.Tests/Helpers/ApiGatewayProxyRequestBuilder.cs` (89 lines),
  plus `examples/Versioning/.../VersionedEventBuilder.cs` outside this territory. Three copies.
- The framework's rungs: `MessageBuilder.Create(topic, msg).AsSqs()` / `.AsSns()`
  (`src/Benzene.Aws.Lambda.Sqs.TestHelpers/MessageBuilderExtensions.cs:12`,
  `...Sns.TestHelpers/MessageBuilderExtensions.cs:12`), `HttpBuilder.Create(...).AsApiGatewayRequest()`,
  `host.SendSqsAsync(builder)`, `host.SendApiGatewayAsync(builder)`; the XML case is covered by the
  `AsSqs(ISerializer)` overload (`:17`). `Benzene.Examples.Aws.Minimal.Tests.csproj:24` references only
  `Benzene.Aws.Lambda.Core.TestHelpers`, none of the transport helpers.
- Two spellings of the same rung: `BuildAwsLambdaTestHost()` (the guide, §6) vs
  `new AwsLambdaBenzeneTestHost(... .BuildAwsLambdaHost())` (`AwsMinimalTests.cs:29`, all three
  templates' tests, `docs/testing-benzene.md:16-19` and `:62-65`,
  `docs/cookbooks/testing-lambda-functions.md:189-197`). Count: 13 wrap-form vs 2 fluent-form uses in
  examples/templates/docs.

**What the user experiences.** The "runnable version" of the guide does not use the guide's API,
so they cannot tell which is current; and the 85-line builder tells them the shipped helpers are not
good enough.

**Proposed change.** After finding 1 lands: delete both `AwsEventBuilder.cs` files and
`ApiGatewayProxyRequestBuilder.cs`, reference the transport `TestHelpers`, and standardise on
`BuildAwsLambdaTestHost()` everywhere, with one sentence in `testing-benzene.md` naming
`BuildAwsLambdaHost()` as the level below ("returns the raw `IAwsLambdaEntryPoint` when you need to
wrap it yourself").

Before (`AwsMinimalTests.cs:29` + `:62`):
```csharp
_host = new AwsLambdaBenzeneTestHost(BenzeneTestHost.Create<StartUp>().BuildAwsLambdaHost());
await _host.SendEventAsync(AwsEventBuilder.Sqs(Topic, AnOrder("ORD-3")));
```
After:
```csharp
_host = BenzeneTestHost.Create<StartUp>().BuildAwsLambdaTestHost();
await _host.SendSqsAsync(MessageBuilder.Create(Topic, AnOrder("ORD-3")));
```

---

### 11. Consuming SQS from a worker costs 15 lines of SDK plumbing and a different verb shape than consuming it from Lambda; the two SQS packages disagree on the wire-names override  [SHOULD-FIX - ceremony, cross-surface inconsistency]

**§4.1 clause:** routine capability with only an explicit form; and spec §4 "both sides ship the same
default and both expose the override".

**Evidence.**

- Lambda: `aws.UseSqs(sqs => sqs.UseMessageHandlers())` - one line
  (`src/Benzene.Aws.Lambda.Sqs/Extensions.cs:29`).
- Worker: `src/Benzene.Aws.Sqs/Extensions.cs:33`
  `UseSqs(this IBenzeneWorkerStartup app, SqsConsumerConfig sqsConsumerConfig, ISqsClientFactory sqsClientFactory, Action<...> action, Action<SqsConsumerOptions>? configure = null)`
  - the user constructs the config, the SDK client, and the factory. What that costs in the only
  example: `examples/K8sTransports/App/Startup.cs:45-59` (config 6 lines + endpoint/credential
  switch 5 lines) then `:80` `.UseSqs(sqsConfig, new SqsClientFactory(sqsClient), sqs => sqs.UseMessageHandlers())`.
  Kafka on the same line (`:81`) takes a config object and no client. `AddSqsConsumer` already
  registers `ISqsClientFactory` (`src/Benzene.Aws.Sqs/DependencyInjectionExtensions.cs:44`), so a DI
  path exists but no `UseSqs` overload uses it.
- Wire-names drift: the worker's topic getter consults `IBenzeneWireNames` when the key is left at
  default (`src/Benzene.Aws.Sqs/DependencyInjectionExtensions.cs:69-84`); the Lambda `AddSqs` uses
  the literal key (`src/Benzene.Aws.Lambda.Sqs/DependencyInjectionExtensions.cs:45-46`). Replacing
  `IBenzeneWireNames` therefore re-keys one SQS consumer and not the other.

**Proposed change.** Add `UseSqs(string queueUrl, Action<...> action, Action<SqsConsumerOptions>? configure = null)`
resolving `IAmazonSQS` from DI (defaulting per finding 7), keeping the factory overload as the
explicit rung and naming it in the new overload's xmldoc. Apply the `ResolveTopicAttributeKey`
pattern to `Benzene.Aws.Lambda.Sqs`/`.Sns` so the override behaves identically on both SQS surfaces.

---

### 12. The guide says `UseBenzeneInvocation()` does not reach SQS/SNS/Kafka; the code says every transport except API Gateway auto-wires it  [SHOULD-FIX - doc contradicts code; API Gateway is the odd one out]

**Evidence.**

- `docs/getting-started-aws.md` "Observability": "This flows into a single-request pipeline like API
  Gateway, but **not** into SQS/SNS/Kafka's per-message batch dispatch, since each message in a batch
  gets its own nested DI scope today." Repeated in Troubleshooting.
- `src/Benzene.Aws.Lambda.Sqs/Extensions.cs:34` `builder.UseBenzeneInvocation();` - likewise
  `Sns/Extensions.cs:35`, `EventBridge/Extensions.cs:33`, `S3/Extensions.cs:31`,
  `DynamoDb/Extensions.cs:24`, `Kinesis/Extensions.cs:46`, `Kafka/Extensions.cs:31`, each package's
  `CLAUDE.md` saying "No application code changes needed".
- `src/Benzene.Aws.Lambda.ApiGateway/Extensions.cs:22-26` - `UseApiGateway` builds via
  `app.Create<ApiGatewayContext>()` with **no** `UseBenzeneInvocation()`; it is the one transport
  that relies on the outer-pipeline call the guide tells users to add.

**Proposed change.** Rewrite the two doc paragraphs to the current truth; auto-wire
`UseBenzeneInvocation()` in `UseApiGateway`/`UseApiGatewayV2` for parity, or state in the
`UseApiGateway` xmldoc why it is the exception.

---

### 13. `aws-iam-permissions.md` does not cover half of what the guide sends readers there for  [SHOULD-FIX - honesty]

**Evidence.** `docs/getting-started-aws.md` (Kinesis section): "See AWS IAM Permissions for the
stream-consumer permissions the event source mapping needs (`kinesis:GetRecords`, ...)"; (DynamoDB
Streams section) describes an event source mapping. `docs/aws-iam-permissions.md`'s sections are:
baseline, API Gateway, SQS trigger, SNS trigger, S3 trigger, Kafka trigger, outbound clients,
`Benzene.Aws.Sqs` consumer. There is no Kinesis, DynamoDB Streams or EventBridge trigger section, and
nothing for the DynamoDB stores (`dynamodb:PutItem/UpdateItem/Query/TransactWriteItems/DescribeTable`)
or the S3 claim-check store (`s3:PutObject/GetObject`) that `AwsMesh/deploy/main.tf` has to grant
(five `aws_iam_role_policy` resources).

**Proposed change.** Add the missing sections; the SDK calls to cite are in
`DynamoDbOutboxStore`, `DynamoDbIdempotencyStore`, `DynamoDbEventStore`, `S3ClaimCheckStore`,
`DynamoDbHealthCheck`.

---

### 14. OpenTelemetry on Lambda is 137 lines of example plumbing  [SHOULD-FIX - missing shorthand]

**Evidence.** `examples/AwsMesh/Shared/LambdaTelemetry.cs` (137 lines: builds the tracer/meter
providers by hand because `AddOpenTelemetry()` needs an `IHost`, lines 17-38; force-flushes them in
a `TracingLambdaHost<T>` override, lines 128-137). The framework ships the hook
(`AwsLambdaHost.OnInvocationCompleteAsync`, `AwsLambdaHost.cs:94-103`) and the instrumentation
(`Benzene.OpenTelemetry.AddBenzeneInstrumentation`), but the composition - the part every OTel-on-Lambda
user must get right, including the X-Ray trace-id format (`:85-89`) and the freeze-time flush - lives
in an example. By contrast X-Ray via the SDK is one line (`AddXRayTracing()`,
`src/Benzene.Aws.Lambda.XRay/DependencyInjectionExtensions.cs:28`) - the ladder has a shorthand for
the less common path and not the recommended one.

**Proposed change** (proposal). A `Benzene.OpenTelemetry.Aws.Lambda` (or an addition to
`Benzene.Aws.Lambda.XRay`) shipping `AddBenzeneLambdaOpenTelemetry(serviceName, configure)` and an
`OpenTelemetryLambdaHost<TStartUp>` that flushes, whose xmldoc names `LambdaTelemetry`'s explicit
steps. Document both the OTel path and `AddXRayTracing()` in `getting-started-aws.md`'s
Observability section - today neither `Benzene.Aws.Lambda.XRay` nor `AddXRayTracing` appears in any
user guide (only `docs/reference/packages.md:264`).

---

### 15. IaC: the SQS/SNS templates ship none, the API Gateway template ships some, and the Terraform generator is used by no example and invisible from the guide  [SHOULD-FIX - inconsistency; POLISH on the generator]

**Evidence.**

- `templates/content/aws-sqs/README.md`: "This template doesn't ship a SAM `template.yaml` - an SQS
  trigger needs a queue ARN you'll supply"; `aws-apigateway/template.yaml` exists (28 lines).
  `docs/getting-started-aws.md` §7 already shows an in-stack queue (`MyQueue: Type: AWS::SQS::Queue`
  + `!GetAtt MyQueue.Arn`), so the stated reason does not hold.
- Proportionality: `examples/Aws/Benzene.Examples.Aws/template.yaml` is 122 lines for one function
  and three sources - proportionate. `examples/AwsMesh/deploy/main.tf` is 916 lines / 52 resources
  for seven functions, factored with `for_each = local.services` (`:275-276`) - proportionate for
  what it does, but 100% hand-maintained.
- `Benzene.CodeGen.Terraform` is referenced by zero files under `examples/` or `templates/` (grep).
  Its documented coverage (`docs/terraform.md`; `docs/cookbooks/handling-sqs-failures.md` §3 "It has
  no SQS queue, redrive policy, or DLQ resource generation of any kind") excludes SQS and API Gateway
  - i.e. the two sources in the flagship "one function, every source" service - which is why no
  example can use it. `docs/cookbooks/deploy-with-serverless-framework.md` nonetheless pitches it as
  the thing that "removes the `events:` <-> `.UseXxx(...)` sync seam entirely".

**Proposed change.** Ship `template.yaml` for the SQS/SNS templates mirroring §7. In
`serverless`/`terraform.md`, state the generator's coverage in one line up front (Lambda + IAM + SNS
subscription + EventBridge rule; not SQS, not API Gateway). Whether to extend the generator is a
product decision, not an ergonomics one.

---

### 16. `UseMessageHandlers(_ => { })` x10  [POLISH - incantation]

`examples/Aws/Benzene.Examples.Aws.Minimal/StartUp.cs:55,60,63,64,67` and its `README.md:27-32`
(15 repo-wide). The zero-argument `UseMessageHandlers()` exists
(`src/Benzene.Core.MessageHandlers/Extensions.cs:58`) and is what the templates, `Cloudflare/StartUp.cs:45`
and the guide use. "What does `_ => { }` do?" is the first question a reader asks of the minimal
example; the answer is "nothing".

---

### 17. Verb-shape drift across the eight `UseXxx` and their test helpers  [POLISH - consistency]

| Surface | Shape | Outlier? |
|---|---|---|
| `UseSqs`, `UseSns` | `(action, Action<TOptions>? configure, string topicAttributeKey)` | - |
| `UseEventBridge`, `UseS3`, `UseKafka` | `(action, Action<TOptions>? configure)` | - |
| `UseKinesisStream` | `(action, KinesisStreamOptions? options)` - an **instance**, `src/Benzene.Aws.Lambda.Kinesis/Extensions.cs:38-41` | yes |
| `UseDynamoDb`, `UseApiGateway`, `UseApiGatewayV2` | `(action)` - no options (DynamoDB documented as deliberate, DS5) | API Gateway undocumented |
| `AsSqs(numberOfMessages)`, `AsKinesis(numberOfRecords, partitionKey)`, `AsDynamoDb(numberOfRecords)` | count parameter | `AsSns()` has none |
| `AsSqs/AsSns/AsS3/AsEventBridge/AsDynamoDb/AsKinesis` | `As<Transport>` | `AsAwsKafkaEvent` |

Fix: `UseKinesisStream(action, Action<KinesisStreamOptions>? configure = null)` alongside the
existing overload; `AsSns(int numberOfRecords = 1)`; rename or alias `AsKafka`.

---

### 18. Three conventions with no start-up check, and an unrecognised-event message that names nothing  [POLISH - late failure]

- `UseHttpBridgeV2()` + `UseApiGatewayV2(...)` both registered: "never both"
  (`src/Benzene.Aws.Lambda.HttpBridge/Extensions.cs:18-21`, cookbook) - first registered claims
  silently; no check.
- `app.UseAwsLambda(aws => { })` with no routers: the outer pipeline has zero constructors so
  `TerminalMiddlewareStartUpCheck` skips it by design (`TerminalMiddlewareStartUpCheck.cs:48-51`);
  every invocation then throws `AwsLambdaEntryPoint.cs:53`. A host-level check ("no event-source
  router mounted") is cheap.
- `AwsLambdaEntryPoint.cs:53` "The event type has not been recognized ..." - rule 3 asks the failure
  to name what was looked for: list the mounted routers and the top-level keys of the payload it saw.

---

### 19. Stale references a stranger will trip on  [POLISH - honesty]

- `docs/getting-started-aws.md` "Bare Metal Entry Point": `new AwsEventStreamPipelineBuilder(...)` -
  no such type in `src/` (grep; the example it mirrors, `examples/Aws/Benzene.Examples.Aws/BareMetalLambdaEntryPoint.cs:28`,
  uses `new MiddlewarePipelineBuilder<AwsEventStreamContext>(...)`). The block is not tagged
  `<!-- compile -->`, so `DocSnippetsCompileTest` does not catch it.
- `src/Benzene.Aws.Lambda.Core/InlineAwsLambdaStartUp.cs:12,17` xmldoc: "alternative to declaring an
  `AwsLambdaStartUp` subclass ... prefer deriving from `AwsLambdaStartUp`" - `AGENTS.md`/`examples/CLAUDE.md`
  record that type as removed.
- `examples/Aws/Benzene.Examples.Aws/template.yaml:38` "Benzene itself doesn't have an X-Ray-specific
  package" - `Benzene.Aws.Lambda.XRay` exists.
- `docs/getting-started-aws.md:594` / `docs/cookbooks/testing-lambda-functions.md:357`: the
  no-argument `AddMessageHandlers()` "scans the calling assembly" - it registers no discovery at
  all (`src/Benzene.Core.MessageHandlers/DI/Extensions.cs:165-173`); see finding 9.

---

### 20. Shipped rungs that no user-facing doc names  [POLISH - invisible-ladder]

`Benzene.Aws.Lambda.TestPayloads` / `UseAwsTestPayloads()` (zero mentions in `docs/` or `examples/`),
`Benzene.Aws.Lambda.XRay` / `AddXRayTracing()` (packages.md only), `Benzene.Aws.Lambda.Hosting`
(finding 5), `UseApiGatewayV2` (not in getting-started's event-source list; the SAM snippet deploys an
`HttpApi`, whose default is payload 2.0 - worth one sentence on which of `UseApiGateway`/`UseApiGatewayV2`
that needs).

---

### 21. `examples/Aws` carries 115 lines of hand-rolled Serilog formatting and an unexplained incantation  [POLISH - plumbing]

`examples/Aws/Benzene.Examples.Aws/Logging/CustomJsonFormatter.cs` (85 lines) +
`Logging/Extensions.cs` (30 lines) re-implement JSON console logging; `AwsMesh` gets the same with
`AddJsonConsole()`. `DependenciesBuilder.cs:52` `JsonConvert.DeserializeObject("{}");` has no
comment. Per §4.1 either delete or mark "deliberate demonstration of a custom log formatter".

---

### 22. Small ceremonies in `Outbox` and `Saga`  [POLISH]

- `examples/Outbox/Benzene.Example.Outbox/Program.cs:37` and `:124`
  `services.AddTransient<ISerializer, JsonSerializer>();` - `AddOutbox()` should `TryAdd` its own
  serializer default; the user should not have to know the engine needs one.
- `examples/Saga/Benzene.Example.Saga/SignupSaga.cs:28,34,43,51` - four copies of
  `ArgumentNullException.ThrowIfNull(x); return api.DeleteXAsync(x.Id);` inside `Compensate`, with a
  comment (17-21) explaining that compensation "only ever runs for a step that already succeeded, so
  the forward payload is always present". A `Compensate(Func<SagaContext, T, Task<IBenzeneResult>>)`
  overload with a non-nullable payload removes all four and keeps the nullable overload as the
  explicit form.

---

### 23. `Cloudflare/StartUp.cs` hand-registers the `/livez` route  [POLISH - verify]

`examples/Cloudflare/Benzene.Example.Cloudflare/StartUp.cs:37-38` registers
`new HttpEndpointDefinition("GET", "/livez", Constants.DefaultLivenessTopic)` and then calls
`UseLivenessCheck(...)` (`:44`). `Benzene.Aws.Lambda.ApiGateway`'s `UseLivenessCheck` defaults to
`GET /livez` with no separate definition (`src/Benzene.Aws.Lambda.ApiGateway/CLAUDE.md`, "Health
checks"). If `Benzene.Http`'s `UseLivenessCheck` does not register the route itself, that is the
same capability costing two lines on one host and zero on another; I did not read `Benzene.Http`'s
implementation (outside territory) - flagging for the ASP.NET reviewer.

---

## Boilerplate ledger (examples in territory)

Method: every non-blank, non-comment, non-`using` line classified as **domain** (the thing the
example is about), **intent** (what it handles / talks to / needs), or **plumbing** (everything
else), with the plumbing named and categorised as *missing shorthand* (framework bug) or *deliberate
demonstration* (must say so in a comment).

| File | domain | intent | plumbing | The plumbing, and which category |
|---|---|---|---|---|
| `Aws.Minimal/StartUp.cs` | 0 | 12 | 7 | `GetConfiguration` override identical to default (4) - *missing nothing; delete*. Explicit envelope pipeline `aws.Create<BenzeneMessageContext>()...; aws.UseBenzeneMessage(pipeline)` (3) where the `UseBenzeneMessage(Action)` shorthand exists - *deliberate demonstration, uncommented*. Five `_ => { }` counted as intent with a polish note. |
| `Aws.Minimal/PlaceOrderMessageHandler.cs` | 28 | 2 | 0 | Clean. |
| `Aws.Minimal/ProcessedLog.cs` | 0 | 0 | 12 | Test seam so fire-and-forget sources can be asserted - *deliberate, commented*. |
| `Aws.Minimal.Tests/AwsMinimalTests.cs` | 0 | 40 | 3 | `new AwsLambdaBenzeneTestHost(...BuildAwsLambdaHost())` where `BuildAwsLambdaTestHost()` exists (1); `InMemoryProcessedLog.Clear()` + `ProcessedEntries()` (2) - test seam. |
| `Aws.Minimal.Tests/Helpers/AwsEventBuilder.cs` | 0 | 0 | 62 | Hand-built SQS/SNS/API Gateway/EventBridge events - *missing shorthand for EventBridge (finding 1), existing shorthand unused for the other three (finding 10)*. |
| `Aws/StartUp.cs` | 19 | 31 | 31 | `GetConfiguration` with dead `SetBasePath` (7); prelude repeated 6x (24) - *missing shorthand (finding 6)*. Authorizer block (19) is domain. |
| `Aws/DependenciesBuilder.cs` | 4 | 17 | 27 | `AWSOptions`/region/service-URL client construction (20) - *missing shorthand (finding 7)*; `JsonConvert.DeserializeObject("{}")` (1) - unexplained; `AddLogging(... AddSerilog)` (1) - finding 3; `IProcessTimerFactory` composite (4) - *deliberate, commented*; `AddActivityPerMiddleware` comment says why (intent). |
| `Aws/Logging/*.cs` | 0 | 0 | 95 | Custom Serilog JSON formatter + LogContext middleware - *neither category stated (finding 21)*. |
| `Aws/BareMetalLambdaEntryPoint.cs` | 0 | 5 | 20 | The explicit form of `AwsLambdaHost` - *deliberate demonstration; the class name says so, a comment should too and should name `AwsLambdaHost<T>` as the level above*. |
| `Aws/PublishOrderCreatedMessageHandler.cs` | 14 | 3 | 0 | Clean. |
| `Aws.Tests/Helpers/AwsEventBuilder.cs` | 0 | 0 | 214 | Hand-built SQS/SNS events incl. 88 unreachable lines - *existing shorthand unused (finding 10)*. |
| `Aws.Tests/Helpers/ApiGatewayProxyRequestBuilder.cs` | 0 | 0 | 70 | Hand-built API Gateway request - *existing shorthand `HttpBuilder...AsApiGatewayRequest()` unused*. |
| `AwsMesh/Shared/MeshServiceWiring.cs` | 0 | 38 | 190 | The whole file is the seam six services share: logging, OTel wiring call, client registration x3, outbound route assembly, per-transport `Observe()` prelude x5, three feature flags - *missing shorthand (findings 3, 6, 7)*. |
| `AwsMesh/Shared/LambdaTelemetry.cs` | 0 | 6 | 96 | Hand-built OTel providers + flush host - *missing shorthand (finding 14)*. |
| `AwsMesh/Shared/OutboundSend.cs` | 0 | 26 | 0 | A declaration type - this is what intent looks like; arguably belongs in the framework. |
| `AwsMesh/Orders/Startup.cs` | 0 | 24 | 12 | Two env-var constants (2), `IAmazonDynamoDB`/`IAmazonS3` registration (2), env-var reads with fallbacks (2), store registrations (6, intent but each needing the client line) - *finding 7*. |
| `AwsMesh/Payments/Startup.cs` | 0 | 16 | 8 | Same shape as Orders - *finding 7*. |
| `AwsMesh/{Shipping,Inventory,Notifications,Analytics}/Startup.cs` | 0 | 10-13 each | 0 | Clean - because `MeshServiceWiring` absorbed the plumbing. |
| `AwsMesh/*/Program.cs` (x6 service side) | 0 | 1 | 3 each | Bootstrap loop - *existing shorthand unused (finding 5)*. |
| `AwsMesh/Orders/Handlers/OutboxHandlers.cs` | 20 | 8 | 0 | Clean. |
| `Outbox/Program.cs` | 120 | 30 | 14 | `AddTransient<ISerializer, JsonSerializer>()` x2 - *missing default*; `new MicrosoftServiceResolverAdapter(...)` x3 and manual dispatch scope (lines 92-104) - *deliberate, commented*. |
| `Saga/*.cs` | 95 | 12 | 8 | `ThrowIfNull` x4 in `Compensate` - *missing overload (finding 22)*. |
| `Cloudflare/Program.cs` | 0 | 1 | 0 | Exemplary: one line, comment names the explicit calls it composes. |
| `Cloudflare/StartUp.cs` | 0 | 9 | 9 | `GetConfiguration` with dead `SetBasePath` (7) - finding 8; hand-registered `/livez` definition (2) - finding 23. |
| `templates/content/aws-{apigateway,sqs,sns}/StartUp.cs` | 0 | 8-10 each | 12 each | `GetConfiguration` (7), `AddLogging(AddConsole)` (1), `.AddBenzene()` (1), `.AddHttpMessageHandlers()` (apigateway, 1), prelude pair (2) - findings 3, 6, 8, 9. |

Duplication sweep totals (examples + templates, territory): `GetConfiguration` override x6,
`AddLogging(...)` x5, Lambda bootstrap x7, hand-rolled SQS/SNS event builders x3, wrap-form test host
x13 (vs fluent x2), `.AddBenzene()` x14 repo-wide, `UseBenzeneEnrichment().UseLogResult` x12
repo-wide, per-transport prelude x6 within one file, hand-registered AWS SDK clients x7.

---

## Capability -> explicit form -> shorthand -> documented?

"Documented?" answers rule 4: does the shorthand's documentation name the explicit form it composes?

| Capability | Explicit form (public) | Shorthand | Shorthand names its explicit form? |
|---|---|---|---|
| Host a Benzene pipeline as a Lambda | `new AwsLambdaEntryPoint(pipeline.Build(), factory)` + your own `FunctionHandler` (`examples/Aws/BareMetalLambdaEntryPoint.cs`) | `class Function : AwsLambdaHost<StartUp>` | Yes - guide "Bare Metal Entry Point" (but see finding 19: the snippet names a type that does not exist) |
| Run the custom-runtime loop | `HandlerWrapper` + `LambdaBootstrap` (3 lines) | `AwsLambdaBootstrap.RunAsync<StartUp>()` / `RunAsync(entryPoint)` | Yes in xmldoc; **absent from the guide**; examples use the explicit form uncommented (finding 5) |
| Serve API Gateway v1 / v2 | `AddApiGateway()` + `Create<ApiGatewayContext>()` + `new ApiGatewayLambdaHandler(pipeline, resolver)` | `UseApiGateway(action)` / `UseApiGatewayV2(action)` | Partly - `AddApiGateway` remarks say "called automatically by UseApiGateway"; v2 not in the guide |
| Consume SQS / SNS / EventBridge / S3 / DynamoDB Streams / Kafka | `AddXxx()` + `CreateMiddlewarePipeline<TContext>` + `new XxxLambdaHandler(new XxxApplication(pipeline, options), resolver)` | `UseXxx(action, configure?)` | Reverse direction only (`AddXxx` -> `UseXxx`); `UseXxx` xmldoc does not name the handler/application it composes |
| Consume Kinesis as a stream | same, with `KinesisStreamApplication` | `UseKinesisStream(action, options)` | Yes, guide + CLAUDE.md; options shape is the outlier (finding 17) |
| Several event sources in one function | chain `Use*` on the outer pipeline | same (it *is* the explicit form) | Yes |
| ASP.NET Core serving HTTP on the same Lambda | `AddSingleton<IServer, LambdaServer>` + bridge + `UseHttpBridgeV2()` + `await app.StartAsync()` (HttpBridge CLAUDE.md) | `AddBenzeneAwsLambdaHosting(events => ...)` | Yes - cookbook "Under the hood" + CLAUDE.md |
| Discover handlers | `AddMessageHandlers(types)` / `AddMessageHandler<T,...>(topic)` | `UseMessageHandlers()` (AppDomain scan) | Yes, guide §4; but the interaction is unchecked (finding 9) |
| Configuration source | `override GetConfiguration()` | virtual default (env vars) | Yes, `BenzeneStartUp` xmldoc + guide; examples contradict (finding 8) |
| Logging provider | `services.AddLogging(x => x.AddConsole())` | **none** - host registers no provider | Only as a template comment (finding 3) |
| Observability prelude across transports | repeat per `UseXxx` | **none** (`MeshServiceWiring.Observe<T>` is user-written) | n/a (finding 6) |
| Invocation identity | `UseBenzeneInvocation()` on the outer pipeline | auto-wired per record by every transport except API Gateway | Doc says the opposite (finding 12) |
| Outbox on DynamoDB | `AddSingleton<IAmazonDynamoDB>` + `AddOutbox` + `AddDynamoDbOutboxStore` + `AddDynamoDbOutboxTransaction` + `UseOutbox()` on the route + a `{table}:INSERT` handler + a sweep handler | **none** for the client; the relay pair is hand-written per service (`OutboxHandlers.cs`) | Yes - cookbook + `Benzene.Outbox.DynamoDb/CLAUDE.md` show every step |
| Idempotency on DynamoDB | `AddSingleton<IAmazonDynamoDB>` + `AddDynamoDbIdempotencyStore` + `UseIdempotency<T>()` | **none** for the client | Cookbook shows Redis/in-memory; DynamoDB store appears only in `claim-check.md` and CLAUDE.md |
| Claim check on S3 | `AddSingleton<IAmazonS3>` + `AddS3ClaimCheckStore` + `UseClaimCheck()` / `UseClaimCheck<T>()` | **none** for the client | Yes - `docs/claim-check.md` |
| Event sourcing on DynamoDB | `AddSingleton<IAmazonDynamoDB>` + `AddDynamoDbEventStore` | **none** for the client | CLAUDE.md only |
| DynamoDB health check | `AddSingleton<IAmazonDynamoDB>` + `AddDynamoDbHealthCheck(table)` | **none** for the client | CLAUDE.md only |
| Tracing to X-Ray (SDK) | register `IMiddlewareWrapper` yourself | `AddXRayTracing()` | xmldoc names `AddActivityPerMiddleware` as its twin; **no user guide** (finding 20) |
| Tracing via OpenTelemetry on Lambda | `LambdaTelemetry` + `TracingLambdaHost` (137 lines, example) | **none** | n/a (finding 14) |
| Test a StartUp in memory | `new AwsLambdaBenzeneTestHost(BenzeneTestHost.Create<T>().BuildAwsLambdaHost())` | `BenzeneTestHost.Create<T>().BuildAwsLambdaTestHost()` | Yes, xmldoc names `BuildAwsLambdaHost`; docs split across both spellings (finding 10) |
| Send a transport event in a test | hand-built `SQSEvent`/`SNSEvent`/... | `MessageBuilder...AsSqs()` + `SendSqsAsync`; `AsSns()`, `AsEventBridge()`, `AsS3()`, `AsDynamoDb()`, `AsKinesis()` | `Send*` exists for SQS/API Gateway only; EventBridge/DynamoDB/Kinesis cannot go through the host (finding 1) |
| Self-serve test payloads from a deployed function | `UseTestPayloads()` + `AddAwsTestPayloadDressers()` | `UseAwsTestPayloads()` | CLAUDE.md only (finding 20) |
| Consume SQS from a worker | `UseSqs(config, ISqsClientFactory, action)` | **none** | Yes in `getting-started-kubernetes.md`, as the explicit form (finding 11) |
| Terraform for a Lambda | hand-written `.tf` | `TerraformLambdaBuilder` / `TerraformEventBridgeRuleBuilder` | `docs/terraform.md`; not linked from the guide's deploy step; unused by every example (finding 15) |
| Least-privilege IAM | hand-written policy | `docs/aws-iam-permissions.md` | Incomplete (finding 13) |

---

## Ceremony parity across the AWS surface (Q5)

Same verb, same builder shape, same options delegate for SQS, SNS, EventBridge, S3, Kafka:
`UseXxx(inner => inner.UseMessageHandlers(), o => ...)`. DynamoDB Streams drops the options
deliberately and documents why. Kinesis takes an options instance. API Gateway takes no options and
does not auto-wire invocation identity. Test helpers: `Send*Async` for 2 of 8, `As*()` for 8 of 8,
of which 3 cannot be sent through the host. Templates: IaC for 1 of 3. Worker-side SQS costs ~15
lines where Lambda-side SQS costs 1. Everything in this paragraph has a file:line above.

---

## What is genuinely good

- **The start-up-check phase is §4.1 rule 3 done properly.** `AwsLambdaHost.cs:52-61` warms up and
  then runs every `IStartUpCheck`; `BuildAwsLambdaHost` runs the same checks
  (`BenzeneTestHostExtensions.cs:35` `.WithStartUpChecks()`), so a wiring bug is a red unit test;
  `InlineAwsLambdaStartUp.Build` does too. `TerminalMiddlewareStartUpCheck`'s remarks
  (`UseSqs(sqs => { })` "compiles, composes, deploys, and dead-letters the entire queue") and
  `PipelineResolutionStartUpCheck`'s "innermost exception is the one the developer has to register"
  are exactly the failure-naming the spec asks for, and `BenzeneStartUpCheckException` tells the user
  how to soften the checks in the message itself.
- **`BenzeneStartUp.GetConfiguration()`'s xmldoc** (`BenzeneStartUp.cs:17-28`) is a worked §4.1
  argument in the source: counted the copies, made the default, kept the override one line away.
- **`docs/getting-started-aws.md` is executed, not just compiled.** `AwsQuickstartRunsTest` builds
  the guide's `StartUp` from the markdown and sends an API Gateway request and an SQS message
  through it in CI - the remarks even record the bug that motivated it.
- **`AwsLambdaBootstrap`'s xmldoc shows the three lines it collapses**, and
  **`Cloudflare/Program.cs`'s one-line comment names the explicit calls `BenzeneWebHost` composes** -
  both are rule 4 in its ideal form; the rest of the surface should copy them.
- **The HTTP bridge is a port with a shorthand on top, and the CLAUDE.md shows the hand composition
  and its two footguns.** `AddBenzeneAwsLambdaHosting` is a real, one-level-down-able shorthand.
- **`TryAdd` ordering discipline is explicit on both sides**: `AddSqs` comments
  (`DependencyInjectionExtensions.cs:43-44`) and `InlineAwsLambdaStartUp.cs:62-66` (#106) both
  state that a user registration made earlier wins - spec §4's "both sides overridable" as code.
- **Per-record `UseBenzeneInvocation()` auto-wiring across seven transports** removed an application
  code change from every service (only the doc lags - finding 12).
- **Safe-by-default settlement is stated per transport with the opt-out named next to it**
  (SNS/S3/EventBridge `RaiseOnFailureStatus`, SQS `BatchFailureMode`, Kinesis "throw or withhold the
  checkpoint") - the user can predict what a failure result does on every source.
- **`Benzene.Aws.Lambda` as a references-only umbrella** makes the getting-started `dotnet add package`
  a single line without hiding the narrower packages.
- **`examples/K8sTransports`** shows three inbound transports on one `UseWorker` with the same
  `UseMessageHandlers()` on each - the "write once, host anywhere" promise as a diff, not a claim.

---

## Notes for the parent reviewer

- Prior AWS rounds (`work/review-round17-aws-deep-2026-08.md`, `review-round18-aws-2026-08.md`)
  focused on correctness (checkpointing, settlement, pagination); nothing above overlaps them.
- The mesh Lambda (`examples/AwsMesh/Mesh`) and `examples/Versioning` were out of scope here but
  carry the same `Program.cs` bootstrap and `UseMessageHandlers(_ => { })` patterns; the counts above
  include them only where stated.
- Findings 1, 3, 5, 6, 7, 11, 14 are framework changes and belong to this port's ergonomics
  champion; finding 2 is a new start-up check; 4, 9, 12, 13, 19, 20 are documentation (9 also
  strips two template lines); 8, 10, 16, 21, 22 are example/template strips that need no library
  change once the framework rungs exist.
- One claim I made in an earlier draft of finding 9 was wrong and has been removed: the
  `examples/Aws` comment about a "locked" handler finder describes superseded behaviour
  (`DI/Extensions.cs:195-215` builds a union), so no "excluded handlers" start-up check is needed.
