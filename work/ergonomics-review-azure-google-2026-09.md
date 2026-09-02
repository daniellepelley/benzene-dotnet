# Ergonomics review — Azure Functions and Google Cloud Functions hosting (2026-09-02)

**Reviewer role:** cross-language ergonomics champion, enforcing
`docs/specification/design-principles.md` §4.1 "The shorthand ladder" (spec repo). The four rules
referred to below are §4.1's four normative paragraphs: **R1** both ends of the ladder exist;
**R2** the shorthand is composed from the public explicit form (user could have written it; drop
exactly one level; every rung public); **R3** the price of a convention is a start-up check that
names what was looked for, where, and what to add; **R4** the ladder is visible from the top.
The examples rule ("every line is domain or intent; duplicated plumbing is a framework bug, and
the count is the evidence") is applied to every `Program.cs`/`StartUp`/trigger file in scope.

**Commit reviewed:** `f3f1be5` (benzene-dotnet). **Territory:** `docs/azure-functions.md`,
`docs/getting-started-azure.md`, `docs/getting-started-google.md`, the Azure/GCP cookbooks
(`cosmos-change-feed-processing`, `service-bus-handling`, `event-hub-processing`,
`managed-identity`, `entity-framework-integration`, `transactional-outbox`), `examples/Azure`,
`examples/AzureFunctionsMesh` and `examples/GoogleCloudMesh` (service side), `examples/Google`,
`src/Benzene.Azure.Function.*` (incl. SourceGenerators and every `*.TestHelpers`),
`Benzene.Azure.{ServiceBus,EventHub,CosmosDb}`, `Benzene.GoogleCloud.Functions.*`,
`Benzene.ClaimCheck.Azure.Blob`, `Benzene.HealthChecks.{Azure.ServiceBus,EntityFramework}`,
`Benzene.Outbox.EntityFramework`, `Benzene.Mesh.Usage.ApplicationInsights`, plus the two Azure
`dotnet new` templates because they are the first adoption path a newcomer meets.

**What I could and could not run.** No dotnet SDK exists in this sandbox. Nothing below was built
or executed; every "compiles"/"does not compile" statement is **trace-only** against the source at
`f3f1be5`. Where a doc snippet carries a `<!-- compile: ... -->` marker it is compile-checked in CI
by `test/Benzene.Core.Test/Docs/DocSnippetsCompileTest.cs`; I say explicitly which claims have
that backing. I did not modify any source, doc or example.

---

## Executive verdict

- **Go-live blockers: 2** — the declared Timer trigger silently discards the schedule the
  explicit form delivers (R2), and the two real Google Cloud Functions hosts never run the
  start-up checks their own test helpers run (R3).
- **Should-fix: 12** — Azure start-up checks run on the first (and every) invocation, not at
  start-up; no cross-check between a declared trigger and its configured pipeline; the declared
  form cannot reach a second trigger of the same type; eight hand-copied `Program.cs` preambles
  with no `BenzeneFunctionsHost`; and the rest listed below.
- **Polish: 6** — example plumbing ledger items, naming asymmetries, doc drift.
- The core ladder for the eight Azure triggers is sound: the shorthand is composed from documented
  public API, the explicit form sits directly beneath it in the same doc, and the generator fails
  the build with a diagnostic that names the field to set. The blockers are at the edges of that
  ladder, not its centre.
- Verdict: **NEEDS CHANGES** for the two blockers; the should-fix list is the backlog the
  duplication counts justify.

---

## Findings, by severity

### F1. Declared Timer trigger drops the schedule the explicit form forwards — `BLOCKER`

- **Clause:** R2 (the shorthand must be composed from the explicit form, never a degraded parallel).
- **Evidence.** The generator's Timer reader binds the SDK's `TimerInfo` and then throws it away:

  `src/Benzene.Azure.Function.SourceGenerators/Transports/MessagingTransports.cs:417-422`
  ```csharp
  $"[{binding}] global::Microsoft.Azure.Functions.Worker.TimerInfo timer, global::System.Threading.CancellationToken cancellationToken",
  "global::System.Threading.Tasks.Task",
  // The bound "timer" parameter is the Azure SDK's TimerInfo, not Benzene's own
  // TimerTriggerInfo - there's no conversion, so (as before this change) it's bound
  // but intentionally not forwarded; only cancellationToken is new here.
  "global::Benzene.Azure.Function.Timer.Extensions.HandleTimer(_app, cancellationToken: cancellationToken)",
  ```
  That overload synthesises an empty tick — `src/Benzene.Azure.Function.Timer/Extensions.cs:73-76`:
  ```csharp
  public static Task HandleTimer(this IAzureFunctionApp source, CancellationToken cancellationToken = default)
  {
      return source.HandleTimer(new TimerTriggerInfo(), cancellationToken);
  }
  ```
  The explicit form the docs show binds Benzene's own type and forwards it —
  `docs/azure-functions.md:819-841`: *"bind the timer parameter directly as Benzene's
  `TimerTriggerInfo`, whose property names match the worker's `TimerInfo` JSON"* →
  `[TimerTrigger("0 0 2 * * *")] TimerTriggerInfo timer` → `_app.HandleTimer(timer)`. Two
  paragraphs earlier (`:800-806`) the pipeline sample reads `info.IsPastDue` and
  `info.ScheduleStatus?.Next`. The test pins the degraded behaviour rather than catching it:
  `test/Benzene.Core.Test/Autogen/AzureFunctions/AzureFunctionTriggerGeneratorTest.cs:204`
  `Timer_EmitsScheduleRunOnStartupAndNoArgDispatch`.
- **What the user experiences.** They take the steer (`[assembly: BenzeneTimerTrigger(...)]`,
  `docs/azure-functions.md:363`) and write the documented `UseTick(info => ...)`. `IsPastDue` is
  always `false`; `ScheduleStatus` is always `null`, so `info.ScheduleStatus.Next` is an NRE **at
  tick time** — a shorthand that does strictly less than the explicit form, with no diagnostic,
  and the failure lands on the message path (R3 as a consequence).
- **Fix.** The generator's own comment is out of date: the docs already state that
  `TimerTriggerInfo` binds directly. Emit the explicit form.

  Before (generated):
  ```csharp
  public Task Run([TimerTrigger("0 0 2 * * *")] global::Microsoft.Azure.Functions.Worker.TimerInfo timer, CancellationToken cancellationToken)
      => global::Benzene.Azure.Function.Timer.Extensions.HandleTimer(_app, cancellationToken: cancellationToken);
  ```
  After (generated — identical to `docs/azure-functions.md:836-840` plus the token):
  ```csharp
  public Task Run([TimerTrigger("0 0 2 * * *")] global::Benzene.Azure.Function.Timer.TimerTriggerInfo timer, CancellationToken cancellationToken)
      => global::Benzene.Azure.Function.Timer.Extensions.HandleTimer(_app, timer, cancellationToken);
  ```
  Change `MessagingTransports.cs:417` and `:422`, and flip the test at
  `AzureFunctionTriggerGeneratorTest.cs:204` to assert the forwarded parameter.

### F2. The real Google Cloud Functions hosts never run start-up checks; their test helpers do — `BLOCKER`

- **Clause:** R3 (a convention must be verified before any message is handled), and R2's
  test/prod parity promise that the test helper makes in its own doc comment.
- **Evidence.** Both hosts build without checks:

  `src/Benzene.GoogleCloud.Functions.Http/GoogleCloudFunctionHost.cs:37`
  ```csharp
  _app = appBuilder.Build(new MicrosoftServiceResolverFactory(services));
  ```
  `src/Benzene.GoogleCloud.Functions.PubSub/GooglePubSubFunctionHost.cs:35`
  ```csharp
  _app = appBuilder.Build(new MicrosoftServiceResolverFactory(services));
  ```
  Their test helpers do run them, while claiming to reconstruct "the same construction the real
  host performs" —
  `src/Benzene.GoogleCloud.Functions.Http.TestHelpers/BenzeneTestHostExtensions.cs:34` and
  `src/Benzene.GoogleCloud.Functions.PubSub.TestHelpers/BenzeneTestHostExtensions.cs:35`:
  ```csharp
  var app = appBuilder.Build(new MicrosoftServiceResolverFactory(services).WithStartUpChecks());
  ```
  Every other host runs them at initialisation: `src/Benzene.Aws.Lambda.Core/AwsLambdaHost.cs:61`,
  `src/Benzene.Azure.Function.Core/HostBuilderExtensions.cs:42`,
  `src/Benzene.HostedService/HostBuilderExtensions.cs:33`,
  `src/Benzene.AspNet.Core/BenzeneExtensions.cs:178`. The check runner's own remark
  (`src/Benzene.Core.MessageHandlers/StartUpChecks/BenzeneStartUpCheckExtensions.cs:59-62`) says
  *"Called by every host from its initialization"* — not true for Google. No test constructs
  `GooglePubSubFunctionHost<>` at all (grep of `test/` for `GooglePubSubFunctionHost<` returns
  nothing), so the Pub/Sub host's constructor path — including its "call app.UsePubSub(...)"
  guard — has never been executed by CI.
- **What the user experiences.** A pipeline whose middleware cannot be constructed
  (`PipelineResolutionStartUpCheck`), a pipeline with no terminal middleware
  (`TerminalMiddlewareStartUpCheck` — the `UseSqs(sqs => { })` class of bug), or two handlers on
  one topic: green in `Benzene.Examples.Google.Tests`, red on the first HTTP request or Pub/Sub
  push in production. That is exactly "finding out late", and the test helper's parity claim
  makes it worse than having no checks at all.
- **Fix.** One token in each host, plus a test that constructs each host with a broken StartUp:
  ```csharp
  // GoogleCloudFunctionHost.cs:37 and GooglePubSubFunctionHost.cs:35
  _app = appBuilder.Build(new MicrosoftServiceResolverFactory(services).WithStartUpChecks());
  ```
  `examples/Google/README.md:3-5` labels GCP experimental and out of the 1.0 support commitment;
  if that status is accepted at go-live this drops to should-fix, but the `TestHelpers` doc
  comment is false either way and should be corrected in the same change.

### F3. Azure Functions start-up checks run on the first invocation — and on every invocation — `SHOULD-FIX`

- **Clause:** R3. The spec's wording is "verified before any message is handled".
- **Evidence.** `src/Benzene.Azure.Function.Core/HostBuilderExtensions.cs:36-44`:
  ```csharp
  services.AddScoped<IAzureFunctionApp>(serviceProvider =>
  {
      var serviceResolverFactory = new MicrosoftServiceResolverFactory(serviceProvider);
      // Azure Functions resolves IAzureFunctionApp per invocation, so unlike the Lambda host
      // there is no separate INIT hook to hang this on. Running it here still moves the
      // failure off the message path and onto the trigger's own construction.
      serviceResolverFactory.RunStartUpChecks();
      return builder.Create(serviceResolverFactory);
  });
  ```
  `IAzureFunctionApp` is scoped and the generated trigger classes inject it per invocation, so
  the checks (which construct every middleware in every pipeline twice —
  `PipelineResolutionStartUpCheck` and `TerminalMiddlewareStartUpCheck` — and run the handler
  finder) execute on **every** trigger invocation, and the first one is the first message. The
  comment's premise is wrong: the isolated worker is a generic host, and an `IHostedService`
  registered in `ConfigureServices` starts before the worker accepts invocations.
- **What the user experiences.** `func start` succeeds; the first Service Bus message is
  abandoned with a `BenzeneStartUpCheckException` and redelivered until dead-lettered; every
  invocation thereafter pays the construction cost of the checks.
- **Fix.**
  ```csharp
  // HostBuilderExtensions.UseBenzene<TStartUp>, after startUp.Configure(builder, configuration):
  services.AddHostedService<BenzeneStartUpCheckHostedService>();   // once, at host start
  services.AddScoped<IAzureFunctionApp>(sp => builder.Create(new MicrosoftServiceResolverFactory(sp)));

  internal sealed class BenzeneStartUpCheckHostedService(IServiceProvider services) : IHostedService
  {
      public Task StartAsync(CancellationToken _) { new MicrosoftServiceResolverFactory(services).RunStartUpChecks(); return Task.CompletedTask; }
      public Task StopAsync(CancellationToken _) => Task.CompletedTask;
  }
  ```
  `BuildAzureFunctionApp()` (`Benzene.Azure.Function.Core.TestHelpers`) already runs the checks
  at build time, so tests keep the same behaviour. Trace-only: I could not confirm the Functions
  host's `IHostedService` ordering by running it; it is the documented generic-host contract.

### F4. A declared trigger and its configured pipeline are never cross-checked — `SHOULD-FIX`

- **Clause:** R3.
- **Evidence.** `[assembly: BenzeneServiceBusTrigger(Name = "orders", QueueName = "orders")]` with
  a `Configure` that never calls `UseServiceBus(...)` compiles, generates, and is indexed by the
  host. The first message reaches `src/Benzene.Azure.Function.Core/AzureFunctionApp.cs:71-79`
  and throws the (good) no-entry-point message: *"No entry point application is registered for
  request shape [ServiceBusReceivedMessage[]]. Registered entry points: [...]. Wire the matching
  Use...() extension ..."* — right text, wrong time. The converse — `UseServiceBus(...)` wired
  with no trigger declared or hand-written — is a dead pipeline and nothing says so. Neither
  the generator (compile time, cannot see `Configure`) nor the host (runtime, does not look at
  the assembly attributes) closes the loop.
- **What the user experiences.** Same as F3: the first message is the diagnostic.
- **Fix.** Give the nine attributes a Core base type that carries the request shape, and add a
  start-up check in Core that needs no transport knowledge:
  ```csharp
  // Benzene.Azure.Function.Core
  public abstract class BenzeneTriggerAttribute : Attribute
  {
      public abstract Type RequestType { get; }        // e.g. typeof(ServiceBusReceivedMessage[])
      public virtual  Type? ResponseType => null;      // HTTP: typeof(IActionResult)
  }

  public sealed class DeclaredTriggerStartUpCheck : IStartUpCheck
  {
      public string Name => "declared-trigger";
      public void Check(IServiceResolver resolver)
      {
          var app = resolver.GetService<IAzureFunctionApp>();
          foreach (var t in Assembly.GetEntryAssembly()!.GetCustomAttributes<BenzeneTriggerAttribute>())
              if (!app.HasEntryPoint(t.RequestType, t.ResponseType))
                  throw new BenzeneException($"[assembly: {t.GetType().Name}(Name = \"{t.Name}\")] is declared but Configure never wired a pipeline for {t.RequestType.Name}. Add app.Use{Transport}(...) to your StartUp's Configure.");
      }
  }
  ```
  (`HasEntryPoint` is a one-line addition to `IAzureFunctionApp`.) An advisory log for the
  converse — an entry point with no declared or hand-written `[Function]` — belongs in the same
  check.

### F5. The declared form cannot reach a second trigger of the same type, and nothing detects the collision — `SHOULD-FIX`

- **Clause:** R1 (a routine capability — two queues, two timers, two hubs in one Function App —
  has no shorthand and, for most transports, no explicit form short of `app.Add(key, ...)` at the
  bottom of the ladder), and R3.
- **Evidence.**
  - The discriminator `name` exists on exactly two of nine `Use*` methods:
    `src/Benzene.Azure.Function.QueueStorage/DependencyInjectionExtensions.cs:55` and
    `src/Benzene.Azure.Function.EventGrid/DependencyInjectionExtensions.cs:56`. It is absent from
    `UseServiceBus` (`Function.ServiceBus/DependencyInjectionExtensions.cs:72`), `UseEventHub`
    (`Function.EventHub/Function/DependencyInjectionExtensions.cs:73,90`), `UseKafka` (`:57`),
    `UseTimerTrigger` (`Function.Timer/DependencyInjectionExtensions.cs:64,79`),
    `UseBlobStorage` (`:38`), `UseCosmosDbChangeFeed` (`:31`) and `UseHttp` (`:35`).
  - The generator never emits a name for any transport — e.g.
    `Transports/MessagingTransports.cs:231`
    `HandleQueueMessage(_app, messageText, cancellationToken)` and `:422` for Timer — so even
    where the explicit rung exists the shorthand cannot reach it.
  - Dispatch is first-match: `AzureFunctionApp.cs:44-53` `if ((name == null || key == name) && app is ... typed) return typed.SendAsync(...)`.
  - The Service Bus attribute (`Function.ServiceBus/BenzeneServiceBusTriggerAttribute.cs:13-29`)
    also has no `AutoCompleteMessages`/`IsBatched`, so the declared form cannot express the
    `AckMode = Explicit` path the cookbook's step 5 documents — the doc's "a binding shape the
    attribute doesn't expose" (`docs/azure-functions.md:208`) covers a routine case.
- **What the user experiences.** Two `[assembly: BenzeneTimerTrigger(Name = "nightly", ...)]` /
  `(Name = "hourly", ...)` and two `UseTimerTrigger(...)` pipelines: both ticks run the first
  pipeline, the second is unreachable, nothing logs. Same for two Event Hubs, two Service Bus
  queues (there, routing by the `topic` property often masks it), two Blob paths.
- **Fix.** Three parts, all additive:
  1. `string? name = null` on every `Use*`/`Handle*` (consistency with Queue/EventGrid).
  2. An optional `EntryPoint` property on every `Benzene*TriggerAttribute`, emitted as the
     dispatch name when set:
     ```csharp
     [assembly: BenzeneTimerTrigger(Name = "nightly", Schedule = "0 0 2 * * *", EntryPoint = "nightly")]
     app.UseTimerTrigger(t => t.UsePresetTopic("nightly-cleanup").UseMessageHandlers(), name: "nightly");
     // generated: HandleTimer(_app, "nightly", timer, cancellationToken)
     ```
  3. `AzureFunctionApp`'s constructor throws when two **unkeyed** entry points share a request
     type — that is a start-up check with no false positives, and its message can name both
     `Use*` calls and the `name:` argument that disambiguates them.

### F6. Eight hand-copied `Program.cs` preambles and no `BenzeneFunctionsHost` — `SHOULD-FIX` (duplication x8, +2 templates)

- **Clause:** §4.1 "Building a host ... [is] the framework's work"; examples rule "the third copy
  is a backlog item, the fourth is choosing not to fix it".
- **Evidence.** `grep -rl "ConfigureFunctionsWebApplication()" examples` = 8 files:
  `examples/Azure/Benzene.Example.Azure/Program.cs:7-17` and all seven
  `examples/AzureFunctionsMesh/*/Program.cs:7-12`. Each is the same five statements:
  ```csharp
  var host = new HostBuilder()
      .ConfigureFunctionsWebApplication()
      .UseBenzene<StartUp>()
      .Build();
  host.Run();
  ```
  The other two hosts already have the shorthand — `src/Benzene.HostedService/BenzeneHost.cs:87`
  `BenzeneHost.RunAsync<TStartUp>(args, configureHost)` and
  `src/Benzene.AspNet.Core/BenzeneWebHost.cs:92` — and `examples/Azure/Benzene.Example.Azure.Worker/Program.cs:7-10`
  shows the one-liner is possible on the same machine. The preamble also carries two documented
  traps the shorthand would erase: the `IHostBuilder.UseBenzene<TStartUp>` name collision between
  `Benzene.HostedService` and `Benzene.Azure.Function.Core` (`docs/getting-started-worker.md`
  troubleshooting "Wrong `UseBenzene<TStartUp>()` resolves"; the Worker `Program.cs:7-9` comment)
  and the two-halves `IBenzeneInvocation` opt-in (`docs/azure-functions.md:872-894`,
  `:1012-1015`). The two Azure templates disagree with each other:
  `templates/content/azure-http/Program.cs:6` uses `ConfigureFunctionsWebApplication()`,
  `templates/content/azure-servicebus/Program.cs:6` uses `ConfigureFunctionsWorkerDefaults()`.
- **Fix.** Ship the composition the docs already spell out:
  ```csharp
  // Benzene.Azure.Function.Core
  public static class BenzeneFunctionsHost
  {
      public static IHost Build<TStartUp>(Action<IHostBuilder>? configureHost = null) where TStartUp : BenzeneStartUp, new()
      {
          var builder = new HostBuilder()
              .ConfigureFunctionsWebApplication(worker => worker.UseBenzene())   // IBenzeneInvocation half, always
              .UseBenzene<TStartUp>();
          configureHost?.Invoke(builder);
          return builder.Build();
      }
      public static void Run<TStartUp>(Action<IHostBuilder>? configureHost = null) where TStartUp : BenzeneStartUp, new()
          => Build<TStartUp>(configureHost).Run();
  }
  ```
  Example `Program.cs` becomes `BenzeneFunctionsHost.Run<StartUp>();` (App Insights via
  `configureHost`). Keep the explicit form in `docs/azure-functions.md:162-172` and have the
  shorthand's doc line name it.

### F7. Cloud Run `PORT` plumbing hand-copied x9 (5 in this territory) — `SHOULD-FIX`

- **Clause:** examples rule; §4.1 "hosting is the framework's work".
- **Evidence.** `grep -rl 'GetEnvironmentVariable("PORT")' examples` = 9 files, identical:
  `examples/Google/Benzene.Examples.Google/Program.cs:8-9`,
  `examples/GoogleCloudMesh/{Orders,Payments,Shipping,Notifications,Mesh}/Program.cs:6-7`,
  plus `AzureMesh/Mesh`, `K8sMesh/{Mesh,Service}`:
  ```csharp
  await BenzeneWebHost.RunAsync<Startup>(args, builder => builder.WebHost.UseUrls(
      $"http://0.0.0.0:{Environment.GetEnvironmentVariable("PORT") ?? "8080"}"));
  ```
  `docs/getting-started-google.md` never shows `Program.cs` at all, although the README it points
  to calls Cloud Run "the recommended target" (`examples/Google/README.md:17-20`).
- **Fix.** Either honour `PORT` in `BenzeneWebHost` when `ASPNETCORE_URLS` is unset (it is the
  container contract for Cloud Run, Knative and Heroku-style hosts), or ship
  `builder.ListenOnPortFromEnvironment()`; then `Program.cs` is
  `await BenzeneWebHost.RunAsync<Startup>(args);` and the guide can show it.

### F8. `AsEventHubBenzeneMessage()` exists in two packages with two incompatible wire shapes, and the Functions trigger's property-routed path has no test helper — `SHOULD-FIX`

- **Clause:** R4 and consistency.
- **Evidence.** `src/Benzene.Azure.Function.EventHub.TestHelpers/MessageBuilderExtensions.cs:11-22`
  builds an **envelope body** (`EventBody = new BinaryData(source.AsBenzeneMessage(serializer))`);
  `src/Benzene.Azure.EventHub.TestHelpers/MessageBuilderExtensions.cs:24-47` (self-hosted) builds a
  **`topic` property + raw body** — its own doc comment (`:9-13`) records the difference. Same
  method name, same `IMessageBuilder<T>` receiver. Meanwhile the Functions trigger's property-routed
  path — `UseEventHub(eh => eh.UseMessageHandlers())`, which is what
  `examples/AzureFunctionsMesh/Inventory/StartUp.cs:32` and `Notifications/StartUp.cs:36` use —
  has no helper in the Functions `TestHelpers` package, so the only shipped helper cannot exercise
  the pipeline those examples wire.
- **What the user experiences.** A `using` swap between the two packages changes the wire shape
  under a test that keeps compiling; a test of the mesh examples' Event Hub path has to hand-build
  `EventData` (the `Benzene.Azure.EventHub.TestHelpers` one would work but lives in the worker
  package).
- **Fix.** In the Functions helper: keep `AsEventHubBenzeneMessage()` for the envelope path but
  document it as such, and add `AsEventHubMessage()` (property-routed) with the same body as the
  worker helper; mention both in `docs/azure-functions.md:941-945`.

### F9. `UseMessageHandlers()` scans every loaded assembly; the guides steer to it; the only check is an advisory log — `SHOULD-FIX`

- **Clause:** R3 and R4.
- **Evidence.** `src/Benzene.Core.MessageHandlers/Extensions.cs:58-61`:
  ```csharp
  public static IMiddlewarePipelineBuilder<TContext> UseMessageHandlers<TContext>(this IMiddlewarePipelineBuilder<TContext> app)
  {
      return app.UseMessageHandlers(AppDomain.CurrentDomain.GetAssemblies());
  }
  ```
  Which assemblies are loaded when `Configure` runs depends on what the JIT has touched — a
  handler assembly nothing in the start-up path references is silently not scanned. Both
  getting-started guides steer to this overload with empty `ConfigureServices`
  (`docs/azure-functions.md:143-152`, `docs/getting-started-google.md:88-102`), while the
  troubleshooting entry in the same file tells the opposite story
  (`docs/azure-functions.md:1009-1011`: *"handlers are discovered by reflection over that
  assembly [`AddMessageHandlers`], not auto-registered globally"*). The finder is `TryAddSingleton`
  (`src/Benzene.Core.MessageHandlers/DI/Extensions.cs:211`) — first registration wins, which
  `examples/Azure/Benzene.Example.Azure/DependenciesBuilder.cs:60-67` documents as a trap it fell
  into. The only start-up verification is `EmptyHandlerRegistryStartUpCheck`
  (`StartUpChecks/EmptyHandlerRegistryStartUpCheck.cs:32-45`), which logs (does not throw) and
  only when **zero** handlers were found; a partially-registered set passes.
- **What the user experiences.** 404/"no handler for topic" per message, which reads as a
  routing bug; the two doc paragraphs disagree about which call is the explicit form.
- **Fix.** (a) A start-up check that reflects the entry assembly (and, cheaply, assemblies it
  references) for `[Message]` types absent from the finder and names them with the
  `AddMessageHandlers(typeof(X).Assembly)` line to add; (b) make the no-arg overload scan the
  entry assembly rather than "whatever is loaded", or document the current semantics verbatim;
  (c) one sentence in both guides: `AddMessageHandlers(assembly)` in `ConfigureServices` is the
  explicit form, `UseMessageHandlers()` is the shorthand over it.

### F10. The framework's own Azure templates do not take the framework's steer — `SHOULD-FIX`

- **Clause:** R4 (the ladder must be visible from the top — a `dotnet new` template is the top).
- **Evidence.** `templates/content/azure-http/HttpFunction.cs:11-26` and
  `templates/content/azure-servicebus/ServiceBusFunction.cs:10-25` hand-write the `[Function]`
  class the generator exists to replace; neither template mentions `[assembly: Benzene*Trigger]`.
  Both override `GetConfiguration()` with the base default plus a no-op `SetBasePath`
  (`azure-http/StartUp.cs:16-22`, `azure-servicebus/StartUp.cs:15-21`), which
  `src/Benzene.Microsoft.Dependencies/BenzeneStartUp.cs:17-28` was made virtual precisely to
  remove. (I read only these two templates; `azure-eventhub`/`azure-eventgrid`/
  `azure-queuestorage` are listed in `templates/README.md:24-27` and were not opened.)
- **Fix.** Replace each `*Function.cs` with a `Triggers.cs` containing the one attribute and a
  comment naming the hand-written form as the escape hatch (`docs/azure-functions.md:204-240`);
  delete the `GetConfiguration()` overrides; align the two `Program.cs` files (or use F6's host).

### F11. Every Azure cookbook shows only the hand-written trigger; one snippet cannot compile — `SHOULD-FIX`

- **Clause:** R4.
- **Evidence.** `grep -n "assembly:" docs/cookbooks/service-bus-handling.md
  docs/cookbooks/event-hub-processing.md docs/cookbooks/cosmos-change-feed-processing.md
  docs/cookbooks/managed-identity.md docs/testing-benzene.md docs/hosting.md` returns nothing.
  Each cookbook's step 1 is a full `[Function]` class (`service-bus-handling.md:61-81`,
  `event-hub-processing.md:65-87`, `cosmos-change-feed-processing.md:106-131`) presented as "the
  trigger", so a reader arriving through a cookbook never learns the shorthand exists.
  `cosmos-change-feed-processing.md:88`:
  ```csharp
  public override void Configure(IBenzeneApplicationBuilder app)
  ```
  does not match `BenzeneStartUp.Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)`
  (`BenzeneStartUp.cs:33`); the snippet has no `<!-- compile: -->` marker, so
  `DocSnippetsCompileTest` does not cover it (trace-only).
- **Fix.** One paragraph per cookbook ("declare it: `[assembly: BenzeneServiceBusTrigger(...)]`
  — the class below is what that generates, for when you need `AutoCompleteMessages`/`IsBatched`"),
  fix the signature, add the compile marker.

### F12. Outbox relay on Azure Functions exists only as an undocumented hand-composition — `SHOULD-FIX`

- **Clause:** R1 (a routine capability with no shorthand and no documented explicit form is
  unfinished).
- **Evidence.** `docs/cookbooks/transactional-outbox.md:229-230`: *"Other FaaS (Azure Functions,
  ...). Same shape — a change-feed/timer trigger calling into `IOutboxDispatcher` — deferred
  alongside a future Cosmos store."* Yet `Benzene.Outbox.EntityFramework` (relational, host-agnostic)
  and `Benzene.Azure.Function.Timer` (`UseTick`, `Extensions.cs:21-44`) both ship, and the
  composition is three lines a user would have to discover for themselves.
- **Fix.** Either a terminal on the Timer pipeline or a documented recipe — the former is the
  shorthand over the latter:
  ```csharp
  // Benzene.Outbox (or Benzene.Azure.Function.Timer, referencing Benzene.Outbox)
  public static IMiddlewarePipelineBuilder<TimerContext> UseOutboxSweep(this IMiddlewarePipelineBuilder<TimerContext> timer)
      => timer.Use(resolver => new FuncWrapperMiddleware<TimerContext>("OutboxSweep",
          async (_, next) => { await resolver.GetService<IOutboxDispatcher>().RunOnceAsync(); await next(); }));

  // StartUp: [assembly: BenzeneTimerTrigger(Name = "outbox-sweep", Schedule = "0 */1 * * * *")]
  app.UseTimerTrigger(timer => timer.UseOutboxSweep());
  ```
  and replace the "deferred" sentence with it.

### F13. Wire-name override honoured by the self-hosted consumers but not the Functions triggers; `topicPropertyKey` on `UseServiceBus` but not `UseEventHub` — `SHOULD-FIX`

- **Clause:** consistency across the Azure surface; a spec convention that costs differently by host.
- **Evidence.** Self-hosted: `src/Benzene.Azure.ServiceBus/DependencyInjectionExtensions.cs:50-53,74-79`
  and `src/Benzene.Azure.EventHub/DependencyInjectionExtensions.cs:47-50,71-76` resolve the key via
  `ResolveTopicPropertyKey` (`IBenzeneWireNames`, wire-contracts §2). Functions:
  `src/Benzene.Azure.Function.ServiceBus/DependencyInjectionExtensions.cs:47-48` and
  `src/Benzene.Azure.Function.EventHub/Function/DependencyInjectionExtensions.cs:46` construct
  `new ...TopicGetter(topicPropertyKey)` from the literal. `UseServiceBus` exposes
  `topicPropertyKey` (`:72`); `UseEventHub` does not (`:73,:90`) although `AddAzureEventHub(string)`
  (`:41`) exists.
- **What the user experiences.** A fleet-wide `IBenzeneWireNames` registration applies to the
  worker-hosted services and silently not to the Function-hosted ones.
- **Fix.** Copy `ResolveTopicPropertyKey` into the two Functions `Add*` methods; add
  `topicPropertyKey` to both `UseEventHub` overloads.

### F14. Test seams the examples hand-roll twice, and an eager SDK client that leaks into tests — `SHOULD-FIX` (duplication x2)

- **Clause:** examples rule ("the second copy is a signal").
- **Evidence.** `class FakeBenzeneMessageSender` exists in
  `examples/Aws/Benzene.Examples.Aws.Tests/Helpers/FakeBenzeneMessageSender.cs` and
  `examples/Azure/Benzene.Example.Azure.Test/Helpers/FakeBenzeneMessageSender.cs`; `Benzene.Testing`
  has no `IBenzeneMessageSender` type (grep). Separately, the Azure example constructs a real
  `ServiceBusClient` eagerly (`examples/Azure/Benzene.Example.Azure/DependenciesBuilder.cs:55-58`),
  so both test projects must plant a fake connection string before building the host
  (`Benzene.Example.Azure.Test/StartUpTest.cs:28-36`, `Benzene.Example.Azure.Dev.Test/PublishOrderCreatedServiceBusTest.cs:34-37`)
  — while the sibling Azure example registers senders lazily
  (`examples/AzureFunctionsMesh/Shared/MeshServiceWiring.cs:101-108`). Two Azure examples, two
  patterns for one thing.
- **Fix.** `Benzene.Testing.RecordingMessageSender` + `BenzeneTestHostBuilder.WithRecordingSender()`;
  register the example's sender lazily (or via `pipeline.UseServiceBus(sb => sb.UseServiceBusClient())`
  resolving the client from DI, the shape the mesh example already uses).

### F15. Mesh service-side plumbing: the Cloud Service preamble re-derived x3, `ServiceHealthCheck` x8, handler list stated three times, cross-cutting middleware re-declared per pipeline — `POLISH` (counts below)

- **Clause:** examples rule.
- **Evidence.** `MeshServiceWiring.cs` exists three times at three sizes — 84 lines
  (`examples/GoogleCloudMesh/Shared`), 138 (`examples/AzureFunctionsMesh/Shared`), 309
  (`examples/AwsMesh/Shared`) — each re-deriving `SetApplicationInfo`/`AddDiagnostics`/
  `AddMessageHandlers`/`AddHttpMessageHandlers` + `UseHttp(enrichment, metrics, UseBenzeneCloudService(...))`.
  `class ServiceHealthCheck` is defined 8 times (six byte-identical copies in
  `examples/AzureFunctionsMesh/*/Domain.cs:77-89`, one in `GoogleCloudMesh/Shared/ServiceHealthCheck.cs`,
  one in `K8sMesh/Service/Domain.cs`) — an always-healthy "self" check whose only job is profile
  conformance. Each service's `Handlers` list is passed three times
  (`examples/AzureFunctionsMesh/Payments/StartUp.cs:24,28,43,48`; `Shared/MeshServiceWiring.cs:65,91`)
  even though `WithHandlers` is optional (`src/Benzene.CloudService/CloudServiceBuilder.cs:40-46`:
  *"when omitted, handlers come from the container's existing registrations"*) and nothing says
  eager derivation is why. `UseBenzeneEnrichment()` appears 16 times and `UseBenzeneMetrics()` 8
  times across the Azure/GCP examples because each transport pipeline re-declares them.
  `(IMessageDefinition)new ResponseEventDefinition(...)` casts in every mesh `StartUp` are
  overload-disambiguation ceremony on `AddResponseEventDeclarations`.
- **Fix.** A framework `SelfHealthCheck(name)` (or make `WithHealthChecks` optional with that
  default); a comment on `WithHandlers` in the examples naming the trade-off; the mesh/cloud-service
  owner should look at the 3x preamble; the per-pipeline decorator repetition is a Core question
  (a "decorate every pipeline" seam) worth raising there.

### F16. `examples/Azure` and `examples/Google` carry plumbing the framework already provides — `POLISH`

- **Clause:** examples rule.
- **Evidence.**
  - `GetConfiguration()` overrides reproducing the base default:
    `examples/Azure/Benzene.Example.Azure/StartUp.cs:19-22` → `DependenciesBuilder.cs:24-30`
    (`SetBasePath` with no file provider is a no-op); `examples/Google/Benzene.Examples.Google/Startup.cs:25-31`;
    `Benzene.Example.Azure.Worker/StartUp.cs:23-26` (this one legitimately adds `config.json`).
    `BenzeneStartUp.cs:19-22` names this exact body as the ceremony the virtual default removed.
  - `DependenciesBuilder.cs:44-47` hand-registers the `benzene:spec` `IMessageHandlerDefinition`/
    `HttpEndpointDefinition` that `UseSpec()` already registers
    (`src/Benzene.Schema.OpenApi/Extensions.cs:89-95`) — a second definition for the same handler.
  - `StartUp.cs:32` `.OnRequest("strip-api", ...)` **and** `host.json:13` `"routePrefix": ""` —
    two mechanisms for one prefix; the doc (`docs/azure-functions.md:271-274`) recommends the
    latter only. Neither carries a "deliberate demonstration" comment.
  - `getting-started-google.md:8` says the guide follows `examples/Google`; the guide's `Startup`
    has an empty `ConfigureServices` and no `GetConfiguration`, the example has five registrations
    and the override; the guide scaffolds `dotnet new console`, the example is `Microsoft.NET.Sdk.Web`.
    Ceremony honesty: the guide is 3 lines lighter than the thing it claims to be.
- **Fix.** Delete the overrides and the spec duplicate; pick one prefix mechanism; make the
  Google guide and example match (either direction).

### F17. Google example tests carry AWS leftovers and a missing response-reading helper — `POLISH`

- **Evidence.** `examples/Google/Benzene.Examples.Google.Tests/Helpers/EnvironmentSetUp.cs:9-10`
  sets `AWS_SERVICE_URL`/`MY_QUEUE_URL` (called from `InMemoryOrdersTestBase.cs:25`) in a Google
  test suite; `Helpers/DirectMessageBuilder.cs` declares `class BenzeneMessageBuilder` (file/class
  mismatch; no non-helper caller found by grep); `Helpers/MessageExtensions.cs:9-14` reads the
  response body with Newtonsoft because `Benzene.GoogleCloud.Functions.Http.TestHelpers`'
  `HttpContextBuilder` builds requests and nothing reads responses.
- **Fix.** Add `ReadBodyAsync<T>()`/`Body<T>()` on `HttpContext`/`HttpResponse` to the TestHelpers;
  delete the AWS leftovers.

### F18. Naming and shape asymmetries across the Azure/GCP surface — `POLISH`

| Where | A | B | Cost to the user |
|---|---|---|---|
| Class name in guides/examples | `StartUp` (`docs/azure-functions.md:132`, `getting-started-aws.md:115`, `examples/Azure`) | `Startup` (`docs/getting-started-google.md:83`, `examples/Google`) | copy between guides → compile error |
| Event Hub trigger namespaces | attribute in `Benzene.Azure.Function.EventHub` (`Inventory/Triggers.cs:3`) | extensions in `Benzene.Azure.Function.EventHub.Function` (`Inventory/StartUp.cs:3`) | two `using`s for one package; every other package is one namespace |
| Test-helper verbs | `AsAzureServiceBusMessage`, `AsAzureKafkaEvent` | `AsEventHubBenzeneMessage`, `AsEventGridBenzeneMessage`, `AsQueueStorageBenzeneMessage`, `AsPubSubEvent` | no rule to guess the next one |
| Exception option | `CatchExceptions` (all `Benzene.Azure.Function.*Options`) | `CatchHandlerExceptions` (`BenzeneEventHubConfig`, `BenzeneCosmosChangeFeedConfig`) | same knob, two names |
| Ack mode | `ServiceBusAckMode`, default `AutoComplete` (`Function.ServiceBus/ServiceBusOptions.cs:34`) | `ServiceBusConsumerAckMode`, default `Explicit` (`Benzene.Azure.ServiceBus`) | two enums, opposite defaults, documented but easy to conflate |
| Fan-out cap | `UseEventHub(action, int? maxDegreeOfParallelism)` positional overload (`:73`) | every other transport: `Options.MaxDegreeOfParallelism` only | one-off overload |
| Preset topic | Azure SB/Queue/EventGrid/Timer: `UsePresetTopic` wired | Google Pub/Sub: not wired (its `CLAUDE.md` says so) | a non-Benzene producer routes as `Missing` on GCP only |
| Builder recognition | HTTP: `app is IAspApplicationBuilder` (interface) | Pub/Sub: `app is GooglePubSubFunctionApplicationBuilder` (`DependencyInjectionExtensions.cs:72`, concrete) | a third-party host cannot satisfy `UsePubSub` |

### F19. Documentation drift and small invisible rungs — `POLISH`

- `docs/testing-benzene.md:82-86`: *"Azure's `BenzeneMessage` bridge today only exists over Event
  Hub"* — Queue Storage has it (`docs/azure-functions.md:640`); the section omits
  `HandleServiceBusMessages`/`HandleQueueMessages`/`HandleEventGridEvent`/`HandleTimer`.
- `docs/azure-functions.md:941-945` lists EventHub/Kafka/ServiceBus TestHelpers; EventGrid and
  QueueStorage TestHelpers exist and are not named.
- `docs/cookbooks/entity-framework-integration.md:92-98` hand-constructs
  `new DatabaseConnectionHealthCheck<OrdersDbContext>(dbContext)` and never names the shorthand
  `AddDatabaseConnectionHealthCheck<T>()` (`src/Benzene.HealthChecks.EntityFramework/Extensions.cs:30`).
- Nothing tells a user how to *see* the generated trigger class (`EmitCompilerGeneratedFiles`
  appears nowhere in the repo; the IDE "Analyzers" node is not mentioned). R4 for generated code
  is "name the explicit form" — done well at `docs/azure-functions.md:239` — but inspectability
  is one sentence away.
- `FunctionsEnableWorkerIndexing=true` (a user override, or a `ProjectReference` consumer who
  missed `:198-202`) surfaces as Microsoft's *"No job functions found"*. The generator can read
  `build_property.FunctionsEnableWorkerIndexing` from `AnalyzerConfigOptions` and report a
  `BENZ0012` that names the property; the direct-extension-package requirement (`:366-369`)
  cannot be detected and is correctly left to the doc.
- `src/Benzene.Azure.Function.Core/Benzene.Azure.Function.Core.csproj` pins `Azure.Identity 1.11.4`
  with no code reference in the package (grep), and `docs/cookbooks/managed-identity.md:48` cites
  that pin as a fact for users.

---

## Boilerplate ledger (examples)

Statement-level counts; `using`, blank and brace-only lines excluded. **D** domain, **I** intent
(what it handles / talks to / needs), **P** plumbing. Every plumbing line is classified as either a
missing shorthand (→ finding) or a deliberate demonstration (→ needs a comment saying so).

| File | D | I | P | The plumbing, and which category |
|---|---|---|---|---|
| `examples/Azure/Benzene.Example.Azure/Program.cs` | 0 | 1 | 6 | HostBuilder/ConfigureFunctionsWebApplication/AppInsights x2/Build/Run → missing shorthand (F6) |
| `.../StartUp.cs` | 0 | 12 | 7 | `GetConfiguration` override (3), delegation to `DependenciesBuilder` (3), `strip-api` (1) → F16 |
| `.../Triggers.cs` | 0 | 3 | 0 | clean — the shorthand working as intended |
| `.../DependenciesBuilder.cs` | 0 | 6 | 19 | `GetConfiguration` (5), `CreateServiceResolverFactory` (5), `AddSingleton(configuration)` (1), spec definition duplicate (4), eager `ServiceBusClient`/sender (4) → F14, F16 |
| `.../PublishOrderCreatedMessageHandler.cs` | 10 | 2 | 0 | clean |
| `examples/Azure/Benzene.Example.Azure.Worker/Program.cs` | 0 | 1 | 0 | clean — `BenzeneHost.RunAsync` is the shorthand F6 asks for on Functions |
| `.../Worker/StartUp.cs` | 0 | 12 | 16 | `GetConfiguration` (3), delegation (3), `EventProcessorClient` construction incl. blob container create (10) → the last is the documented factory seam and carries a comment: deliberate |
| `.../Worker/DependenciesBuilder.cs` | 0 | 3 | 11 | `GetConfiguration` (6), `AddSingleton(configuration)`/`AddLogging` (2), timer factory (3) → F16 |
| `examples/AzureFunctionsMesh/<svc>/Program.cs` (x7) | 0 | 1 | 4 | identical preamble x7 → F6 |
| `examples/AzureFunctionsMesh/Orders/StartUp.cs` | 0 | 9 | 3 | delegation x2, `(IMessageDefinition)` cast → F15 |
| `.../Orders/Triggers.cs` | 0 | 1 | 0 | clean |
| `.../Orders/Domain.cs` | 40 | 2 | 12 | `ServiceHealthCheck` (x6 across services) → F15 |
| `examples/AzureFunctionsMesh/Shared/MeshServiceWiring.cs` | 0 | 15 | 30 | OTel/Azure Monitor exporter (8), three lazy SDK-client factories (18), region env (1) → F15 |
| `examples/Google/Benzene.Examples.Google/Function.cs` | 0 | 1 | 0 | clean |
| `.../Program.cs` | 0 | 1 | 1 | `PORT` → F7 |
| `.../Startup.cs` | 0 | 6 | 5 | `GetConfiguration` override → F16 |
| `examples/GoogleCloudMesh/<svc>/Functions.cs` (x4) | 0 | 1-2 | 0 | clean |
| `.../<svc>/Program.cs` (x4) | 0 | 1 | 1 | `PORT` x4 → F7 |
| `.../Orders/Startup.cs` | 0 | 8 | 3 | delegation x2, cast → F15 |
| `examples/GoogleCloudMesh/Shared/MeshServiceWiring.cs` | 0 | 12 | 6 | `AddLogging`, region env, lazy publisher → F15 |

Corpus-wide duplication counts (the argument): Functions `Program.cs` preamble **x8**
(+2 templates); Cloud Run `PORT` **x9**; `MeshServiceWiring.cs` **x3**; `ServiceHealthCheck`
**x8**; `GetConfiguration()`-equals-default **x3** in this territory (+2 templates);
`FakeBenzeneMessageSender` **x2**; `UseBenzeneEnrichment()` **x16** / `UseBenzeneMetrics()` **x8**
re-declared per pipeline; `host.json routePrefix ""` **x8**.

---

## Ceremony parity: the same capability on each host (trace-only line counts from the files above)

| Capability: consume one queue/topic and route to a `[Message]` handler | Trigger/entry point | `Configure` | Host `Program.cs` | Build conventions | Test |
|---|---|---|---|---|---|
| Azure Functions Service Bus (`examples/Azure`) | 1 line (`Triggers.cs:11`) | 3 lines (`StartUp.cs:47-49`) | 5-11 lines, hand-copied (F6) | direct `Microsoft.Azure.Functions.Worker.Extensions.ServiceBus` ref + `FunctionsEnableWorkerIndexing=false` (auto via NuGet) | `BuildAzureFunctionApp()` + `AsAzureServiceBusMessage()` |
| Google Cloud Functions Pub/Sub (`examples/GoogleCloudMesh/Payments`) | 1 line (`Functions.cs:10`) | 3 lines (`MeshServiceWiring.cs:73-75`) | none needed for Functions (Cloud Run: 2 lines, F7) | `--trigger-topic` at deploy; one trigger per function → a second class for HTTP | `BuildGooglePubSubFunctionHost()` + `AsPubSubEvent()` |
| Self-hosted Service Bus worker (`examples/Azure/...Worker`) | none | ~10 lines (config object + client factory + pipeline, `StartUp.cs:35-39,46-57`) | 1 line (`BenzeneHost.RunAsync`) | none | live emulator only (`docs/getting-started-worker.md` "Testing") |

Pub/Sub is the cheapest and Service Bus-on-Functions the most convention-laden; the difference is
legitimately the platform (Functions' worker-indexing model and extension discovery), and the
docs say so (`docs/azure-functions.md:198-202,366-369`). The one *illegitimate* difference is
`Program.cs` (F6): the worker has a one-line host, Functions has none.

---

## Capability → explicit form → shorthand → documented?

| Capability | Explicit form (public?) | Shorthand | Shorthand doc names the explicit form? |
|---|---|---|---|
| Host a Function App | `new HostBuilder().ConfigureFunctionsWebApplication().UseBenzene<T>().Build().Run()` (`docs/azure-functions.md:162-172`, `hosting.md:236-260`) | **none** (F6) | n/a |
| HTTP trigger | hand-written `[Function]`/`[HttpTrigger]` calling `HandleHttpRequest` (`:210-240`) | `[assembly: BenzeneHttpTrigger]` (`:186-196`) | **yes** — `:239` "The declaration above generates exactly this class" |
| Service Bus / Event Hub / Kafka / Queue / Blob / Event Grid / Cosmos trigger | hand-written class per transport (`:414-435, 460-480, 514-535, 590-615, 652-672, 705-727, 764-784`) | `[assembly: Benzene*Trigger]` (`:354-364`) | yes in the guide (`:371-373`); **no** in any cookbook (F11) |
| Timer trigger | hand-written, forwards `TimerTriggerInfo` (`:822-841`) | `[assembly: BenzeneTimerTrigger]` | doc says yes; generated code is **not** the explicit form (F1) |
| Two triggers of one type in one app | `Use*(name:)` + `Handle*(name, …)` — Queue/EventGrid only; otherwise `app.Add(key, …)` (bottom rung) | **none** (F5) | `Benzene.Azure.Function.Core/CLAUDE.md` only |
| Per-message explicit ack (SB) | `AckMode = Explicit` + `AutoCompleteMessages=false` + `ServiceBusMessageActions` overload (`service-bus-handling.md` step 5) | none — attribute lacks the binding fields (F5) | hand-written form documented; attribute limitation not named |
| Test an Azure Function App | `InlineAzureFunctionStartUp` (`:951-966`) | `BenzeneTestHost.Create<T>().BuildAzureFunctionApp()` (`:928-940`) | yes (`:937-939`) |
| Self-hosted SB/EH/Cosmos consumer | `worker.Add(rf => new BenzeneServiceBusWorker(...))` (Part A of worker guide) | `worker.UseServiceBus(config, factory, pipeline)` (Part B) | yes (`getting-started-worker.md` "How UseWorker composes…") |
| Google HTTP function | implement `IHttpFunction` over `GoogleCloudStartUpRunner.Bootstrap` + `GoogleCloudFunctionApplicationBuilder` (public; `CLAUDE.md` only) | `class Function : GoogleCloudFunctionHost<Startup>` | rung below is public but appears in no user doc |
| Google Pub/Sub function | same shape, `GooglePubSubFunctionApplicationBuilder` | `class PubSubFunction : GooglePubSubFunctionHost<Startup>` | `getting-started-google.md:135-140` only points at the mesh example; no `UsePubSub` snippet |
| Cloud Run hosting | `WebApplication.CreateBuilder` / `UseBenzene<T>()` / `app.UseBenzene()` | `BenzeneWebHost.RunAsync<T>` (named in `examples/Google/Program.cs:6-7`) | yes in code comment; `PORT` plumbing not covered (F7) |
| Outbox relay on Functions | `UseTimerTrigger(t => t.UseTick(_ => dispatcher.RunOnceAsync()))` — undocumented | **none** (F12) | no ("deferred") |
| EF health check | `new DatabaseConnectionHealthCheck<T>(db)` (cookbook) | `AddDatabaseConnectionHealthCheck<T>()` | cookbook shows explicit only (F19) |
| Managed identity for triggers | app settings only | n/a — zero code | yes, honestly (`managed-identity.md:247-284`) |
| Claim-check store on Blob | `AddBlobClaimCheckStore(BlobContainerClient)` | `AddBlobClaimCheckStore(Uri, containerName)` (`DefaultAzureCredential`) | `CLAUDE.md` only; hydration for Azure contexts honestly documented as not wired |
| Handler discovery | `AddMessageHandlers(assembly)` in `ConfigureServices` | `UseMessageHandlers()` (AppDomain scan) | guides contradict each other on which is which (F9) |

---

## What is genuinely good

- **The HTTP trigger ladder is the model the rest of the port should copy.** The shorthand is
  one assembly attribute; the explicit form sits directly beneath it in a `<details>` block with
  the sentence "The declaration above generates exactly this class" (`docs/azure-functions.md:204-240`);
  the generated code is built only from public API (`IAzureFunctionApp`, `Extensions.HandleHttpRequest`)
  in a `public sealed` class the user could have typed; hand-written and generated coexist.
- **The generator pays for its convention at build time, correctly.** `BENZ0001`–`BENZ0011`
  (`DiagnosticDescriptors.cs`) each say what was looked for and what to set, fail the build rather
  than emit a broken binding, and refuse to auto-rename a colliding `Name` because the name is
  externally meaningful — a textbook R3 stance, tested case by case.
- **`AzureFunctionApp`'s no-entry-point message** lists the requested shape, the registered
  shapes, and the `Use…()` call to add (`AzureFunctionApp.cs:84-104`, pinned by
  `AzureFunctionAppErrorMessageTest`). Only its timing is wrong (F3/F4).
- **`BenzeneStartUp.GetConfiguration()` going virtual** with the remark "23 of the 50 StartUps in
  this repo had this exact body" (`BenzeneStartUp.cs:17-28`) is exactly the §4.1 move: a steer
  should cost a line when you want something different, not when you want the default.
- **The start-up check family** (pipeline-resolution, terminal-middleware, duplicate-topic) is the
  right mechanism, every failure is reported at once, and the aggregate message names the switch
  that softens it (`BenzeneStartUpCheckExtensions.cs:127-136`). F2/F3 are about wiring it in,
  not about its design.
- **`TryAdd` everywhere the transport registers a default** so a user registration in
  `ConfigureServices` wins without ceremony (`Function.ServiceBus/DependencyInjectionExtensions.cs:45-52`
  and siblings).
- **`GoogleCloudFunctionApplicationBuilder : IAspApplicationBuilder`** means one `Startup` runs on
  Cloud Run and Cloud Functions unchanged, and the Google host fails at cold start with "call
  `app.UseHttp(...)`" when `Configure` forgot to — the right check at the right time
  (`GoogleCloudFunctionApplicationBuilder.cs:62-71`, tested).
- **Managed identity is zero code** by design and the cookbook says so plainly, including the
  Consumption-plan caveat.
- **Cosmos and Blob are honest about not routing** (`UseStream`/`UseBlob` as the terminal
  sugar), and the Cosmos trigger/worker share one pipeline shape so handlers port between them.
- **`Triggers.cs` files in the examples are pure intent** — the one place in the corpus where the
  ledger is domain/intent only.
- **Example plumbing is at least concentrated**: `MeshServiceWiring.cs` and `DependenciesBuilder.cs`
  keep it in one file per example rather than smeared across services, which is why the counts
  above were cheap to take.
