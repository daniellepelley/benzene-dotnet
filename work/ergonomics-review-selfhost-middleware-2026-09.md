# Ergonomics review: self-hosted transports, Kubernetes, cross-cutting middleware (2026-09-02)

**Reviewer role:** cross-language ergonomics champion, enforcing `docs/specification/design-principles.md`
§4.1 "The shorthand ladder" (spec repo). **Commit reviewed:** `f3f1be5`.
**Method:** read-only. No `dotnet` SDK in the sandbox, so every "compiles"/"fails at start-up" claim
below is **trace-only** (signature read against the call site), never executed. Nothing in `src/`,
`docs/`, `examples/` or `templates/` was modified.

## Executive verdict

- **go-live blockers: 2** - both are documentation that tells a user a capability does not exist when the
  package ships it (§4.1: "an escape hatch nobody can find is, from the user's seat, indistinguishable
  from no escape hatch").
- **should-fix: 15** - ladder gaps (missing shorthands, one explicit-only rung that skips start-up
  checks), start-up checks that name *what* but not *what to add*, transport-family asymmetries between
  Kafka / RabbitMQ / Service Bus / SQS / Event Hub / AspNet, and duplicated plumbing in examples and
  templates (counts below).
- **polish: 10** - undocumented public rungs, verb inconsistencies, redundant lines in examples, stale
  guide text.
- The framework's ladder is structurally sound: every routine capability has an explicit form, most have a
  shorthand, and the shorthands are composed from public API (verified for `BenzeneHost`, `UseKafka`,
  `UseRabbitMq`, `UseAspNet`, `UseCorrelationId`, `UseHealthCheck`). The problems are at the edges:
  parity, hints, and docs that lag the code.
- Verdict: **NEEDS CHANGES** for the two doc blockers (cheap, one afternoon); the rest is the backlog the
  count argues for.

---

## Findings, by severity

### BLOCKER

#### 1. Docs deny the worker test hosts that ship and that the templates already use  [invisible-ladder]

- **§4.1 clause:** "The ladder MUST be visible from the top ... the user will conclude the framework
  cannot do the thing."
- **Evidence:**
  - `docs/getting-started-kafka.md:221-223`:
    > There's no `BenzeneTestHost` support for Kafka — `Benzene.Testing` has no `Send*Async` extension
    > for it ... The pattern used in `examples/Kafka/Benzene.Examples.Kafka.Test` instead runs the worker
    > for real against a live broker
  - `docs/getting-started-worker.md:251-253` ("There's no `BenzeneTestHost.Build*`/`Send*Async` helper
    for a worker-only `StartUp`") and `:576-581` (Part B "Testing": "there's no `BenzeneTestHost`
    shortcut — drive it ... against a live broker").
  - `docs/testing-benzene.md:98-111` ("Worker / generic host ... there's no `Send*Async` to call. Build
    the real host").
  - But: `src/Benzene.Kafka.Core.TestHelpers/BenzeneTestHostExtensions.cs:27`
    `BuildKafkaWorkerHost<TStartUp, TKey, TValue>(this BenzeneTestHostBuilder<TStartUp>)`,
    `src/Benzene.RabbitMq.TestHelpers/BenzeneTestHostExtensions.cs:23` `BuildRabbitMqWorkerHost<TStartUp>`,
    plus `Benzene.Azure.ServiceBus.TestHelpers` / `Benzene.Azure.EventHub.TestHelpers` equivalents - all
    running `.WithStartUpChecks()` (rule 3 honoured). The templates use them:
    `templates/content/kafka-worker/BenzeneStarter.Tests/HelloWorldMessageHandlerTests.cs:23-26`,
    `templates/content/rabbitmq-worker/BenzeneStarter.Tests/HelloWorldMessageHandlerTests.cs:21-24`.
  - `grep -rn "BuildKafkaWorkerHost\|BuildRabbitMqWorkerHost" docs/` returns **zero** hits.
- **What the user experiences:** reads the guide, concludes a Kafka/RabbitMQ handler can only be tested
  against Docker, and hand-rolls `examples/Kafka/...Test/Helpers/{KafkaSetUp,WorkerSetUp,ResultPoller}.cs`
  (three helper files, a live broker, a poll loop) - the exact outcome §4.1 predicts.
- **Fix (docs only):** replace the three "no test host" paragraphs with the template's shape and add a
  "Worker (Kafka / RabbitMQ / Service Bus / Event Hub)" section to `testing-benzene.md`:
  ```csharp
  // before (getting-started-kafka.md §1.7): "run the worker for real against a live broker"
  // after
  using var host = BenzeneTestHost.Create<StartUp>()
      .WithServices(s => s.AddSingleton<IGreeter>(spy))
      .BuildKafkaWorkerHost<StartUp, Ignore, string>();       // no broker; start-up checks run
  await host.HandleAsync(MessageBuilder.Create("hello_world", msg).AsKafkaBenzeneMessage());
  ```
  Keep the live-broker section as "integration tier", not as the only option.

#### 2. `common-middleware.md` says W3C trace continuation is HTTP-only; code, `monitoring.md` and the cookbook say the opposite  [invisible-ladder]

- **§4.1 clause:** ladder visible from the top; a reference page is where a Kafka/RabbitMQ user checks.
- **Evidence:**
  - `docs/common-middleware.md:143-146`:
    > Only wired for HTTP-based transports today (ASP.NET Core, Azure Functions' ASP.NET-style trigger,
    > API Gateway) — SQS/SNS/Kafka/Event Hub inbound extraction is not yet implemented.
  - `src/Benzene.Diagnostics/W3CTraceContextExtensions.cs:11-27` - generic over `TContext`, resolves only
    `IMessageHeadersGetter<TContext>`, which `AddKafka<TKey,TValue>()`
    (`src/Benzene.Kafka.Core/DependencyInjectionExtensions.cs:24`) and `AddRabbitMq()`
    (`src/Benzene.RabbitMq/DependencyInjectionExtensions.cs:53`) both register.
  - `docs/monitoring.md:160-167` ("works on ... SQS, SNS, Kafka (AWS Lambda, Azure Functions, and the
    self-hosted worker), and Event Hub"), `docs/correlation-ids.md:28-31`, and
    `docs/cookbooks/distributed-tracing-opentelemetry.md:264-273` all say it works on the async transports.
- **What the user experiences:** a self-hosted Kafka/RabbitMQ user reading the reference page drops
  `UseW3CTraceContext()` from the worker pipeline and loses trace continuity; or hand-rolls header parsing.
- **Fix:** delete the sentence at `common-middleware.md:143-144` and replace with the `monitoring.md:160-167`
  wording (or a link to it). One paragraph.

### SHOULD-FIX

#### 3. "Expose health for Kubernetes probes" on a self-hosted worker has no shorthand and no documented path  [ceremony]

- **§4.1 clause:** "Every capability a service needs *routinely* MUST have a shorthand"; "the ladder MUST
  be visible from the top".
- **Evidence:**
  - `UseKafka(..., healthCheck: true)` / `UseRabbitMq(..., healthCheck: true)` auto-register a dependency
    check "on the deep `healthcheck` layer" (`src/Benzene.Kafka.Core/Extensions.cs:41-44`,
    `src/Benzene.RabbitMq/Extensions.cs:39-42`). A pure worker has **no surface that answers that topic**:
    nothing in `getting-started-kafka.md`, `getting-started-rabbitmq.md` or `getting-started-worker.md`
    adds `UseHealthCheck`/`UseAspNet`, so the auto-wired check is unreachable in the guides' shape.
  - `docs/kubernetes-health-checks.md:142-170` (the only Kubernetes-probe recipe for ASP.NET) needs two
    `IHttpEndpointDefinition` singletons in `ConfigureServices` **plus** `UseLivenessCheck`/`UseReadinessCheck`
    in `Configure` - ~8 lines split across two methods - and is written for `UseHttp`, not the
    `UseWorker(w => w.UseAspNet(...))` shape the Kubernetes guide itself teaches. `grep` finds no doc showing
    health on the `UseAspNet` worker.
  - `Benzene.Aws.Lambda.ApiGateway` already ships the path defaults
    (`src/Benzene.Aws.Lambda.ApiGateway/DependencyInjectionExtensions.cs:226` `UseLivenessCheck("/livez", ...)`,
    `:275` `UseReadinessCheck("/readyz", ...)`); `Benzene.AspNet.Core` has no equivalent.
  - The runnable Kubernetes example ships a bare TCP probe: `examples/K8sTransports/k8s/app.yaml:34-36`
    `readinessProbe: tcpSocket: { port: 8080 }`; the guide admits it at
    `docs/getting-started-kubernetes.md:333-335` and points at the page above.
- **What the user experiences:** the "Benzene on Kubernetes" guide ends with a probe that reflects nothing,
  and the page it defers to does not cover the guide's own hosting shape. They will conclude the two
  `IHttpEndpointDefinition` lines are "just the setup you have to write".
- **Fix (proposal, not merge):** an AspNet-side path overload composed from the existing pieces, and use
  it in `examples/K8sTransports/App/Startup.cs` + `k8s/app.yaml`:
  ```csharp
  // before (kubernetes-health-checks.md:156-168): 2 singletons in ConfigureServices + 2 calls in Configure
  // after
  .UseAspNet(asp => asp
      .UseLivenessCheck()                                   // GET /livez  (default path, as ApiGateway)
      .UseReadinessCheck(x => x.AddShutdownReadinessCheck(shutdown))   // GET /readyz
      .UseHealthCheck(x => x.AddKafkaHealthCheck(kafkaConfig))         // deep layer, monitoring
      .UseMessageHandlers())
  ```
  Implementation is a composition the user could write today (`IHttpEndpointDefinition` +
  `UseLivenessCheck`), so it passes rule 2. Document the rung below it (the two registrations) beside it.

#### 4. `InlineSelfHostedStartUp` / `BuildHostedService` - the rung below `UseBenzene<TStartUp>()` - skips the start-up checks  [ladder-broken]

- **§4.1 clause:** "The price of a convention is a start-up check ... A convention that can first fail on
  the message path has not paid for itself."
- **Evidence:**
  - `src/Benzene.HostedService/HostBuilderExtensions.cs:31-33` runs `serviceResolverFactory.RunStartUpChecks()`.
  - `src/Benzene.SelfHost/InlineSelfHostedStartUp.cs:29-45` (`Build()`) and
    `src/Benzene.HostedService/BenzeneHostedServiceStartup.cs:136-142` (`BuildHostedService`) never call it;
    `grep RunStartUpChecks src/Benzene.SelfHost src/Benzene.HostedService` -> only `HostBuilderExtensions.cs`.
  - `docs/getting-started-worker.md:272-292` promotes this rung ("a fast unit test of just the worker
    wiring"); `docs/diagnosing-failures.md:18-20` promises "Every host runs the checks".
- **What the user experiences:** the "unit test of the wiring" cannot catch a wiring mistake; a pipeline
  missing `UseMessageHandlers()` passes the inline test and dead-letters in production.
- **Fix:** `InlineSelfHostedStartUp.Build()`: `return app.Create(serviceResolverFactory.WithStartUpChecks());`
  (the same one-liner the TestHelpers use). One line; no public surface change.

#### 5. Missing-registration failures for the middleware families name what was looked for, not what to add  [magic]

- **§4.1 clause:** "the failure names what was looked for, where, and what to add."
- **Evidence:**
  - Rule 3 is half-met: `PipelineResolutionStartUpCheck` constructs every middleware at start-up
    (`src/Benzene.Core.MessageHandlers/StartUpChecks/PipelineResolutionStartUpCheck.cs:16-31`), so
    `UseIdempotency()` without a store (`src/Benzene.Idempotency/Extensions.cs:28`
    `resolver.GetService<IIdempotencyStore>()`), `UseOutbox()` without `AddOutbox()`
    (`src/Benzene.Outbox/Extensions.cs:19` `resolver.GetService<OutboxOptions>()`), and `UseClaimCheck()`
    without a store (`src/Benzene.ClaimCheck/Extensions.cs:18,31`) all fail **before the first message**. Good.
  - The message is `"Unable to resolve type Benzene.Outbox.OutboxOptions"` +
    `RegistrationErrorHandler.Describe` hint (`src/Benzene.Microsoft.Dependencies/MicrosoftServiceResolverAdapter.cs:59-62`).
    The hint table is fed by `RegistrationsBase` implementers, and **none** of `Benzene.Kafka.Core`,
    `Benzene.RabbitMq`, `Benzene.Idempotency`, `Benzene.Outbox`, `Benzene.ClaimCheck`, `Benzene.Auth.*`,
    `Benzene.Cache.Core`, `Benzene.RateLimiting`, `Benzene.Resilience` ship one
    (`grep -rl ": RegistrationsBase" src` lists 23 packages; those nine are absent). So the user is told a
    type they never typed (`OutboxOptions`) is missing, with no "add `.AddOutbox()`".
  - Contrast the model that does it right: `src/Benzene.Auth.Core/AuthorizationExtensions.cs:60-62`
    `"No IAuthorizationPolicy named '{policyName}' is registered. Register one with AddAuthorizationPolicy."`
- **Fix:** add a `RegistrationsBase` per family (the mechanism exists; ~10 lines each) so the hint reads
  `IIdempotencyStore is registered in .AddInMemoryIdempotencyStore() from Benzene.Idempotency`, or throw
  a named exception at `Use*` time as `RequirePolicy` does.

#### 6. Kafka: no start-up check ties `BenzeneKafkaConfig.Topics` to the registered `[Message]` topics  [magic]

- **§4.1 clause:** start-up check for a convention; "finding out late".
- **Evidence:** the only handler checks are `DuplicateTopicStartUpCheck` and
  `EmptyHandlerRegistryStartUpCheck` (`src/Benzene.Core.MessageHandlers/DI/Extensions.cs:222-229`).
  `KafkaHealthCheck` verifies the **broker** has the topics (`src/Benzene.Kafka.Core/KafkaHealthCheck.cs:65-76`),
  not that a handler exists for them. The guide's own troubleshooting entry is the message-path symptom:
  `docs/getting-started-kafka.md:338-341` "Handler never fires — ... Double-check `BenzeneKafkaConfig.Topics`".
  Under the default auto-store config an unrouted record is committed (documented carve-out,
  `src/Benzene.Kafka.Core/CLAUDE.md` "`RaiseOnFailureStatus`" bullet).
- **What the user experiences:** `Topics = { "order-place" }` vs `[Message("order_place")]` - every record
  silently lost, health green, first sign is an empty database.
- **Fix:** an `IStartUpCheck` registered by `UseKafka` (it already calls `app.Register`) that warns when a
  subscribed topic has no handler and when a handler topic is not subscribed. Advisory (log), like
  `empty-handler-registry`, because a topic can be intentionally routed by header.

#### 7. `UseFluentValidation()` with no arguments scans `AppDomain.CurrentDomain.GetAssemblies()` and has no start-up signal when it finds nothing  [magic]

- **§4.1 clause:** scanning "permitted exactly to the degree that it is verified before any message is handled".
- **Evidence:** `src/Benzene.FluentValidation/DependencyExtensions.cs:14-18` (`params Assembly[]` with no
  args -> `AddFluentValidation()` over an empty set -> zero validators, silently);
  `docs/fluent-validation.md:72-73` ("No arguments: scans every assembly currently loaded");
  `docs/fluent-validation.md:11` ("If no `IValidator<TRequest>` is registered ... the middleware does nothing").
  The handler-side analogue (`EmptyHandlerRegistryStartUpCheck`) exists; the validator-side one does not.
- **What the user experiences:** a validator assembly not yet loaded at `Configure` time (common with a
  separate `Validators` project) means **no validation, ever, with no log line**.
- **Fix:** log an error at start-up when `UseFluentValidation()` discovered zero validators but
  `IValidator<T>` types exist in referenced assemblies (or simply when zero were found), mirroring
  `empty-handler-registry`'s wording.

#### 8. Self-hosted consumer health auto-wiring is inconsistent across the five workers  [ceremony / parity]

- **§4.1 clause:** routine capability shorthand (health "for free") must not depend on which transport.
- **Evidence:**
  | Worker | `healthCheck` param / auto-wire | file:line |
  |---|---|---|
  | `UseKafka` | yes, default on | `src/Benzene.Kafka.Core/Extensions.cs:34,41-44` |
  | `UseRabbitMq` | yes, default on | `src/Benzene.RabbitMq/Extensions.cs:30-32,39-42` |
  | `UseServiceBus` | yes, default on | `src/Benzene.Azure.ServiceBus/Extensions.cs:9-10,17-20` |
  | `UseSqs` (worker) | **none** (`SqsHealthCheck` exists at `src/Benzene.Clients.Aws.Sqs/SqsHealthCheck.cs:33`) | `src/Benzene.Aws.Sqs/Extensions.cs:11-28` |
  | `UseEventHub` | **none** (`EventHubHealthCheck` exists in `Benzene.Clients.Azure.EventHub`) | `src/Benzene.Azure.EventHub/Extensions.cs:9-28` |
  | `UseAspNet` | none (n/a - no dependency) | `src/Benzene.AspNet.Core/AspNetSelfHostExtensions.cs:10-33` |
  `docs/kubernetes-health-checks.md:44-53` promises "every client ships a check".
- **Fix:** add `bool healthCheck = true` + `AddSqsDependencyHealthCheck` / `AddEventHubDependencyHealthCheck`
  to the two outliers, composed exactly as `AddKafkaDependencyHealthCheck` is.

#### 9. TestHelpers parity: the SQS worker has no `Build*WorkerHost` and `UseSqs` does not register the application singleton the pattern relies on  [parity]

- **Evidence:** `UseKafka`/`UseRabbitMq`/`UseServiceBus`/`UseEventHub` all `app.Register(x => x.AddSingleton(application))`
  "so it can be resolved and driven directly ... (see ...TestHelpers)" (`src/Benzene.Kafka.Core/Extensions.cs:51-55`,
  `src/Benzene.RabbitMq/Extensions.cs:56-60`, `src/Benzene.Azure.ServiceBus/Extensions.cs:26-30`,
  `src/Benzene.Azure.EventHub/Extensions.cs:21-25`). `src/Benzene.Aws.Sqs/Extensions.cs:25-26` does not, and
  `src/Benzene.Aws.Sqs.TestHelpers/` contains only `MessageBuilderExtensions.cs`.
- **What the user experiences:** the Kubernetes guide's three-transport service can component-test its
  Kafka leg in memory and not its SQS leg.
- **Fix:** mirror the four siblings (register `SqsConsumerApplication`, add `BuildSqsWorkerHost<TStartUp>()`).

#### 10. `UseBenzeneInvocation()` is seeded by Kafka/SQS/EventHub workers but not by RabbitMQ/Service Bus, so `UseBenzeneEnrichment()` silently loses `invocationId` on two transports  [parity / magic]

- **Evidence:** `src/Benzene.Kafka.Core/Extensions.cs:46`, `src/Benzene.Aws.Sqs/Extensions.cs:18`,
  `src/Benzene.Azure.EventHub/Extensions.cs:16` call `middlewarePipelineBuilder.UseBenzeneInvocation()`;
  `src/Benzene.RabbitMq/Extensions.cs:44-52` and `src/Benzene.Azure.ServiceBus/Extensions.cs:21-23` do not.
  `docs/common-middleware.md:76-80`: "`invocationId` requires `UseBenzeneInvocation()` to have been called
  ... omitted" otherwise. `docs/diagnosing-failures.md:291-294` says the batch transports wire it
  "automatically" - true for three of five.
- **Fix:** add the same call to the two outliers (both already have a per-delivery scope).

#### 11. `Program.cs`: the guides and templates show the 5-line explicit host; the examples they call "the runnable version" use the 1-line `BenzeneHost.RunAsync`  [invisible-ladder / drift]

- **§4.1 clause:** shorthand documented beside its explicit form; examples are where the claim is tested.
- **Evidence (long form shown, shorthand absent):** `docs/getting-started-kafka.md:171-179`,
  `docs/getting-started-rabbitmq.md:157-165`, `docs/getting-started-kubernetes.md` §2 (the
  `// Program.cs - the plain generic host` block), `docs/getting-started-worker.md:321-330` (Part B),
  `docs/cookbooks/distributed-tracing-opentelemetry.md:230-240`, `templates/content/{kafka,rabbitmq,servicebus}-worker/Program.cs`.
  **Shorthand shown:** `examples/Kafka/Benzene.Examples.Kafka/Program.cs:6`,
  `examples/K8sTransports/App/Program.cs:7`, `docs/getting-started-worker.md:192` (Part A, with the explicit
  form in a `<details>` at `:212-230` - the model to copy), `docs/hosting.md:323-364`.
  `docs/getting-started-kafka.md` calls `examples/Kafka` "a runnable version of exactly this Kafka worker"
  (`getting-started-worker.md:335`), yet its `Program.cs` differs from the guide's.
- **Fix (docs/templates):** show `await BenzeneHost.RunAsync<StartUp>(args);` as the primary form in the
  five guide sites and three templates, keeping the explicit form in a collapsed block as Part A does.

#### 12. Templates re-register what `UseKafka`/`UseRabbitMq`/`UseServiceBus` already register, and the comment says the opposite of the guides  [duplication x3]

- **Evidence:** `templates/content/kafka-worker/StartUp.cs:33-37` `.AddBenzene().AddMessageHandlers(...).AddKafka<Ignore,string>()`;
  `templates/content/rabbitmq-worker/StartUp.cs:26,33-37` ("AddRabbitMq() registers the consumer's services");
  `templates/content/servicebus-worker/StartUp.cs:26,33-37`. But `UseKafka` calls `AddBenzeneMessage().AddKafka<TKey,TValue>()`
  itself (`src/Benzene.Kafka.Core/Extensions.cs:36-39`), `UseRabbitMq` calls `AddBenzeneMessage().AddRabbitMq(...)`
  (`src/Benzene.RabbitMq/Extensions.cs:34-37`), `UseServiceBus` likewise (`src/Benzene.Azure.ServiceBus/Extensions.cs:12-15`),
  and the guides say so: `docs/getting-started-kafka.md:157-160` "no Benzene registration is needed in
  `ConfigureServices`", `docs/getting-started-rabbitmq.md:137-140`.
- **What the user experiences:** the first `StartUp` they ever see contradicts the first guide they read;
  they keep both forever.
- **Fix:** delete the three `Add<Transport>()` lines (and `.AddBenzene()` if `UseMessageHandlers()` covers
  it - `AddMessageHandlers` registers the core, `src/Benzene.Core.MessageHandlers/Extensions.cs:86-89`) and
  fix the comment. *(Templates are adjacent to my territory; reported because they are the newcomer path
  for these transports.)*

#### 13. The recommended observability stack is 5 pipeline calls + 1 registration + an OTel block, copied by hand across the example corpus  [duplication / missing shorthand]

- **§4.1 clause:** "Every capability a service needs *routinely* MUST have a shorthand"; "duplicated
  plumbing across examples is a framework bug ... the count is the evidence."
- **Counts (`grep -rn --include=*.cs examples`, `/obj` excluded):**
  | Pattern | Occurrences | Files |
  |---|---|---|
  | `UseBenzeneEnrichment()` | 29 | - |
  | `UseBenzeneMetrics()` | 15 | - |
  | `UseW3CTraceContext()` | 9 | - |
  | the exact `UseW3CTraceContext().UseBenzeneEnrichment().UseBenzeneMetrics()` chain | 7 | - |
  | `AddOpenTelemetry()` block with `AddBenzeneInstrumentation()` x2 | 8 | `K8sMesh/Service/Startup.cs:53-65`, `K8sMesh/Mesh/Startup.cs`, `AwsMesh/Shared/{MeshServiceWiring,LambdaTelemetry}.cs`, `AwsMesh/Mesh/Startup.cs`, `AzureMesh/Mesh/Startup.cs`, `AzureFunctionsMesh/Shared/MeshServiceWiring.cs`, `OpenTelemetry/Program.cs:22-31` |
  | `SetSampler(new AlwaysOnSampler())` incantation | 4 | with a "required, otherwise no spans" comment (`OpenTelemetry/Program.cs:20-21`) that `docs/sampling-strategies.md:20` hedges to "under some SDK/host combinations" |
  `docs/diagnosing-failures.md:203-218` documents the stack as 5 + 1 calls and calls it "the recommended stack".
- **Fix (proposal):** `UseBenzeneObservability()` (pipeline; = the three inbound calls, doc names them) and
  `AddBenzeneOpenTelemetry(otlp: bool)` (host; = `AddOpenTelemetry().WithTracing(...AddBenzeneInstrumentation()).WithMetrics(...)`)
  - both compositions a user can write today, so rule 2 holds. Settle the `AlwaysOnSampler` question in
  one place (either it is required, and the shorthand sets it, or the four comments are wrong).

#### 14. `examples/Kafka` is three times the size of the guide it claims to be the runnable version of, and most of the difference is plumbing  [ceremony / example ledger]

- **Ledger:** see "Boilerplate ledger" below - `DependenciesBuilder.cs` is 4 intent lines to 21 plumbing.
- **Evidence of dead/redundant plumbing:** `examples/Kafka/Benzene.Examples.Kafka/DependenciesBuilder.cs:20-27`
  builds config from `config.json` whose only key (`DB_CONNECTION_STRING`, `config.json:2`) is never read;
  `:29-34` `CreateServiceCollection` has no callers (`grep` -> definition only); `:39-40`
  `AddSingleton(configuration)` / `AddLogging()` duplicate what `BenzeneHost`'s generic host and
  `HostBuilderExtensions.cs:21` already do; `:48` `.AddKafka<Ignore,string>()` duplicates `UseKafka`
  (`src/Benzene.Kafka.Core/Extensions.cs:38`); `:55-58` registers a `benzene:spec` handler **and an HTTP
  endpoint** (`IHttpEndpointDefinition("get", "/spec", ...)`) in a worker with no HTTP transport;
  `:51-53` a `CompositeProcessTimerFactory` with no comment saying it is a deliberate demonstration;
  `StartUp.cs:30-31` `SaslMechanism.Plain` + `SecurityProtocol.Plaintext` (Confluent defaults);
  `StartUp.cs:32` `GroupId = Guid.NewGuid()` (a test-isolation concern the guide has to explain at
  `docs/getting-started-kafka.md:342-344`).
- **Fix:** collapse to the guide's `StartUp` (`docs/getting-started-kafka.md:117-150`) + the one-line
  `Program.cs`; move any deliberate demonstration under a comment saying so, or delete it.

#### 15. `examples/Kafka/...Producer/Program.cs` is the fully explicit outbound rung with no "deliberately explicit" comment, while the shorthand it should point at is documented elsewhere  [ceremony / example rule]

- **§4.1 clause:** "a deliberate demonstration of the explicit form, which MUST say so in a comment."
- **Evidence:** `examples/Kafka/Benzene.Examples.Kafka.Producer/Program.cs:13-34` - `new ServiceCollection()`,
  `UsingBenzene(x => x.AddBenzene().AddBenzeneMiddleware())`, `new MicrosoftBenzeneServiceContainer(services)`,
  `new MiddlewarePipelineBuilder<KafkaSendMessageContext>(...).UseKafkaClient(producer).Build()`,
  `new KafkaBenzeneMessageClient(pipeline, NullLogger<...>.Instance, serviceContainer.CreateServiceResolverFactory().CreateScope())`
  - 10 plumbing lines, no comment. The shorthand (`AddOutboundRouting(r => r.Route(topic, p => p.UseKafka(...)))`
  + `IBenzeneMessageSender`) exists (`src/Benzene.Kafka.Core/Kafka/Extensions.cs:84,109`;
  `docs/getting-started-rabbitmq.md:261-263` names it for RabbitMQ) but the Kafka guide's §1.6 shows only
  the explicit form (`docs/getting-started-kafka.md:194-210`).
- **Fix:** show the routed shorthand first in §1.6 and the example; keep the explicit client under a
  comment that says it is the rung below.

#### 16. `health-checks.md` shows an `IHealthCheck` the code no longer has  [drift, trace-only]

- **Evidence:** `docs/health-checks.md:61-66` and the "custom health check" sample at `:570`
  `public async Task<IHealthCheckResult> ExecuteAsync()`; `src/Benzene.HealthChecks.Core/IHealthCheck.cs:7`
  `Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken);` (the package `CLAUDE.md`
  states the token is required, "no default parameter, no parallel overload"). The doc sample would not
  compile as an implementer.
- **Fix:** update the two snippets; also mention `IsNonCritical`/`Timeout` DIMs there (they are documented
  only in troubleshooting, `:607-610`).

#### 17. `examples/K8sMesh/Service` (service side): the same information is declared twice inside one file, and the same helper class is copied across eight example files  [duplication x8 / x10]

- **Evidence:**
  - `examples/K8sMesh/Service/Startup.cs:75` `AddMessageHandlers(Domain.HandlersFor(ServiceName))` and
    `:163` `.WithHandlers(Domain.HandlersFor(name))`; `:134` `IHealthCheck[] healthChecks`, used at `:153`
    (`UseHealthCheck`) and `:162` (`WithHealthChecks`). The cloud-service builder is asked for what the
    container already knows.
  - `class ServiceHealthCheck : IHealthCheck` is defined in **8** files (`examples/K8sMesh/Service/Domain.cs:130`,
    `examples/GoogleCloudMesh/Shared/ServiceHealthCheck.cs:6`, `examples/AzureFunctionsMesh/{Payments,Notifications,Shipping,Orders,Analytics,Inventory}/Domain.cs`).
  - `$"http://0.0.0.0:{Environment.GetEnvironmentVariable("PORT") ?? "8080"}"` appears in **10** `Program.cs`/`Startup.cs`
    files (`grep -rn '"PORT"' examples`), while `AspNetServerOptions.Urls` defaults to `http://0.0.0.0:8080`
    (`src/Benzene.AspNet.Core/AspNetServerOptions.cs:7`) and never reads `PORT`.
  - The OTel block (finding 13) is here at `:53-65`.
- **Fix:** (a) `UseBenzeneCloudService` should default `WithHandlers`/`WithHealthChecks` from what is
  registered (drop one level = the explicit `With*`); (b) one `ServiceHealthCheck` in a shared example
  project or, better, a framework `SimpleHealthCheck(name)`; (c) decide whether `PORT` is a convention
  Benzene honours (then `AspNetServerOptions`/`BenzeneWebHost` read it) or not (then delete it from nine
  places). *(Shared with the mesh reviewer; reported here for the count.)*

### POLISH

#### 18. `examples/K8sTransports/App/Startup.cs:38-40` registers `.AddHttpMessageHandlers()` "for the HTTP route table" - `UseAspNet` already does it

- `src/Benzene.AspNet.Core/AspNetSelfHostExtensions.cs:19-22` -> `AddAspNetMessageHandlers()` ->
  `src/Benzene.AspNet.Core/DependencyInjectionExtensions.cs:58` `services.AddHttpMessageHandlers();`. Harmless
  (`TryAdd`), but the comment teaches a registration the framework owns, and the guide repeats it
  (`docs/getting-started-kubernetes.md` §2). Delete the line and the comment in both.

#### 19. The rungs below `UseWorker` are public but undocumented, and one carries dead code

- `src/Benzene.SelfHost/IBenzeneWorkerStartup.cs:7-12` (no `///`; `:14-21` commented-out interface),
  `BenzeneWorkerBuilder.cs:8-37`, `WorkerApplicationBuilder.cs:7-22`, `CompositeBenzeneWorker.cs:5` - all
  `public`, no XML docs. §4.1 rule 2.3: "Every rung you land on is public, documented API". The prose
  in `docs/getting-started-worker.md:583-628` is good; the types themselves say nothing.

#### 20. `UseKafka<TKey,TValue>` is the only worker that needs explicit generic arguments, and the guide names `<Ignore, string>` as "the common case"

- `src/Benzene.Kafka.Core/Extensions.cs:34` vs the non-generic `UseRabbitMq`/`UseSqs`/`UseServiceBus`/`UseEventHub`;
  `docs/getting-started-kafka.md:153-156`; every call site in the territory writes `<Ignore, string>`
  (`examples/Kafka/...StartUp.cs:39`, `examples/K8sTransports/App/Startup.cs:81`, templates, and the
  test host `BuildKafkaWorkerHost<StartUp, Ignore, string>`). Proposal: a non-generic `UseKafka(config, action, ...)`
  forwarding to `<Ignore, string>`; the generic form stays as the rung below.

#### 21. RabbitMQ has a template but no runnable example, and its guide has no "Runnable version" banner

- `ls examples` -> no RabbitMq folder; `docs/getting-started-rabbitmq.md` has no banner where
  `getting-started-kubernetes.md:10-12` and `getting-started-kafka.md` have one. Parity with Kafka.

#### 22. `examples/CLAUDE.md:187` says the Kafka example is "run via `Host…UseBenzene<StartUp>()`"

- It runs via `BenzeneHost.RunAsync<StartUp>(args)` (`examples/Kafka/Benzene.Examples.Kafka/Program.cs:6`).

#### 23. `UseHealthCheck` requires a `topic` argument that the middleware always supplements with the default topic anyway; `UseLivenessCheck`/`UseReadinessCheck`/`UseContractsCheck` do not

- `src/Benzene.HealthChecks/Extensions.cs:12-33` (three overloads, all `string topic` first) vs `:35-106`.
  Every example passes `"healthcheck"` or `"benzene:healthcheck"` (3 sites). Proposal: `UseHealthCheck(Action<IHealthCheckBuilder>)`.

#### 24. Verb / rung inconsistencies inside the families

- `UsePayloadVersionCasting<TContext>` is a `Use*` on `IBenzeneServiceContainer`
  (`src/Benzene.Core.Versioning/PayloadVersionCastingExtensions.cs:40`); every other container verb is `Add*`.
- `UseJsonSchema()` is pipeline-level (`src/Benzene.JsonSchema/Extensions.cs:8-14`) while the other two
  validators nest inside `UseMessageHandlers(router => router.UseFluentValidation())`
  (`src/Benzene.FluentValidation/DependencyExtensions.cs:14`, `src/Benzene.DataAnnotations/DependencyExtensions.cs:7`).
  Same capability, two rungs - fine if stated; `docs/common-middleware.md:348-368` does not say why.
- `docs/cookbooks/idempotency.md:47,67,99` writes `UseIdempotency<ServiceBusMessageContext>()` explicitly while
  `docs/claim-check.md:59` says "`TContext` inferred". Pick one house style.

#### 25. `examples/OpenTelemetry/Program.cs` is the rung-1 in-process pipeline, built by hand, without saying so

- `:33-42` `new MicrosoftBenzeneServiceContainer` / `new MiddlewarePipelineBuilder<BenzeneMessageContext>` /
  `new BenzeneMessageApplication` / `:53` `new MicrosoftServiceResolverFactory(app.Services)` - the explicit
  form §4.1 requires, but with no comment saying it is deliberate, no `.WithStartUpChecks()`
  (available, `src/Benzene.Core.MessageHandlers/StartUpChecks/BenzeneStartUpCheckExtensions.cs`), and a
  double handler registration (`:46` `AddMessageHandlers(typeof(Program).Assembly)` plus `:40`
  `UseMessageHandlers(typeof(Program).Assembly)`, which registers the same set at
  `src/Benzene.Core.MessageHandlers/Extensions.cs:86-89`). Add the comment, the check, drop one registration.

#### 26. Health-check *factories* are constructed at probe time, outside `pipeline-resolution`

- `src/Benzene.Cache.Core/CacheHealthCheckFactory.cs:10-12` resolves `TCacheService` when the probe runs;
  `AddHttpPing` resolves `HttpClient` likewise (`docs/health-checks.md:273-275`). A forgotten
  `AddScoped<OrderCacheService>()` surfaces as a `Failed` check (`BuildHealthCheck` -> `FailedHealthCheck`,
  `src/Benzene.HealthChecks/Extensions.cs:140-150`) on the first probe, not at start-up. Visible, but late.
  Proposal: have `pipeline-resolution` (or a sibling check) call `IHealthCheckBuilder.GetHealthChecks(resolver)` once.

#### 27. Kafka and RabbitMQ config objects: `Topics`/`QueueName` are `required` (compile-time, good); `Topics = Array.Empty<string>()` is accepted silently

- `src/Benzene.Kafka.Core/BenzeneKafkaConfig.cs:12`. Trivial guard in `StartAsync` alongside the existing
  `CommitOnlyOnSuccess` invariants (`src/Benzene.Kafka.Core/BenzeneKafkaWorker.cs:68-75`).

---

## Ceremony-parity table: the cross-cutting middleware families

"Lines" = lines a user writes to add the capability to one pipeline in the common case (excluding
`using`s). "Separate DI first" = a registration in `ConfigureServices` that must exist or the `Use*` fails.
"Health/monitoring" = does the family's own observability come with the call.

| Family | Verb / entry point | Lines | Argument style | Separate DI first | Fails at start-up if misconfigured? (trace) | Health / monitoring for free | Doc names the rung below? | file:line |
|---|---|---|---|---|---|---|---|---|
| Validation (FluentValidation) | `router.UseFluentValidation(assemblies)` inside `UseMessageHandlers(router => ...)` | 1 (+1 nesting) | `params Assembly[]` / `Type[]` | no (self-registers) | **no** - zero validators found is silent (finding 7) | n/a | partly (`AddFluentValidation` shown) | `src/Benzene.FluentValidation/DependencyExtensions.cs:14-24` |
| Validation (DataAnnotations) | `router.UseDataAnnotationsValidation()` | 1 (+1) | none | no | n/a (no config) | n/a | yes (says it registers nothing) | `src/Benzene.DataAnnotations/DependencyExtensions.cs:7` |
| Validation (JsonSchema) | `app.UseJsonSchema()` - **pipeline** level, not router | 1 | none | no | yes (`Use<TContext, JsonSchemaMiddleware>` resolved by `pipeline-resolution`) | n/a | no | `src/Benzene.JsonSchema/Extensions.cs:8-14` |
| Retry | `app.UseRetry(...)` | 1 | **8 positional optionals**, no options object | no | n/a | no metrics tag | yes (`RetryMiddleware<T>` ctor documented) | `src/Benzene.Resilience/Extensions.cs:15-28` |
| Timeout | `app.UseTimeout(TimeSpan)` | 1 | positional | needs `AddBenzene` (`CancellationTokenAccessor`, `Core.MessageHandlers/DI/Extensions.cs:113`) | yes (`GetService<CancellationTokenAccessor>` at construction) | no | yes | `src/Benzene.Resilience/Extensions.cs:8-13` |
| Polly | `app.UseResiliencePipeline(pipeline | Action<builder>)` | 1 | object **or** lambda (4 overloads) | no | yes | no | yes (`PollyResilienceMiddleware` ctor) | `src/Benzene.Resilience.Polly/Extensions.cs:9-35` |
| Rate limiting | `app.UseRateLimiting(RateLimiter)`, `UseFixedWindowRateLimiting(int, TimeSpan)`, `UseTokenBucketRateLimiting(...)`, `UsePayloadSizeRateLimiting(...)`, `UsePartitionedRateLimiting(...)` | 1 | positional (5 named variants) | no | n/a (limiter is a required arg) | no (a rejection is a `429` result only) | yes (`docs/rate-limiting.md:138-139` table) | `src/Benzene.RateLimiting/Extensions.cs:12-88` |
| Auth: Basic | `app.UseBasicAuth(validator, realm)` | 1 | positional | no (registers `AuthenticationHolder`) | yes (middleware resolved via DI) | no | yes (cookbook) | `src/Benzene.Auth.Basic/Extensions.cs:12-29` |
| Auth: OAuth2 | `app.UseOAuth2Bearer(OAuth2BearerOptions)` | 1 (+5 option lines) | **options object**, validated at wire-up | no | **yes, at `Use*` time** (`options.Validate()`) - the best in the table | no | yes (cookbook) | `src/Benzene.Auth.OAuth2/Extensions.cs:16-53` |
| Authorization | `RequireScope` / `RequireRole` / `RequirePolicy` / `RequireAuthorization` | 1 | `params string[]` / name / predicate / instance | `AddAuthorizationPolicy` only for named policies | yes, **and names the fix** (`"Register one with AddAuthorizationPolicy"`) | no | yes | `src/Benzene.Auth.Core/AuthorizationExtensions.cs:10-119` |
| Correlation | `app.UseCorrelationId(key?)` | 1 | optional positional; key also via `CorrelationHeaderOptions` | no (self-registers `ICorrelationId`) | yes (resolves at build) | log-scope via `WithCorrelationId()` (2nd call) | **yes - three rungs written out** (`docs/correlation-ids.md:36-55`) | `src/Benzene.Diagnostics/Correlation/Extensions.cs:48-57` |
| Tracing (W3C) | `app.UseW3CTraceContext()` | 1 | none | no | yes | needs `AddDiagnostics()` + `AddBenzeneInstrumentation()` to export (2 more calls in 2 places) | reference page contradicts (finding 2) | `src/Benzene.Diagnostics/W3CTraceContextExtensions.cs:11` |
| Enrichment | `app.UseBenzeneEnrichment()` | 1 | none | no | yes | `invocationId` silently absent on RabbitMQ/Service Bus (finding 10) | yes | `src/Benzene.Diagnostics/EnrichmentExtensions.cs:14` |
| Metrics | `app.UseBenzeneMetrics()` + `AddDiagnostics()` + `metrics.AddBenzeneInstrumentation()` | **3 calls in 3 places** | none | `AddDiagnostics()` | no (silently no metrics if any of the three is missing - `docs/cookbooks/custom-metrics-opentelemetry.md:105-110`) | is the monitoring | yes | `src/Benzene.Diagnostics/MetricsExtensions.cs:13`, `src/Benzene.OpenTelemetry/DependencyInjectionExtensions.cs:14-24` |
| Health (deep) | `app.UseHealthCheck(topic, Action<IHealthCheckBuilder>)` | 1 (+n checks) | **required `topic` string** + lambda | no | factories resolved at probe time (finding 26) | is the monitoring; auto-wired dependency checks harvested here only | yes | `src/Benzene.HealthChecks/Extensions.cs:12-33` |
| Health (probes) | `UseLivenessCheck(...)` / `UseReadinessCheck(...)` | 1 each | lambda / params / builder (no topic) | no; **AspNet needs 2 `IHttpEndpointDefinition` lines** for `/livez` `/readyz` (finding 3) | as above | - | yes, but not for the `UseAspNet` shape | `src/Benzene.HealthChecks/Extensions.cs:35-83`; `src/Benzene.Aws.Lambda.ApiGateway/DependencyInjectionExtensions.cs:226,275` |
| Idempotency | `app.UseIdempotency<TContext>(Action<opts>?)` + `services.AddInMemoryIdempotencyStore(ttl?)` | 2 (two places) | lambda options | **yes** (`IIdempotencyStore`) | yes, **unhinted** (finding 5) | no | yes | `src/Benzene.Idempotency/Extensions.cs:11-42` |
| Claim check | `pipeline.UseClaimCheck(opts?)` (outbound) + `app.UseClaimCheck<TContext>(opts?)` (inbound) + `AddInMemoryClaimCheckStore` | 3 (three places, two services) | lambda options | **yes** (`IClaimCheckStore`) | yes, unhinted; missing body setter is a **message-time** `InvalidOperationException` (`docs/claim-check.md:190-194`) | activity tags | yes | `src/Benzene.ClaimCheck/Extensions.cs:10-44` |
| Outbox | `pipeline.UseOutbox(opts?)` + `AddOutbox(opts?)` + `AddInMemoryOutboxStore()` + `AddOutboxDispatcherWorker()` | **4** | lambda options (x2, cloned) | **yes** (`AddOutbox` is mandatory - `UseOutbox` resolves `OutboxOptions`) | yes, unhinted on a type the user never typed (finding 5) | no | yes (`docs/cookbooks/transactional-outbox.md`) | `src/Benzene.Outbox/Extensions.cs:13-67` |
| Serializers (Xml/Newtonsoft/MessagePack/Avro) | `AddXml(opts?)` **and** `UseXml()` - consistent pair across all four | 1 | lambda options | either | yes | n/a | yes | `src/Benzene.Xml/DependencyInjectionExtensions.cs:20-47` etc. |
| Versioning | `AddPayloadVersioning(...)` / `UsePayloadVersionCasting<TContext>()` (both on the **container**) | 1-3 | builder | - | yes (caster graph validated at start-up per `examples/CLAUDE.md`) | n/a | yes (Versioning example) | `src/Benzene.Core.Versioning/PayloadVersion*Extensions.cs:40,43` |
| Response events | `router.UseResponseEvents(Action)` + `AddResponseEventDeclarations(...)` | 1-2 | lambda | optional | `unmapped-response-handlers` warns | n/a | yes | `src/Benzene.ResponseEvents/ResponseEventsExtensions.cs:26,53` |
| Cache | **no middleware by design** (`docs/caching.md:7`); subclass `RedisCacheService` + `AddCacheHealthCheck<T>()` | ~20 | class | `IProcessTimerFactory` **mandatory** (`docs/caching.md:43-60`) | **no** - missing `IProcessTimerFactory` fails when the service is first resolved (a handler dependency, outside `pipeline-resolution`) | health via 1 extra call | yes (doc is candid) | `src/Benzene.Cache.Core/Extensions.cs:7` |
| Saga / MapReduce | in-code (`new SagaBuilder()`, `ScatterGatherAsync`) - not pipeline families | - | - | - | - | - | yes (`docs/cookbooks/sagas.md:62` warns `Saga.Define()` does not exist) | - |

**Inconsistencies the table makes visible:** (1) argument style is four different things - positional
optionals (Retry), options object (OAuth2), options lambda (Idempotency/Outbox/ClaimCheck), fluent
builder (Health); (2) validation sits on two different rungs (router vs pipeline); (3) health "for free"
holds for three of five self-hosted consumers; (4) the observability capability is the only one that
needs three calls in three places; (5) the families that need a store all fail correctly at start-up
but none names the registration to add; (6) only OAuth2 validates its own options at `Use*` time.

---

## Transport consistency: self-hosted workers vs each other and vs the cloud triggers

| | `UseKafka` | `UseRabbitMq` | `UseServiceBus` (worker) | `UseEventHub` | `UseSqs` (worker) | `UseAspNet` | Lambda `UseSqs` | Functions `UseServiceBus` |
|---|---|---|---|---|---|---|---|---|
| Signature | `(config, action, factory?, deadLetter?, healthCheck=true)` | `(config, factory, action, healthCheck=true)` | `(config, factory, action, healthCheck=true)` | `(config, factory, action)` | `(config, factory, action, Action<opts>?)` | `(action, Action<opts>?)` | `(action, Action<opts>?, topicKey)` | `(action, Action<opts>?, topicKey)` |
| file:line | `Kafka.Core/Extensions.cs:34` | `RabbitMq/Extensions.cs:30-32` | `Azure.ServiceBus/Extensions.cs:9-10` | `Azure.EventHub/Extensions.cs:9` | `Aws.Sqs/Extensions.cs:11` | `AspNet.Core/AspNetSelfHostExtensions.cs:10-13` | `Aws.Lambda.Sqs/Extensions.cs:29` | `Azure.Function.ServiceBus/DependencyInjectionExtensions.cs:72,99` |
| Client factory | optional 3rd (built from config) | required 2nd | required 2nd | required 2nd | required 2nd | n/a | n/a | n/a |
| Options shape | positional optionals | `bool` | `bool` | none | **lambda** (`SqsConsumerOptions`) | lambda | lambda | lambda |
| Generic args required | **yes** `<TKey,TValue>` | no | no | no | no | no | no | no |
| Registers | `AddBenzeneMessage().AddKafka<>()` | `AddBenzeneMessage().AddRabbitMq(key)` | `AddBenzeneMessage().AddServiceBusConsumer(key)` | `AddBenzeneMessage().AddEventHubConsumer(key)` | `AddBenzeneMessage().AddSqsConsumer(key)` | **`AddBenzene()`** + `AddAspNetMessageHandlers()` | `AddSqs(key)` | `AddAzureServiceBus(key)` |
| `UseBenzeneInvocation()` seeded | yes `:46` | **no** | **no** | yes `:16` | yes `:18` | via `BuildHttpPipeline` | yes | yes |
| Ambient cancellation seeded | not in `UseKafka` (worker not verified) | yes `:47-51` | not verified | not verified | not verified | yes (`UseHttp`, per HealthChecks CLAUDE.md) | - | - |
| Health auto-wired | yes | yes | yes | **no** | **no** | n/a | (client side) | (client side) |
| App registered for TestHelpers | yes `:55` | yes `:60` | yes `:30` | yes `:25` | **no** | no | n/a | n/a |
| `Build*WorkerHost` in TestHelpers | yes | yes | yes | yes | **no** (MessageBuilder only) | (use `WebApplicationFactory`) | `BuildAwsLambdaHost` | `BuildAzureFunctionApp` |
| `RegistrationsBase` hints | **no** | **no** | yes | yes | yes | (Http yes) | yes | yes |
| Config `required` members | `ConsumerConfig`, `Topics` | `QueueName` | - | - | - | - | - | - |
| Null-outcome policy | ack (documented carve-out) | nack | (per AckMode) | ack | leave on queue | 404 | batch-item-failure | - |
| Runnable example | `examples/Kafka`, `K8sTransports` | **none** (template only) | none (template) | none | `K8sTransports` | `K8sTransports` | `examples/Aws` | `examples/Azure` |

The worker family is one verb, one config-first shape, one pipeline lambda - good. The asymmetries are
all in the optional tail (health, invocation, test host, hints), which is exactly where a user notices
"this worked on Kafka and not on SQS" and cannot see why.

---

## Capability -> explicit form -> shorthand -> documented?

| Capability | Explicit form (public, one level down) | Shorthand | Shorthand is a composition of the explicit form? | Documented from the top? |
|---|---|---|---|---|
| Run a worker process | `Host.CreateDefaultBuilder(args).UseBenzene<StartUp>().Build().RunAsync()` (`HostBuilderExtensions.cs:14-40`) | `BenzeneHost.RunAsync<StartUp>(args)` (`BenzeneHost.cs:87-90`) | yes (verbatim, `:69-75`) | yes (`hosting.md:323-364`, worker doc Part A) - **but not in the Kafka/RabbitMQ/K8s guides** (finding 11) |
| Below that | `InlineSelfHostedStartUp` / `BuildHostedService` | - | - | yes, but this rung skips start-up checks (finding 4) |
| Host a Kafka consumer | `app.Create<KafkaRecordContext<K,V>>()` + `UseBenzeneInvocation()` + `action` + `new KafkaApplication` + `worker.Add(f => new BenzeneKafkaWorker(...))` + `AddBenzeneMessage().AddKafka<K,V>()` | `worker.UseKafka<K,V>(config, kafka => ...)` (`Kafka.Core/Extensions.cs:34-63`) | yes - I traced every line to public API (`IBenzeneWorkerStartup.Register/Create/Add`, `KafkaApplication`, `BenzeneKafkaWorker` ctor are public) | yes (`getting-started-kafka.md` §1.4; `getting-started-worker.md:583-628` shows the composition) |
| Host a RabbitMQ consumer | same shape + `SeedCancellationToken` middleware | `worker.UseRabbitMq(config, factory, rabbit => ...)` (`RabbitMq/Extensions.cs:30-69`) | yes | yes (`getting-started-rabbitmq.md` §2) |
| Several workers in one process | `worker.Add(...)` x n -> `CompositeBenzeneWorker` (`SelfHost/CompositeBenzeneWorker.cs`) | chain `UseAspNet().UseSqs().UseKafka()` in one `UseWorker` | yes | yes (`getting-started-kubernetes.md` §2), but `CompositeBenzeneWorker` is undocumented as a type (finding 19) |
| Serve HTTP as a worker | `new AspNetServerWorker(new AspNetApplication(pipeline, factory), options)` | `worker.UseAspNet(asp => ..., o => ...)` | yes (`AspNetSelfHostExtensions.cs:24-32`) | yes |
| Kubernetes probes | `IHttpEndpointDefinition` x2 + `UseLivenessCheck`/`UseReadinessCheck` | **missing on AspNet** (exists on ApiGateway) | - | partly (finding 3) |
| Dependency health for a consumer | `UseHealthCheck(t, b => b.AddKafkaHealthCheck(config))` (`KafkaHealthCheckExtensions.cs:19-24`) | `UseKafka(..., healthCheck: true)` -> `AddKafkaDependencyHealthCheck` | yes (`:36-42`) | yes; but no doc shows where a worker *answers* it (finding 3) |
| Caching | subclass `RedisCacheService`, `ICacheEntry<T>.LazyLoadAsync` | none (by design, stated) | - | yes, candid (`caching.md:7`) |
| Rate limiting | `app.Use(r => new RateLimitingMiddleware<T>(limiter, cost, r, ...))` | `UseRateLimiting(limiter)` / `UseFixedWindowRateLimiting(n, window)` | yes (`RateLimiting/Extensions.cs:19-27`) | yes (`rate-limiting.md:138-139`) |
| Timeout / retry | `new TimeoutMiddleware<T>(accessor, timeout)` / `new RetryMiddleware<T>(...)` | `UseTimeout(ts)` / `UseRetry(...)` | yes | yes (`resilience.md`, `common-middleware.md:452-518`) |
| Auth | `new BasicAuthMiddleware<T>(...)` / `new OAuth2BearerMiddleware<T>(...)` (both `public`) | `UseBasicAuth(v)` / `UseOAuth2Bearer(opts)` | yes | yes (cookbook), explicit ctor not shown (acceptable: `IHttpContext`-bound) |
| Validation | `router.Add(new ValidationMiddlewareBuilder())` + `AddFluentValidation(assemblies)` | `router.UseFluentValidation(assemblies)` | yes (`DependencyExtensions.cs:14-18`) | yes (`fluent-validation.md:81-88`) |
| Correlation | `app.Use(r => new InboundCorrelationIdMiddleware<T>(...))` and the inline func below it | `UseCorrelationId()` | yes | **yes - exemplary** (`correlation-ids.md:36-55`) |
| Tracing / metrics | `AddDiagnostics()` + `UseW3CTraceContext()` + `UseBenzeneEnrichment()` + `UseBenzeneMetrics()` + OTel `AddBenzeneInstrumentation()` x2 | **none** (finding 13) | - | yes (`diagnosing-failures.md:199-231`), stale contradiction on one page (finding 2) |
| Swap serializer | `AddXml<TContext>()` / register `ISerializer` before `AddBenzene` (TryAdd) | `UseXml()` / `AddXml()` | yes | yes (`common-middleware.md:683-725`; `BenzeneHost` docs explain the TryAdd order) |
| Outbox / idempotency / claim check | `new OutboxMiddleware(...)` etc. (public ctors) | `UseOutbox()`/`UseIdempotency()`/`UseClaimCheck()` + `Add*Store()` | yes | yes (cookbooks); store registration mandatory and unhinted on failure (finding 5) |
| Test a worker in memory | `new WorkerApplicationBuilder(container)` + `startUp.Configure(...)` + `WithStartUpChecks()` + resolve `KafkaApplication` | `BenzeneTestHost.Create<StartUp>().BuildKafkaWorkerHost<StartUp,K,V>()` | yes (`Kafka.Core.TestHelpers/BenzeneTestHostExtensions.cs:27-41`) | **no - docs say it does not exist** (finding 1) |
| Saga | `new SagaBuilder()...` | - (in-code API) | - | yes |

---

## Boilerplate ledger (examples in the territory)

Every non-blank, non-`using`, non-brace line classified. **Plumbing** is then either *missing shorthand*
(framework bug) or *deliberate demonstration* (must say so in a comment; "commented" below means it does).

| File | domain | intent | plumbing | The plumbing, and which category |
|---|---|---|---|---|
| `examples/Kafka/Benzene.Examples.Kafka/Program.cs` | 0 | 1 | 0 | clean |
| `examples/Kafka/Benzene.Examples.Kafka/StartUp.cs` | 0 | 9 | 7 | `GetConfiguration` override delegating to a JSON file with an unused key (missing shorthand: the base default already reads env); `SaslMechanism.Plain`/`SecurityProtocol.Plaintext` (Confluent defaults); `GroupId = Guid.NewGuid()` (test concern, uncommented) |
| `examples/Kafka/Benzene.Examples.Kafka/DependenciesBuilder.cs` | 2 | 4 | 21 | `config.json` loader (7, unused key); `CreateServiceCollection` (5, no callers); `AddSingleton(configuration)`/`AddLogging()` (2, host already does); `.AddKafka<>()` (1, `UseKafka` does); `IProcessTimerFactory` composite (3, uncommented); spec handler + **HTTP endpoint on a Kafka-only worker** (4) - all "missing shorthand" or dead (finding 14) |
| `examples/Kafka/Benzene.Examples.Kafka.Producer/Program.cs` | 2 | 3 | 12 | the explicit outbound rung with no "deliberate" comment (finding 15); `SaslMechanism`/`SecurityProtocol` (2) |
| `examples/K8sTransports/App/Program.cs` | 0 | 1 | 0 | clean, and the comment names why |
| `examples/K8sTransports/App/Startup.cs` | 0 | 15 | 9 | `AddHttpMessageHandlers()` (1, redundant - finding 18); `?? throw new InvalidOperationException("X is not set")` x2 (4 - hand-rolled required-config guard; missing shorthand); LocalStack client branch (5 - **commented as deliberate**, counts as demonstration); `SecurityProtocol.Plaintext`/`AutoOffsetReset` (2); `PORT` URL line (1, finding 17c) |
| `examples/K8sTransports/Domain/PlaceOrderMessageHandler.cs` | 14 | 2 | 0 | clean - this is what §4.1 wants a service to read like |
| `examples/K8sMesh/Service/Program.cs` | 0 | 1 | 1 | `PORT` URL (duplicated x10) |
| `examples/K8sMesh/Service/Startup.cs` | 24 | 35 | 25 | OTel block (13, duplicated x8, comments explain *what* not that it is deliberate); `AddHttpMessageHandlers()` (1, unverified for the `UseHttp` path); `new HttpClient()` + null-client fallback (4); `WithHandlers`/`WithHealthChecks` repeating registered state (2); collector conditional (4); `/benzene/spec-ui` + `/benzene/spec?type=benzene` literal paths for §5 default surfaces (1) |
| `examples/OpenTelemetry/Benzene.Examples.OpenTelemetry/Program.cs` | 30 | 8 | 12 | OTel block incl. `AlwaysOnSampler` (10); rung-1 hand-built pipeline uncommented (6 - would be *deliberate demonstration* with one comment); duplicate `AddMessageHandlers` (1); no start-up checks (finding 25) |
| `examples/OpenTelemetry/.../Handlers.cs`, `ExampleDiagnostics.cs` | all | - | 0 | clean |

**Duplication sweep (whole `examples/` corpus, `/obj` excluded):** `UseBenzeneEnrichment()` x29,
`UseBenzeneMetrics()` x15, `UseW3CTraceContext()` x9, OTel `AddOpenTelemetry()` block x8,
`class ServiceHealthCheck` x8, `"PORT"` env line x10, `new MicrosoftBenzeneServiceContainer` x7,
`new MiddlewarePipelineBuilder<` x5, `SetSampler(new AlwaysOnSampler())` x4, `new ConsumerConfig` x2,
`new AmazonSQSClient(` x5. By §4.1's own rule the second copy is a signal and the third a backlog item;
the observability block and `ServiceHealthCheck` are past "choosing not to fix it".

---

## Doc honesty (item 6) - trace-only, no SDK

| Snippet | Traces against current API? | Note |
|---|---|---|
| `getting-started-kafka.md:117-150` `StartUp` + `UseKafka<Ignore,string>(kafkaConfig, kafka => kafka.UseMessageHandlers())` | yes (`Kafka.Core/Extensions.cs:34`) | matches `examples/Kafka/...StartUp.cs:38-40` except the example adds `UseFluentValidation()` and a random `GroupId` |
| `getting-started-kafka.md:171-179` `Program.cs` | yes | differs from the example's `BenzeneHost.RunAsync` (finding 11) |
| `getting-started-kafka.md:194-210` producer | yes (`UseKafkaClient(producer)` `Kafka/Extensions.cs:18`; `KafkaBenzeneMessageClient` ctor per example) | explicit rung only |
| `getting-started-kafka.md:221-223` "no BenzeneTestHost for Kafka" | **false** (finding 1) | |
| `getting-started-rabbitmq.md:99-134` | yes (`RabbitMqConnectionFactory(ConnectionFactory)` ctor `:16`; `UseRabbitMq` `:30`) | template uses the `Uri` ctor `:23` - both exist |
| `getting-started-rabbitmq.md:200-208` `new RabbitMqBenzeneMessageClient(channel, logger, serviceResolver, exchange: "")` | plausible (ctor documented in CLAUDE.md; not read) | `serviceResolver` variable undefined in the snippet - reader must guess where it comes from |
| `getting-started-kubernetes.md` §2 `Startup.cs` | yes - identical to `examples/K8sTransports/App/Startup.cs` | including the redundant `AddHttpMessageHandlers()` |
| `getting-started-kubernetes.md` §2 `Program.cs` | yes | differs from example (finding 11) |
| `getting-started-worker.md:88-181` Part A (`BenzeneMessageApplication`, `workers.Add`) | yes (`IBenzeneWorkerStartup.Add` `:9`; `WorkerApplicationBuilder`) | good explicit-rung walkthrough |
| `getting-started-worker.md:251-288` testing | claims contradict shipped TestHelpers (finding 1); `InlineSelfHostedStartUp` snippet traces but skips checks (finding 4) | |
| `kubernetes-health-checks.md:150-169` ASP.NET probes | yes (`HttpEndpointDefinition`, `Constants.Default*Topic`) | `ProcessResponsiveCheck` is a placeholder type (not in `src`) - say so |
| `health-checks.md:61-66, 556-591` | **no** - `ExecuteAsync()` without `CancellationToken` (finding 16) | |
| `common-middleware.md:143-146` W3C scope | **false** (finding 2) | rest of the page traces (`UseRetry` params `:461-468` match `Resilience/Extensions.cs:15-24` minus `maxDelay`, which the doc omits) |
| `caching.md` | yes; candid about `IProcessTimerFactory` | |
| `correlation-ids.md:39-55` explicit rungs | yes (`InboundCorrelationIdMiddleware<T>` ctor shape matches `Correlation/Extensions.cs:53-56`) | |
| `claim-check.md:48-71` | yes (`UseClaimCheck` both overloads, `AddInMemoryClaimCheckStore(ttl)`) | |
| `cookbooks/distributed-tracing-opentelemetry.md:200-240` | yes (`UseSqs(config, factory, action)`) | shows long `Program.cs` |
| `cookbooks/custom-metrics-opentelemetry.md:29,133` links `../reference/middleware.md#usebenzenemetrics` | file exists (`docs/reference/middleware.md`) | not read |
| `cookbooks/auth-patterns.md` | yes (`UseOAuth2Bearer`, `UseBasicAuth`, `Require*`, `AddAuthorizationPolicy` all present with matching shapes) | |
| `cookbooks/bring-your-own-di-container.md:125-126` `app.HandleAsync(request, factory)` | `app` undefined in snippet | minor |
| `cookbooks/entity-framework-integration.md:92-96` `new DatabaseConnectionHealthCheck<T>(dbContext)` in `Configure` | traces, but resolving a scoped `DbContext` at `Configure` time is the anti-pattern the doc itself flags at `:98` | show the factory form instead |
| `examples/CLAUDE.md:187` | stale (finding 22) | |

---

## What is genuinely good

- **`BenzeneHost` is the model shorthand.** One line, composed verbatim from the public `UseBenzene<T>()`
  (`BenzeneHost.cs:69-75`), with `Build` as the drop-one-level seam, the `TryAdd` ordering rule explained
  in the XML docs, and the explicit form kept in a collapsed block in `getting-started-worker.md:212-230`.
  The `Program.cs` comment in both examples says *why* the shorthand exists ("this file would not change
  if a fourth [transport] were added"). Copy this pattern for finding 13.
- **`correlation-ids.md:36-55` writes the ladder out** - shorthand, the middleware it composes, and the
  inline func below that. This is what rule 4 looks like when it is done.
- **The start-up-check phase is real.** `pipeline-resolution` and `terminal-middleware` construct every
  middleware before the first message; a `UseIdempotency()` without a store, a `UseOutbox()` without
  `AddOutbox()`, a `UseTimeout()` without `AddBenzene()` all fail at INIT, not at message time; every
  host in the territory (`HostBuilderExtensions.cs:33`) and every TestHelper (`WithStartUpChecks()`)
  runs them, and there is exactly one switch to soften them (`diagnosing-failures.md:54-63`).
- **`UseOAuth2Bearer` validates at wire-up** (`options.Validate()`, `Auth.OAuth2/Extensions.cs:20`) and
  **`RequirePolicy(name)` names the fix** in its exception. These are the two examples the rest of the
  families should copy.
- **Kafka's config invariants throw with a paragraph of reasoning** (`BenzeneKafkaWorker.cs:68-75, 96-105`)
  and `RabbitMqConfig`/`BenzeneKafkaConfig` use `required` so the two things you cannot omit are
  compile errors.
- **The worker family is one shape.** `UseWorker(worker => worker.UseAspNet(...).UseSqs(...).UseKafka(...))`
  is three transports in six lines, and `getting-started-worker.md:583-628` shows exactly how
  `UseWorker` reaches `IBenzeneWorkerStartup` - the composition is public and visible.
- **`PlaceOrderMessageHandler.cs` reads as domain only.** Two attributes, one method, and a comment that
  says the transport is not its business. That is the §4.1 target and the example hits it.
- **Every transport's `Use*` registers its own DI** (`AddBenzeneMessage().AddKafka<>()` etc.), and the
  guides say "nothing to register in `ConfigureServices`". The templates just have not caught up.
- **The health-check taxonomy is reasoned, not asserted** (`kubernetes-health-checks.md:27-53`): deep
  layer vs probes, shared-fate, one-way door - and the code enforces it (`HealthChecks/Extensions.cs:30-32, 53-55, 76-82`).
- **`RabbitMq/Extensions.cs` seeds the ambient cancellation token** for the delivery so `UseTimeout` and
  handlers observe it - the right composition, done in the shorthand where the user cannot forget it.

---

*Files to hand to the port's own ergonomics champion:* `docs/getting-started-kafka.md`,
`docs/getting-started-worker.md`, `docs/testing-benzene.md`, `docs/common-middleware.md` (blockers);
`src/Benzene.SelfHost/InlineSelfHostedStartUp.cs`, `src/Benzene.Aws.Sqs/Extensions.cs`,
`src/Benzene.Azure.EventHub/Extensions.cs`, `src/Benzene.RabbitMq/Extensions.cs`,
`src/Benzene.Azure.ServiceBus/Extensions.cs`, `src/Benzene.AspNet.Core/AspNetSelfHostExtensions.cs`,
`templates/content/*-worker/StartUp.cs`, `examples/Kafka/**`, `examples/K8sMesh/Service/Startup.cs`,
`examples/OpenTelemetry/**/Program.cs` (should-fix).
