# Ergonomics review: the first-service journey and ladder rungs 1–3 (benzene-dotnet @ f3f1be5)

Reviewer: cross-language ergonomics champion (spec repo). Normative source:
`docs/specification/design-principles.md` §4.1 "The shorthand ladder" in the `benzene` repo.
Territory: `docs/getting-started*.md`, `docs/message-handlers.md`, `docs/middleware.md`,
`docs/hosting.md`, `docs/message-result.md`, `templates/content/**`, `examples/{App,Asp,Grpc,Cqrs,Versioning}`,
and the packages `Benzene.Core*`, `Benzene.Abstractions.*`, `Benzene.Http`, `Benzene.AspNet.Core`,
`Benzene.Grpc(.AspNet)`, `Benzene.SelfHost`, `Benzene.HostedService`, `Benzene.Microsoft.Dependencies`,
`Benzene.Autofac`, `Benzene.Results`, `Benzene.Testing`.

## Executive verdict

1. **Go-live blockers: 2. Should-fix: 9. Polish: 8.** Verdict for the first-service journey: **NEEDS CHANGES** before go-live; the library underneath is in far better shape than the journey that leads to it.
2. The two blockers are the same shape of failure: **the flagship no-cloud path (getting-started-aspnet.md §4, the `benzene.asp` template, the Grpc example) goes through `IApplicationBuilder.UseBenzene(Action)`, which never runs the start-up checks and builds a second, split service provider** — the one path a newcomer is most likely to take is the one path that forfeits §4.1's "price of a convention". And **no host constructs discovered handlers at start-up**, so the single most common first-service mistake (a handler dependency nobody registered) is found by the first message.
3. Ceremony is the dominant should-fix: across 12 templates the same 3-line observability incantation (`.AddDiagnostics()` / `.UseBenzeneEnrichment()` / `.UseLogResult(_ => { })`) is copied **12 times**, a redundant `GetConfiguration` override **13 times**, a redundant `.AddBenzene()` **11 times**, and the rung-1 in-process host is hand-rolled **7 times** with no shorthand at all.
4. The ladder itself is real and well built: `BenzeneHost`/`BenzeneWebHost` compose exactly the public five-line form and say so; the start-up-check phase reports every failure and names the knob; handler declaration is three lines of pure intent and byte-identical across every template. Do not touch those.
5. **Method limits:** no `dotnet` SDK exists in this sandbox. Nothing here was built, run, or tested; every compile/behaviour claim is a trace through source, marked "(trace-only)". The `<!-- compile: quickstart -->` markers in five docs are consumed by nothing in `.github/workflows/` (grep), so the docs' snippets are not compile-checked by CI either — treat every doc snippet as trace-only too.

---

## Findings (ordered by severity)

### F1 — BLOCKER — The documented first ASP.NET service skips the start-up checks and splits the container

**§4.1 clause:** "The price of a convention is a start-up check … A convention that can first fail on the message path has not paid for itself." Also rule 2: "A shorthand MUST be composed from the public explicit form, never parallel to it" — there are two `UseBenzene` overloads on `IApplicationBuilder` with different lifecycles and different guarantees.

**Evidence.**

`src/Benzene.AspNet.Core/BenzeneExtensions.cs:106-111` — the inline overload the docs and template use:

```csharp
public static IApplicationBuilder UseBenzene(this IApplicationBuilder app, Action<IAspApplicationBuilder> builder)
{
    var aspApplicationBuilder = new AspApplicationBuilder(app);
    aspApplicationBuilder.Register(x => x.AddBenzene());
    builder(aspApplicationBuilder);
    return app;
}
```

`src/Benzene.AspNet.Core/BenzeneExtensions.cs:172-181` — the `StartUp` overload, the only one that checks:

```csharp
public static IApplicationBuilder UseBenzene(this IApplicationBuilder app)
{
    var holder = app.ApplicationServices.GetRequiredService<BenzeneStartUpHolder>();
    holder.AspApplicationBuilder.Finish(app);
    // Check the wiring while the app is still being built, so a registration mistake fails
    // start-up instead of turning into a 404 or a 500 on the first request that reaches it.
    new MicrosoftServiceResolverFactory(app.ApplicationServices).RunStartUpChecks();
    return app;
}
```

`src/Benzene.AspNet.Core/AspApplicationBuilder.cs:63-71, 73-85, 115-117` — the constructor the inline overload uses documents its own defect: it `Reopen()`s a *copy* of the registrations and builds "a second `IServiceProvider` … meaning a singleton registered outside this container … is a different instance". `MicrosoftBenzeneServiceContainer.Reopen()` (`src/Benzene.Microsoft.Dependencies/MicrosoftBenzeneServiceContainer.cs:20-33`) confirms the copy.

Who is on this path (trace-only):

| Place | Line | Call |
|---|---|---|
| `docs/getting-started-aspnet.md` | 129-131 | `app.UseBenzene(benzene => benzene.UseHttp(http => http.UseMessageHandlers()))` |
| `templates/content/asp/Program.cs` | 30-34 | same |
| `examples/Grpc/Benzene.Example.Grpc/Program.cs` | 32-36 | `app.UseBenzene(x => x.UseGrpc(grpc => grpc.UseMessageHandlers()))` |
| `examples/Asp/Benzene.Example.Asp/Startup.cs` | 103-109, 133, 160 | three `UseBenzene(benzene => …)` calls |
| `docs/asp-net-core.md` | 242-247 | "Wiring without `BenzeneStartUp`" |
| `src/Benzene.Grpc.TestHelpers/GrpcTestHostBuilderExtensions.cs` | 46-48 | `new AspApplicationBuilder(app)` — the gRPC test host has no `WithStartUpChecks()` either (it is absent from the grep of every other `*.TestHelpers` `Build*`) |

The check phase's own remarks (`src/Benzene.Core.MessageHandlers/StartUpChecks/BenzeneStartUpCheckExtensions.cs:59-62`) say "Called by every host from its initialization". Every host except this one.

**What the user experiences.** They follow the "five minutes, no cloud account" guide, misconfigure something the checks exist for — `UseHttp(http => { })` (no terminal middleware), a handler with `[HttpEndpoint]` but no `[Message]`, two handlers on one topic across finders — and it compiles, starts, and 404s/500s on the first request. Meanwhile the `StartUp` path two pages away would have failed at start-up naming the fix. The same user, if they add an `IHostedService` or a controller that shares a singleton with a handler, gets two instances. Neither cost is mentioned in `getting-started-aspnet.md`; `asp-net-core.md:253-256` says "Both wiring styles use the same underlying middleware, so message handlers written for one work unchanged with the other" — true of the middleware, false of the lifecycle.

Compounding it, `getting-started-aspnet.md:138-144` explains the two calls incorrectly: `UsingBenzene(x => x.AddMessageHandlers(...))` is described as needed "because the router depends on them" and "has to happen in the services phase". By trace, the real dependency is that the inline `UseBenzene(Action)` resolves `IBenzeneServiceContainer` from `app.ApplicationServices` (`AspApplicationBuilder.cs:77-80`), which only exists because `UsingBenzene` registered it (`src/Benzene.Microsoft.Dependencies/Extensions.cs:25`); and `UseMessageHandlers()` inside `UseHttp` already scans the AppDomain into the reopened container (`src/Benzene.Core.MessageHandlers/Extensions.cs:58-61, 86-90`), so the `AddMessageHandlers(assembly)` argument is belt-and-braces, not load-bearing. The doc's shorthand is not explained by the rung beneath it.

**Proposed change** (three parts; the first is a one-line library fix, the other two are doc/template):

1. Make the inline overload pay the same price. In `BenzeneExtensions.UseBenzene(IApplicationBuilder, Action<IAspApplicationBuilder>)`, after `builder(aspApplicationBuilder)`, run the checks against the container the entry points will actually use (the reopened one), e.g. `aspApplicationBuilder` exposes its `IBenzeneServiceContainer.CreateServiceResolverFactory().RunStartUpChecks()`. Same for `GrpcTestHostBuilderExtensions.BuildGrpcHost` (`.WithStartUpChecks()` like the other eight test hosts).
2. Add the missing rung so the inline shape can also use the checked, single-provider lifecycle: a pre-Build `WebApplicationBuilder.UseBenzene(Action<IBenzeneApplicationBuilder> configure)` (an `InlineBenzeneStartUp`, the ASP.NET sibling of `InlineSelfHostedStartUp` / `InlineAwsLambdaStartUp`, both of which already exist and the Lambda one already runs the checks — `src/Benzene.Aws.Lambda.Core/InlineAwsLambdaStartUp.cs:76`).
3. Point the getting-started doc, the `benzene.asp` template and the Grpc example at that rung, and state the two costs plainly in `asp-net-core.md` "Wiring without `BenzeneStartUp`" until (1) lands.

Before (`templates/content/asp/Program.cs`, 11 statements, 8 plumbing):

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.UsingBenzene(x => x
    .AddMessageHandlers(typeof(HelloWorldMessageHandler).Assembly)
    .AddDiagnostics());
var app = builder.Build();
app.UseBenzene(benzene => benzene
    .UseHttp(http => http
        .UseBenzeneEnrichment()
        .UseLogResult(_ => { })
        .UseMessageHandlers()));
app.Run();
```

After (with the pre-Build inline overload from part 2; checks run in `app.UseBenzene()`):

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.UseBenzene(benzene => benzene
    .UseHttp(http => http.UseMessageHandlers(typeof(HelloWorldMessageHandler).Assembly)));
var app = builder.Build();
app.UseBenzene();   // runs StartUp-equivalent Configure + the start-up checks
app.Run();
```

or, taking the steer the framework already ships (hosting.md:465-496):

```csharp
// Program.cs, entire
await BenzeneWebHost.RunAsync<StartUp>(args);
```

---

### F2 — BLOCKER — A missing handler dependency is found by the first message, not at start-up

**§4.1 clause:** "Inference … is permitted exactly to the degree that it is verified before any message is handled." Handler discovery is the framework's headline convention (`getting-started.md:43-44` "Handlers are discovered by reflection, so there's no routing table to maintain"); construction of what it discovered is part of the same convention.

**Evidence (trace-only).**

- `PipelineResolutionStartUpCheck` constructs every *middleware* (`StartUpChecks/PipelineResolutionStartUpCheck.cs:41-48`), which reaches `MessageRouter<TContext>` — not the handlers behind it. `DuplicateTopicStartUpCheck` and `EmptyHandlerRegistryStartUpCheck` read definitions only.
- Handlers are resolved per message in `MessageHandlerFactory.Create` (`src/Benzene.Core.MessageHandlers/MessageHandlerFactory.cs`, `_serviceResolver` field, resolved inside `CreateMessageHandlerByType`).
- `ValidateOnBuild` exists but is opt-in (`src/Benzene.Microsoft.Dependencies/MicrosoftServiceResolverFactory.cs:21-47`, "It is OPT-IN, and the reason is measured") and no real host passes `true`: `AwsLambdaHost.cs:41`, `HostedService/HostBuilderExtensions.cs:30`, `AspNet.Core/BenzeneExtensions.cs:178` all construct the factory with the default.
- Nine templates register `IGreeter` with the comment "IGreeter is the demo handler's one dependency" (`templates/content/aws-sqs/StartUp.cs:33-35` and 8 siblings). Delete that one line: `dotnet build` passes, every start-up check passes, the process reports healthy, and the first `hello:world` message fails inside `MessageHandlerFactory` (with the good `RegistrationCheck` hint — but on the message path, and on SQS/SNS/Service Bus that means a redelivered/dead-lettered message, not a stack trace on a console).

**What the user experiences.** Exactly the failure §4.1 calls out as the cost of magic: "finding out late". The templates' own tests (`SpyGreeter` via `WithServices`) never exercise this, because they always register the dependency.

**Proposed change.** A `HandlerResolutionStartUpCheck` registered in `RegisterHandlerFinderInfrastructure` (`DI/Extensions.cs:222-229`) beside the other four:

```csharp
public void Check(IServiceResolver resolver)
{
    var finder = resolver.TryGetService<IMessageHandlersFinder>();
    if (finder is null) return;
    var failures = new List<string>();
    foreach (var definition in finder.FindDefinitions())
    {
        try { resolver.GetService(definition.HandlerType); }   // constructs; never dispatches
        catch (Exception ex) { failures.Add($"'{definition.Topic.Id}' -> {definition.HandlerType.FullName}: {Innermost(ex).Message}"); }
    }
    if (failures.Count > 0)
        throw new BenzeneException($"{failures.Count} message handler(s) cannot be constructed, so the first message to reach them would fail:\n  " + string.Join("\n  ", failures));
}
```

Every discovered handler is already registered scoped by `AddMessageHandlers(Type[])` (`DI/Extensions.cs:256-259`), so the throwaway scope the runner opens (`BenzeneStartUpCheckExtensions.cs:70`) can construct them. Handlers with genuinely per-message constructor state are exactly the ones this catches, and the Advisory/Disabled knob already exists for the rare deliberate case. Cost to the user: zero lines.

---

### F3 — SHOULD-FIX — The rung-1 in-process pipeline has no shorthand; it is hand-rolled 7 times

**§4.1 clause:** "Every capability a service needs routinely MUST have a shorthand. A capability that exists only in explicit form is unfinished, not minimal." And "Duplicated plumbing across examples is a framework bug … copying it a fourth time is choosing not to fix it."

**Evidence.** The spec's rung 1 ("the middleware pipeline invoked directly from your own code — no host, no transport") and the transport-agnostic test seam are the same construction, and it appears, hand-rolled, in:

| Copy | Lines of plumbing |
|---|---|
| `templates/content/asp/BenzeneStarter.Tests/HelloWorldMessageHandlerTests.cs:21-41` | 13 |
| `examples/Cqrs/Benzene.Example.Cqrs/Program.cs:29-45, 72-74` | 12 |
| `examples/OpenTelemetry/Benzene.Examples.OpenTelemetry/Program.cs` | (outside territory; counted) |
| `examples/Mesh/Benzene.Examples.Mesh.Shared/EnvelopeHost.cs` | (outside territory; counted) |
| `docs/getting-started-worker.md:171-175` and `:281-283` | 4 + 3 |
| `docs/cookbooks/fluentvalidation-custom-rules.md` | (outside territory; counted) |

The shape, verbatim from the template test:

```csharp
var services = new ServiceCollection();
services.AddLogging();
var container = new MicrosoftBenzeneServiceContainer(services);
container.AddBenzene().AddBenzeneMessage().AddMessageHandlers(typeof(HelloWorldMessageHandler).Assembly);
var pipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(container);
pipeline.UseMessageHandlers();
var app = new BenzeneMessageApplication(pipeline.Build());
var serviceResolverFactory = new MicrosoftServiceResolverFactory(services);
return await app.HandleAsync(new BenzeneMessageRequest { Topic = topic, Headers = new Dictionary<string, string>(), Body = body }, serviceResolverFactory);
```

None of the seven copies runs `RunStartUpChecks()`. `templates/README.md:62-67` admits the gap: "`asp` … doesn't fit the `Create<StartUp>().Build*Host()` shape; its idiomatic in-process host is `WebApplicationFactory`, left as a follow-up." `Benzene.Testing` ships `BenzeneTestHost.Create<TStartUp>()` plus `Build<THost>(factory)` (`src/Benzene.Testing/BenzeneTestHost.cs:82-104`) — the seam is there; the in-process `Build*` over it is not. `Benzene.SelfHost.InlineSelfHostedStartUp` is worker-shaped (`Build()` returns `IBenzeneWorker`), not request/response.

**What the user experiences.** The `benzene.asp` template's test file is the longest file in the template and the only one whose comment cannot say "you shouldn't need to touch this". A newcomer learning "how do I test a handler" sees ten lines of container/pipeline/factory ceremony before the `[Fact]`.

**Proposed change.** One `Build*` in `Benzene.Testing` (or `Benzene.Core.MessageHandlers.TestHelpers`, which exists) composed from exactly the public calls above, running `.WithStartUpChecks()` like its eight siblings:

```csharp
public static BenzeneMessageTestHost BuildBenzeneMessageHost<TStartUp>(this BenzeneTestHostBuilder<TStartUp> builder)
    where TStartUp : BenzeneStartUp, new()
    => builder.Build((startUp, services, configuration) =>
    {
        var container = new MicrosoftBenzeneServiceContainer(services);
        var app = new BenzeneMessageHostBuilder(container);          // IBenzeneApplicationBuilder, Platform = "InProcess"
        startUp.Configure(app, configuration);                       // UseBenzeneMessage(...) mounts the pipeline; every other Use* no-ops
        return new BenzeneMessageTestHost(app.Build(), new MicrosoftServiceResolverFactory(services).WithStartUpChecks());
    });
```

After, the template test:

```csharp
private static BenzeneMessageTestHost BuildHost() => BenzeneTestHost.Create<StartUp>().BuildBenzeneMessageHost();

[Fact]
public async Task Sending_the_hello_world_topic_returns_Ok()
{
    using var host = BuildHost();
    var response = await host.SendAsync(MessageBuilder.Create("hello:world", new HelloWorldRequest { Name = "World" }));
    Assert.Equal("ok", response.StatusCode);
}
```

The same type is the rung-1 production shorthand for `Cqrs` and the worker doc's Part A (`app.Create<BenzeneMessageContext>().UseMessageHandlers().Build()` + `new BenzeneMessageApplication(...)` becomes `app.UseBenzeneMessage(p => p.UseMessageHandlers())` — that overload already exists on the Lambda and Event Hub builders, `hosting.md:132`, and is the obvious name for the neutral one). This also lets `Cqrs` drop its `readResolverFactory = null` / assign-later dance (`Program.cs:45, 74`).

---

### F4 — SHOULD-FIX — "Day-one visibility" costs three lines and one package in every template (12 copies), and `UseLogResult(_ => { })` is an incantation

**§4.1 clause:** rule 1 (routine capability without a shorthand) and the duplication rule. Also the champion's own limit: "If you cannot explain what a shorthand does in one sentence, it is too clever" — `_ => { }` is the opposite problem: a required argument that means nothing.

**Evidence.** `grep` over `templates/content/**/*.cs`:

| Incantation | Copies |
|---|---|
| `.AddDiagnostics()` | 12 |
| `.UseBenzeneEnrichment()` | 12 |
| `.UseLogResult(_ => { })` | 12 |
| `<PackageReference Include="Benzene.Diagnostics" />` in the main csproj | 12 |
| the 4-line comment explaining the three calls | 12 |

`UseLogResult` has exactly one overload and it requires an `Action<ILogContextBuilder<TContext>>` (`src/Benzene.Core.Middleware/LoggerExtensions.cs:28-29`). Every template passes an empty lambda. `UseBenzeneEnrichment()` (`src/Benzene.Diagnostics/EnrichmentExtensions.cs:34`) is itself already the "one portable call" that replaced three AWS-only calls — but it only sets scope; the log line still needs `UseLogResult`, and the spans still need `AddDiagnostics()`. Three calls, two packages, one intent: "log and trace every message".

The templates' comment (`templates/content/asp/Program.cs:26-27`) says it is "day-one visibility"; the getting-started docs omit it entirely (`getting-started-aspnet.md:113-134` has none of the three), so the doc's Program.cs and the template's Program.cs disagree about what a first service contains.

**Proposed change.**

1. `UseLogResult()` parameterless overload (composes `UseLogResult(_ => { })`; one line in `LoggerExtensions`).
2. One shorthand for the routine bundle, composed from the three public calls and documented as such: `UseBenzeneObservability()` on `IMiddlewarePipelineBuilder<TContext>` = `UseBenzeneEnrichment().UseLogResult()`; and `AddDiagnostics()` already being the DI half. Alternatively an `IMiddlewareWrapper` registered by `AddDiagnostics()` that performs the enrichment for every pipeline (the mechanism `middleware.md:295-362` documents so well) — then the pipeline side needs nothing.
3. Either way, `getting-started-aspnet.md` and the template must agree on whether a first service has observability.

Before (every template `Configure`):

```csharp
.UseApiGateway(apiGatewayApp => apiGatewayApp
    .UseBenzeneEnrichment()
    .UseLogResult(_ => { })
    .UseMessageHandlers()));
```

After:

```csharp
.UseApiGateway(apiGatewayApp => apiGatewayApp
    .UseBenzeneObservability()   // = UseBenzeneEnrichment() + UseLogResult(); see docs/monitoring.md
    .UseMessageHandlers()));
```

---

### F5 — SHOULD-FIX — Every template overrides `GetConfiguration()` with the base class's own default (13 copies; 9 add a no-op `SetBasePath`)

**§4.1 clause:** "What a steer should cost: declaration, not wiring." The framework already paid this one and the templates did not collect.

**Evidence.** `src/Benzene.Microsoft.Dependencies/BenzeneStartUp.cs:30-31` made `GetConfiguration()` virtual, defaulting to `new ConfigurationBuilder().AddEnvironmentVariables().Build()`, with the remark "23 of the 50 StartUps in this repo had this exact body … A steer should cost a line when you want something different, not a line when you want the default." Then:

| File | Override body |
|---|---|
| `templates/content/{aws-apigateway,aws-sqs,aws-sns,azure-http,azure-servicebus,azure-eventhub,azure-eventgrid,azure-queuestorage}/StartUp.cs` | `.SetBasePath(Directory.GetCurrentDirectory()).AddEnvironmentVariables().Build()` — `SetBasePath` affects file providers only; with environment variables as the sole source it is a no-op |
| `templates/content/{kafka-worker,rabbitmq-worker,servicebus-worker}/StartUp.cs` | `.AddEnvironmentVariables().Build()` — the default, verbatim |
| `examples/Asp/Benzene.Example.Asp.Minimal/StartUp.cs:19-22` | the default, verbatim — in "the smallest thing that works" |
| `examples/Versioning/Benzene.Examples.Versioning/StartUp.cs:41-46` | the default, verbatim |

That is 4–5 plumbing lines per StartUp, 13 times, and the `hosting.md:204-208` snippet already shows the intended shape ("`GetConfiguration()` not overridden: the default reads environment variables").

**Proposed change.** Delete the override from all 13 (and the three `Microsoft.Extensions.Configuration*` package references the worker templates carry "for `ConfigurationBuilder` in StartUp.cs" — `templates/content/kafka-worker/BenzeneStarter.csproj:29-35`). Before/after for `Asp.Minimal/StartUp.cs`:

```csharp
// before: 13 lines                              // after: 8 lines
public class StartUp : BenzeneStartUp             public class StartUp : BenzeneStartUp
{                                                 {
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();
    public override void ConfigureServices(...)       public override void ConfigureServices(...)
        => services.UsingBenzene(x => x                   => services.UsingBenzene(x => x
            .AddMessageHandlers(typeof(HelloWorldMessageHandler).Assembly));   .AddMessageHandlers(typeof(HelloWorldMessageHandler).Assembly));
    public override void Configure(...)                public override void Configure(...)
        => app.UseHttp(http => http.UseMessageHandlers());   => app.UseHttp(http => http.UseMessageHandlers());
}                                                 }
```

---

### F6 — SHOULD-FIX — Redundant `Add*` registrations the `Use*`/`AddMessageHandlers` calls already make (11 + 3 + 3 + 2 copies), contradicting the docs

**§4.1 clause:** rule 4 (ladder visible from the top): the templates teach that these registrations are required; the code and the docs say they are not. A user copying the template into a real service carries the ceremony forever.

**Evidence (all trace-only).**

| Redundant line | Copies | Why redundant |
|---|---|---|
| `.AddBenzene()` before `.AddMessageHandlers(...)` | 11 templates | `AddMessageHandlers` calls `AddBenzene()` itself, "idempotently … so registering handlers is enough on its own" — `src/Benzene.Core.MessageHandlers/DI/Extensions.cs:175-182, 244-246`. `getting-started-aspnet.md:138-140` says the same: "there's no separate `AddBenzene()` to remember". |
| `.AddHttpMessageHandlers()` | `aws-apigateway/StartUp.cs:36`, `azure-http/StartUp.cs:32`, `examples/Versioning/StartUp.cs:66` | `UseApiGateway` and Azure `UseHttp` register it: `src/Benzene.Aws.Lambda.ApiGateway/DependencyInjectionExtensions.cs:66,112`, `src/Benzene.Azure.Function.AspNet/DependencyInjectionExtensions.cs:115`. The ASP.NET template needs no such line, so the asymmetry reads as "HTTP on Lambda needs an extra registration". |
| `.AddKafka<Ignore, string>()`, `.AddRabbitMq()`, `.AddServiceBusConsumer()` | `kafka-worker/StartUp.cs:36`, `rabbitmq-worker/StartUp.cs:36`, `servicebus-worker/StartUp.cs:36` | `UseKafka`/`UseRabbitMq`/`UseServiceBus` each `Register(x => x.Add…())` first thing: `src/Benzene.Kafka.Core/Extensions.cs:36-38`, `src/Benzene.RabbitMq/Extensions.cs:34-36`, `src/Benzene.Azure.ServiceBus/Extensions.cs:35-37`. The worker doc's troubleshooting says so too (`getting-started-worker.md:646-649`: "`UseServiceBus`/`UseEventHub` register them for you") — while the servicebus-worker template's comment (`StartUp.cs:26`) says "`AddServiceBusConsumer()` registers the consumer's services" as if it were the user's job. |
| `.AddGrpcMessageHandlers()` | `examples/Grpc/Program.cs:23`, `getting-started-grpc.md:237` | `UseGrpc` registers it: `src/Benzene.Grpc.AspNet/BenzeneExtensions.cs:25`; its own XML doc says "Called automatically by … `UseGrpc`; you don't normally need to call this directly" (`src/Benzene.Grpc/DependencyInjectionExtensions.cs:22-23`). |
| `.AddContextItems()` | `examples/Versioning/StartUp.cs:67` | `AddMessageHandlers` calls it (`DI/Extensions.cs:191, 269`). An internal registration surfacing in user code. |
| the `benzene:spec` `IMessageHandlerDefinition` + `SpecMessageHandler` + `IHttpEndpointDefinition` | `examples/Asp/Startup.cs:72-76` (5 lines) | `UseSpec()` at line 105 registers the same definition (`src/Benzene.Schema.OpenApi/Extensions.cs:86-92`). |

**Proposed change.** Strip all of them from templates/examples/docs; where a comment currently says "X registers the consumer's services", say instead "`UseX(...)` registers everything it needs; `ConfigureServices` is for *your* services". If the maintainers believe any of these is load-bearing for `TryAdd` ordering (a user override registered *after* the transport default), that is a rule for the doc, not a line for every service.

---

### F7 — SHOULD-FIX — The platform-`Use*` no-op convention has no start-up check: a service that mounts nothing starts and runs forever

**§4.1 clause:** "The price of a convention is a start-up check … the failure names what was looked for, where, and what to add."

**Evidence (trace-only).** `UseHttp`, `UseGrpc`, `UseAwsLambda`, `UseWorker` all pattern-match the concrete builder and silently return otherwise (`src/Benzene.AspNet.Core/BenzeneExtensions.cs:88-95`, `src/Benzene.Grpc.AspNet/BenzeneExtensions.cs:46-53`, `src/Benzene.SelfHost/WorkerApplicationBuilder.cs:27-35`). This is deliberate and documented (`hosting.md:185-188`). But the failure mode is undetected: a `StartUp` whose `Configure` only calls `UseHttp(...)` run under `BenzeneHost.RunAsync<StartUp>()` builds zero pipelines and zero workers. `TerminalMiddlewareStartUpCheck` iterates zero `PipelineDescriptor`s and passes; `CompositeBenzeneWorker` with an empty list starts cleanly (`src/Benzene.SelfHost/CompositeBenzeneWorker.cs:37-41`); the host reports started. The worker doc's troubleshooting entry is literally titled "**`UseWorker` compiles but nothing happens**" (`getting-started-worker.md:632`). "Nothing happens" is the late failure §4.1 forbids.

**Proposed change.** An `EmptyHostStartUpCheck` (Enforce by default; the Advisory knob covers probe-only deployables, exactly as `EmptyHandlerRegistryStartUpCheck` reasons): every `Use*` no-op branch records what was declined (`app.Register(x => x.AddSingleton(new DeclinedPlatformCall("UseHttp", app.Platform)))` — one line per extension), and the check throws when there are zero `PipelineDescriptor`s *and* zero workers *and* at least one declined call:

> The Worker host mounted nothing: `Configure` called `UseHttp(...)` which is a no-op on platform "Worker". Call `UseWorker(worker => worker.UseAspNet(...))` to host HTTP here, or run this StartUp under `BenzeneWebHost`/`WebApplicationBuilder.UseBenzene<StartUp>()`.

That names what was looked for, where, and what to add.

---

### F8 — SHOULD-FIX — gRPC's route table is compiled on the first RPC, and the doc claims it is start-up

**§4.1 clause:** start-up check; rule 4 (doc accuracy).

**Evidence (trace-only).** `GrpcRouteFinder` builds its index in its constructor (`src/Benzene.Grpc/GrpcRouteFinder.cs:10-14`), and its only consumer is `BenzeneInterceptor`'s constructor (`src/Benzene.Grpc/BenzeneInterceptor.cs:20`), which ASP.NET Core activates per call. No `IStartUpCheck` exists in `Benzene.Grpc` or `Benzene.Grpc.AspNet` (grep). So the duplicate-`[GrpcMethod]` `BenzeneException` from `ReflectionGrpcMethodFinder` is thrown by the first RPC, yet `getting-started-grpc.md:440-442` says "throws a `BenzeneException` at startup". The HTTP side already has the exact fix: `HttpRouteStartUpCheck` "Resolving is the check" (`src/Benzene.Http/Routing/HttpRouteStartUpCheck.cs:27-32`).

The doc's other gRPC trap — handler types registered only inside `UseGrpc(...)` "fall through to `Unimplemented`" (`getting-started-grpc.md:443-445`) — is silent on the message path too.

**Proposed change.** `GrpcRouteStartUpCheck : IStartUpCheck { Check => resolver.TryGetService<IGrpcRouteFinder>(); }` registered in `AddGrpcMessageHandlers` (one `TryAddSingletonImplementation` line, mirroring `Benzene.Http/Extensions.cs:34`); plus `.WithStartUpChecks()` in `BuildGrpcHost` (F1). Then correct the doc line, which will become true.

---

### F9 — SHOULD-FIX — Three different "first ASP.NET service" shapes for one host, and the doc points at an example of a different shape

**§4.1 clause:** rule 4 — a shorthand's documentation names the explicit form it composes. Here the three artefacts that should be one ladder do not reference each other correctly.

**Evidence.**

| Artefact | Shape | Benzene calls |
|---|---|---|
| `docs/getting-started-aspnet.md:113-134` | inline, legacy `UseBenzene(Action)` | 2 (`UsingBenzene(AddMessageHandlers)`, `UseBenzene(UseHttp(UseMessageHandlers))`) |
| `examples/Asp/Benzene.Example.Asp.Minimal` (which the doc at line 9 calls "the runnable version of this guide") | `StartUp : BenzeneStartUp` + `builder.UseBenzene<StartUp>()` + `app.UseBenzene()` | 5 across two files |
| `templates/content/asp/Program.cs` | inline legacy + diagnostics + enrichment + log-result | 5 in one file |

The doc says "There are only two Benzene calls" (`:136`); the "runnable version" has a `StartUp` class the doc never mentions; the template has three calls the doc never mentions. None of the three names `BenzeneWebHost` or `UseAspNet` as the level above/below except the Minimal example's comment (`Program.cs:5-8`, which is exemplary) and one aside in "Why not just a minimal API?" (`:207-210`).

**Proposed change.** Pick one shape for the first service (the `StartUp` + `BenzeneWebHost.RunAsync` shape is the one that gets checks and a single provider; see F1), make the doc, the Minimal example and the template *identical* the way the handler file already is (the template pack diffs `HelloWorldMessageHandler.cs` across templates in CI — `templates/README.md:112-130`; extend the same diff to `Program.cs`/`StartUp.cs` against `examples/Asp/Benzene.Example.Asp.Minimal`), and in the doc write the ladder out once:

> `BenzeneWebHost.RunAsync<StartUp>(args)` is `WebApplication.CreateBuilder(args)` → `builder.UseBenzene<StartUp>()` → `Build()` → `app.UseBenzene()` → `Run()`; `UseHttp(...)` is `AddAspNetMessageHandlers()` + `Create<AspNetContext>()` + `Add(new AspNetApplication(...))`. Drop one level whenever you need to put your own middleware in between.

---

### F10 — SHOULD-FIX — `hosting.md` shows an implementation of `WebApplicationBuilder.UseBenzene<TStartUp>()` that no longer exists

**§4.1 clause:** rule 4. This is the page whose job is to make the ladder visible, and the rung it draws is not the rung in the code.

**Evidence.** `docs/hosting.md:427-445` shows `UseBenzene<TStartUp>` stashing `new BenzeneStartUpHolder(startUp, configuration)` and `app.UseBenzene()` running `holder.StartUp.Configure(...)`. The code (`src/Benzene.AspNet.Core/BenzeneExtensions.cs:151-163, 172-181`) runs `Configure` *before* `Build()` inside `UseBenzene<TStartUp>` (so registrations land in the single root provider), the holder holds the `AspApplicationBuilder`, and `app.UseBenzene()` calls `Finish(app)` and then `RunStartUpChecks()`. The doc's version is precisely the older design whose singleton-split problem the remarks at `:124-134` describe. The `Azure.Function.Core`/`HostedService` snippets on the same page (`:240-257, 279-300`) also omit the `RunStartUpChecks()` call both hosts now make (`HostBuilderExtensions.cs:33`).

**Proposed change.** Replace the three snippets with the current bodies (they are short), and add one sentence under each: "The start-up checks run here."

---

### F11 — SHOULD-FIX — Ceremony parity across hosts for the test rung: 3 lines on Lambda, 13 in-process, 8 for a worker, "use `WebApplicationFactory`" on ASP.NET

**§4.1 clause:** rule 1 and the ceremony-parity mandate ("a capability should not cost four lines in one host and forty in another").

**Evidence.**

| Host | Test entry point | Lines to a sent message | Runs checks? |
|---|---|---|---|
| AWS Lambda | `BenzeneTestHost.Create<StartUp>().BuildAwsLambdaHost()` + `SendApiGatewayAsync` (`templates/content/aws-apigateway/…Tests.cs:15-25`) | 3 | yes (`WithStartUpChecks`) |
| Azure Functions | `.BuildAzureFunctionApp()` + `HandleHttpRequest` | 3 | yes |
| Kafka / RabbitMQ / Service Bus worker | `.BuildKafkaWorkerHost<StartUp, Ignore, string>()` / `.BuildRabbitMqWorkerHost()` / `.BuildServiceBusWorkerHost()` + `HandleAsync` | 3 | yes |
| gRPC | `.BuildGrpcHost(map)` + generated client | 4 | **no** (F1) |
| ASP.NET embedded | "use `WebApplicationFactory`" (`testing-benzene.md:83-92`); the template instead hand-rolls in-process | 13 | **no** |
| In-process / rung 1 | hand-rolled | 13 | **no** |
| Custom worker (Part A) | `new HostBuilder().UseBenzene<StartUp>().Build()` + manual `IHostedService` start/stop loop (`getting-started-worker.md:255-270`) | 8 | yes (via `UseBenzene`) |

**Proposed change.** F3's `BuildBenzeneMessageHost()` closes the in-process and ASP.NET-template rows. For the worker row, a `BuildWorkerHost()` returning a disposable that starts/stops the `IHostedService`s (the 8 lines of the doc, composed) is the parity fix. Also note the generic-inference wart: `BuildKafkaWorkerHost<StartUp, Ignore, string>()` forces the user to restate `StartUp` while every sibling infers it (`templates/content/kafka-worker/…Tests.cs:26`) — a `BuildKafkaWorkerHost<TKey, TValue>()` overload with `TStartUp` inferred from the builder removes it.

---

### F12 — POLISH — Verb and shape inconsistencies on the builder surface

**§4.1 clause:** rule 2/4 by implication — a user cannot predict the next call's name. Listed with file:line; none is individually serious, together they are the "read as intent" tax.

| Inconsistency | Where |
|---|---|
| Three verbs for "hook Benzene into this container/host": `UsingBenzene` (`IServiceCollection`, `Benzene.Microsoft.Dependencies/Extensions.cs:8,14`; `ContainerBuilder`, `Benzene.Autofac/Extensions.cs:8,14`), `AddBenzene` (`IBenzeneServiceContainer`, `Core.MessageHandlers/DI/Extensions.cs:101`), `UseBenzene` (`IHostBuilder`, `WebApplicationBuilder`, `IApplicationBuilder` ×2) | as cited |
| `UseBenzene` on `IApplicationBuilder` twice, different lifecycles and guarantees | F1 |
| `IHostBuilder.UseBenzene<TStartUp>()` declared identically in two packages (`Benzene.HostedService`, `Benzene.Azure.Function.Core`) — the docs carry a 9-line warning about it (`getting-started-worker.md:232-239`, `:639-641`) | cross-territory; the fix is a distinct name for one of them |
| `AddBenzeneGrpc` on `IServiceCollection` but `AddGrpcMessageHandlers`/`AddAspNetMessageHandlers`/`AddHttpMessageHandlers` on `IBenzeneServiceContainer` — gRPC is the only transport whose first service needs two registrations in two container types (`getting-started-grpc.md:233-237`) | `Benzene.Grpc.AspNet/ServiceCollectionExtensions.cs:30` |
| `UseHttpProblemDetailsStatus<TContext>` is a DI registration with a `Use` verb — the only `Use*` on `IBenzeneServiceContainer` | `Benzene.Http/Extensions.cs:88` |
| `SetApplicationInfo` — the only `Set*` on the container; everything else is `Add*` | `Core.MessageHandlers/DI/Extensions.cs:86` |
| `worker.Add(Func<…>)` vs `worker.UseSqs/UseKafka/…` for "mount a worker" | `Benzene.SelfHost/IBenzeneWorkerStartup.cs:9` |
| `UseHttp` means embedded-ASP.NET on `IBenzeneApplicationBuilder` but is also the Azure Functions HTTP verb; AWS's HTTP is `UseApiGateway`; the worker-hosted Kestrel is `UseAspNet` — four names for "serve HTTP" | `azure-http/StartUp.cs:47`, `aws-apigateway/StartUp.cs:52`, `AspNetSelfHostExtensions.cs:37` |
| `UseMessageHandlers()` has 7 overloads; two accept `params` arrays that make `UseMessageHandlers(typeof(A))` and `UseMessageHandlers(assembly)` look the same but differ in scan scope | `Core.MessageHandlers/Extensions.cs:58-163` |
| `BuildKafkaWorkerHost<StartUp, Ignore, string>()` vs `BuildRabbitMqWorkerHost()` | F11 |

Recommendation: one naming rule in `AGENTS.md` ("`Add*` registers, `Use*` mounts into a pipeline/builder, `With*` configures a builder, `Build*` finishes") and the handful of renames above (with `[Obsolete]` forwards — public surface is forever).

---

### F13 — POLISH — `UseMessageHandlers(_ => { })` appears 10 times in examples; the no-arg overload is identical

**Evidence.** `examples/Versioning/Benzene.Examples.Versioning/StartUp.cs:108, 115, 118, 121` (4) and 6 more across `examples/` (grep). Both overloads scan `AppDomain.CurrentDomain.GetAssemblies()` (`Core.MessageHandlers/Extensions.cs:58-76`). The empty lambda is noise a newcomer will copy. Strip; if the intent was "no handler middleware", `UseMessageHandlers()` says it.

---

### F14 — POLISH — `examples/Asp/Benzene.Example.Asp/Startup.cs:111-115` states the router is "unconditionally terminal … always answers, even NotFound"; the code, the doc and the test say the opposite for unmatched routes

**Evidence (trace-only).** `AspNetMessageTopicGetter` returns a null-id topic for an unmatched route (`AspNetMessageTopicGetter.cs:31-32`); the router turns it into the `<missing>` sentinel; `AspMessageHandlerResultSetter : ResponseIfHandledMessageHandlerResultSetter` writes nothing when the topic is `<missing>` (`Core.MessageHandlers/Response/ResponseIfHandledMessageHandlerResultSetter.cs:35-39`); `AspApplicationBuilder.Add` then calls `next()` because the response has not started (`AspApplicationBuilder.cs:120-127`). `asp-net-core.md:19-20, 154-156` and `AspNetUnifiedStartUpTest.cs:109` describe exactly this. The example comment is right only about a *matched* route whose handler returns NotFound. Since the comment is the rationale for the `app.Map("/protected", …)` pattern, fix the sentence: "a request that matches a route in this pipeline is answered here, even with NotFound; an unmatched request falls through".

---

### F15 — POLISH — Template/doc arithmetic and inventory drift

- `docs/getting-started-templates.md:4` lists "self-hosted HTTP" among the templates; there is no such template (12 templates, `templates/README.md:9-28`; none uses `UseAspNet`). Given `hosting.md:498-503` and `BenzeneWebHost`'s own remarks say `UseAspNet` + `BenzeneHost` is the *right* shape when ASP.NET is only the HTTP host, the missing template is the one the framework itself recommends.
- `:62` "The other nine work today": 12 − 4 = 8.
- `examples/Grpc/Benzene.Example.Grpc/Program.cs:31` keeps `dotnet new grpc`'s `app.MapGet("/", …)` scaffold line, and `Services/GreeterService.cs:14-20` overrides `SayHello` with a body that never runs because the Benzene handler claims the route — the doc explicitly recommends *not* overriding claimed methods (`getting-started-grpc.md:75-87`). Both are plumbing without a "deliberate" comment.
- `templates/content/asp/README.md:23-25` links "Full guide: getting-started.md" — the ASP.NET guide is `getting-started-aspnet.md`.

---

### F16 — POLISH — The Azure Functions trigger class is copied into 5 templates although a source generator exists

**Evidence (cross-territory; counted because the templates are in scope).** `HttpFunction.cs`, `ServiceBusFunction.cs`, `EventHubFunction.cs`, `EventGridFunction.cs`, `QueueFunction.cs` are the same 6-line "inject `IAzureFunctionApp`, one `[Function]` that forwards" adapter ×5. `examples/CLAUDE.md` (AzureFunctionsMesh) says "each `Triggers.cs` declares just the triggers it uses via the source generator" (`Benzene.Azure.Function.SourceGenerators`). The templates — the artefacts a newcomer copies — do not use the shorthand the repo ships. Similarly the 5 Azure `Program.cs` files are the same 4 lines (`new HostBuilder().ConfigureFunctionsWorkerDefaults().UseBenzene<StartUp>().Build(); host.Run();`) with no `BenzeneFunctionsHost.Run<StartUp>()` sibling of `BenzeneHost`/`BenzeneWebHost`; and the 3 worker `Program.cs` files write the 4-line explicit form although `BenzeneHost.RunAsync<StartUp>(args)` exists and `hosting.md:323-337` argues for it. Hand to the Azure/AWS champions with the counts.

---

### F17 — POLISH — `services.AddLogging(x => x.AddConsole())` ×3 in the AWS templates

`templates/content/aws-{apigateway,sqs,sns}/StartUp.cs:31-32` each add a console provider "so ILogger output reaches CloudWatch (a Lambda host wires no provider by default)". `AwsLambdaHost` already calls `services.AddLogging()` (`AwsLambdaHost.cs:34`) without a provider. If every Lambda needs it, the host should add it (Lambda's stdout *is* CloudWatch); if not, the templates should say why. Cross-territory; counted.

---

## Boilerplate ledger

Counting rule: statements only (no `using`, blank, comment or brace-only lines; method/class signatures excluded). **Domain** = the example's own subject; **Intent** = what it handles / talks to / needs (`[Message]`, `AddMessageHandlers`, `UseHttp`, `UseSqs`, `UseMessageHandlers`, transport config, service registrations the handler needs); **Plumbing** = everything else, with its category: **MS** = missing shorthand (framework bug), **RED** = redundant with a call already made, **DEL** = deliberate demonstration *with* a comment saying so, **DEL?** = explicit form with no such comment.

| File | Domain | Intent | Plumbing | The plumbing |
|---|---|---|---|---|
| `templates/asp/Program.cs` | 0 | 3 | 8 | `CreateBuilder`, `Build`, `Run` (host shape; MS — `BenzeneWebHost`), `UsingBenzene(` wrapper (MS), `AddDiagnostics`, `UseBenzeneEnrichment`, `UseLogResult(_=>{})` (MS, F4), `UseBenzene(` legacy (F1) |
| `templates/asp/…Tests.cs` (SendAsync) | 0 | 1 | 12 | hand-rolled in-process host (MS, F3) |
| `templates/aws-apigateway/StartUp.cs` (+`Function.cs`) | 0 | 4+1 | 11 | `GetConfiguration` ×4 (RED, F5), `AddLogging(AddConsole)` (F17), `UsingBenzene(`, `AddBenzene` (RED), `AddHttpMessageHandlers` (RED), `AddDiagnostics`/`UseBenzeneEnrichment`/`UseLogResult` (MS) |
| `templates/aws-sqs/StartUp.cs` | 1 | 4 | 9 | as above minus `AddHttpMessageHandlers` |
| `templates/aws-sns/StartUp.cs` | 1 | 4 | 9 | same |
| `templates/azure-http/StartUp.cs` (+`Program.cs`, `HttpFunction.cs`) | 0 | 3+1 | 10+4+6 | `GetConfiguration` ×4, `AddBenzene`, `AddHttpMessageHandlers` (RED), observability ×3 (MS); `Program.cs` host shape ×4 (MS, F16); trigger adapter ×6 (MS, F16) |
| `templates/azure-servicebus/StartUp.cs` (+Program, Function) | 1 | 3+1 | 9+4+6 | same pattern |
| `templates/azure-eventhub/StartUp.cs` (+Program, Function) | 1 | 4+1 | 9+4+6 | same |
| `templates/azure-eventgrid/StartUp.cs` (+Program, Function) | 1 | 3+1 | 9+4+6 | same |
| `templates/azure-queuestorage/StartUp.cs` (+Program, Function) | 1 | 4+1 | 9+4+6 | same |
| `templates/kafka-worker/StartUp.cs` (+`Program.cs`) | 1 | 13+1 | 9+3 | `GetConfiguration` ×3 (RED), `AddBenzene` (RED), `AddKafka<>` (RED, F6), observability ×3 (MS); `Program.cs` ×3 (MS — `BenzeneHost.RunAsync`) |
| `templates/rabbitmq-worker/StartUp.cs` (+`Program.cs`) | 1 | 9+1 | 9+3 | same, `AddRabbitMq` (RED) |
| `templates/servicebus-worker/StartUp.cs` (+`Program.cs`) | 1 | 11+1 | 9+3 | same, `AddServiceBusConsumer` (RED) |
| `examples/Asp/…Minimal/Program.cs` | 0 | 1 | 4 | `CreateBuilder`/`Build`/`UseBenzene()`/`Run` — **DEL**, comment names `BenzeneWebHost` (`:5-8`). Exemplary. |
| `examples/Asp/…Minimal/StartUp.cs` | 0 | 3 | 4 | `GetConfiguration` ×3 (RED, F5), `UsingBenzene(` wrapper |
| `examples/Asp/Benzene.Example.Asp/Program.cs` | 0 | 0 | 8 | classic-host preamble (DEL? — `examples/CLAUDE.md` justifies `Startup.cs`, not `Program.cs`) |
| `examples/Asp/Benzene.Example.Asp/Startup.cs` (approx.) | ~10 | ~20 | ~22 | Serilog/AppInsights block ×9 (DEL — "Demo-only" comment), spec hand-registration ×5 (RED, F6), `AddLogging`/`AddScoped<ILogger>`/`AddSingleton(Configuration)` ×3, ASP.NET pipeline calls ×5 (necessary for controllers; DEL per `examples/CLAUDE.md`) |
| `examples/Grpc/Benzene.Example.Grpc/Program.cs` (+`GreeterService.cs`) | 2 | 5 | 7+6 | `UsingBenzene(`, `AddGrpcMessageHandlers` (RED), `MapGet("/")` scaffold, `UseBenzene(` legacy (F1), `CreateBuilder`/`Build`/`Run`; dead `SayHello` override (F15) |
| `examples/Cqrs/Benzene.Example.Cqrs/Program.cs` (approx.) | ~50 | ~8 | ~35 | hand-rolled in-process host ×12 (MS, F3), `DeliverToReadSideAsync` + two `.Use(async …)` bridges ×12 (MS — `Benzene.Clients.InProcess.UseInProcess` exists for exactly this dispatch by its own description, `Core.MessageHandlers/DI/Extensions.cs:51-53`; trace-only), `QueryReadModelAsync` hand-built request/response ×8 (MS — an in-process client), null-then-assign factory ×2 |
| `examples/Versioning/…/StartUp.cs` | 1 | ~24 | ~11 | `GetConfiguration` ×3 (RED), `AddSingleton(configuration)`, `AddLogging` (host adds it), `AddHttpMessageHandlers` (RED), `AddContextItems` (RED), explicit `Create<BenzeneMessageContext>()`+`UseBenzeneMessage(pipeline)` ×3 where the inline overload exists, `_ => { }` ×4 (F13) |
| `examples/App/Benzene.Examples.App/Handlers/*.cs` | all | — | 0 | the handler shape is pure intent: attribute, interface, one `HandleAsync`. Nothing to strip. |

Totals for the 12 templates' `StartUp`/`Program` files alone: **~112 plumbing statements against ~75 intent and 8 domain**. Of the plumbing, 13 `GetConfiguration` overrides, 11 `AddBenzene`, 5 transport `Add*`, 36 observability lines and 27 host-shape lines are each one framework change away from zero.

---

## Capability → explicit form → shorthand → documented? (territory)

| Capability | Explicit form (public) | Shorthand | Composed from explicit? | Start-up check | Documented (ladder visible)? |
|---|---|---|---|---|---|
| Build a pipeline in process (rung 1) | `MicrosoftBenzeneServiceContainer` + `AddBenzene/AddBenzeneMessage/AddMessageHandlers` + `MiddlewarePipelineBuilder<BenzeneMessageContext>` + `BenzeneMessageApplication` + `MicrosoftServiceResolverFactory` | **none** (F3) | — | none (no host runs them) | worker doc Part A, cookbooks; not in getting-started |
| Host on ASP.NET, embedded | `CreateBuilder` → `builder.UseBenzene<StartUp>()` → `Build` → `app.UseBenzene()` → `Run` | `BenzeneWebHost.RunAsync<StartUp>(args)` | yes, verbatim (`BenzeneWebHost.cs:70-84`) | yes, in `app.UseBenzene()` | hosting.md, asp-net-core.md — yes, exemplary; getting-started-aspnet.md — **no** (teaches the unchecked inline path, F1/F9) |
| Host on ASP.NET, inline (no StartUp) | `app.UseBenzene(b => b.UseHttp(…))` | (is itself the bottom rung) | — | **no** (F1) | asp-net-core.md "Wiring without BenzeneStartUp" — costs not stated |
| Host Kestrel as a worker | `IHostBuilder.UseBenzene<StartUp>` + `UseWorker(w => w.UseAspNet(…))` | `BenzeneHost.RunAsync<StartUp>(args)` | yes (`BenzeneHost.cs:69-75`) | yes | hosting.md, getting-started-kubernetes.md; no template (F15) |
| Host as a worker | `Host.CreateDefaultBuilder(args).UseBenzene<StartUp>().Build().RunAsync()` | `BenzeneHost.RunAsync<StartUp>(args)` | yes | yes (`HostBuilderExtensions.cs:33`) | hosting.md, getting-started-worker.md — yes; 3 templates use the explicit form without saying so |
| Host on Lambda | hand-built entry point (`examples/Aws/…/BareMetalLambdaEntryPoint.cs`) | `class Function : AwsLambdaHost<StartUp>` | yes | yes (`AwsLambdaHost.cs:61`) | hosting.md — yes |
| Host on Azure Functions | `HostBuilder` + `ConfigureFunctionsWorkerDefaults` + `UseBenzene<StartUp>` + trigger class | source generator for triggers; no host one-liner | partly | yes | azure-functions.md (outside territory); templates don't use the generator (F16) |
| Declare a handler / route by topic | `router.AddMessageHandler<THandler,TReq,TRes>("topic")` (`Core.MessageHandlers/Extensions.cs:248`) or DI `IMessageHandlerDefinition` | `[Message("topic")]` + `AddMessageHandlers(assembly)` / `UseMessageHandlers()` | yes (`DependencyMessageHandlersFinder` in the same composite) | duplicate: throw; empty registry: log; **handler construction: none** (F2) | message-handlers.md:135-166 — yes, names every finder and the explicit call |
| Route by HTTP | `IHttpEndpointDefinition` in DI / `ListHttpEndpointFinder` | `[HttpEndpoint("GET","/x/{id}")]` | yes (composite finder) | `HttpRouteStartUpCheck` + `UnroutedHttpEndpointCheck` (throw, excellent text) — only where checks run | message-handlers.md:99-133 names the finders; the explicit registration call shape is shown only in the Asp example |
| Route by gRPC method | replaceable `IGrpcMethodFinder` / `IGrpcRouteFinder`; no public explicit-registration call | `[GrpcMethod("/pkg.Svc/Method")]` | — | **none** (F8) | getting-started-grpc.md — claims start-up check that does not exist |
| Return a result | `BenzeneResult.Set(status, payload, isSuccessful)` / `BenzeneResult.Problem(problem)` | `BenzeneResult.Ok/Created/NotFound/…` | yes | `Problem` throws naming the fix when `BenzeneStatus` missing; custom `Set` throws without explicit `isSuccessful` | message-result.md — yes, exemplary |
| Add validation | `router.Add(IHandlerMiddlewareBuilder)` | `UseMessageHandlers(r => r.UseFluentValidation())` | yes | via pipeline-resolution (middleware) | message-handlers.md:209-213 — yes |
| Add tracing | `IMiddlewareWrapper` registered in DI | `AddDiagnostics()` | yes | n/a | middleware.md:295-362 — yes, exemplary |
| Add logging/enrichment | `UseLogContext(b => …)` / `UseLogResult(b => b.WithTopic()…)` | `UseBenzeneEnrichment()` + `UseLogResult(_ => { })` — two calls, empty lambda | yes | n/a | common-middleware.md (outside territory); 12 copies (F4) |
| Add health | `UseHealthCheck("healthcheck", checks)` (middleware.md:113) | same | — | — | health-checks.md (outside territory) |
| Override a convention (topic getter, serializer, status mapper) | `TryAdd`-based registration in `ConfigureServices` before the transport's default | n/a | — | n/a | message-result.md:237-243 and package CLAUDE.md — yes |
| Test it | see F11 | `BenzeneTestHost.Create<StartUp>().Build*Host()` for 8 transports | yes | yes for 8; **no** for gRPC/in-process/ASP.NET | testing-benzene.md — yes; ASP.NET/in-process rows point elsewhere |

---

## What is genuinely good (do not touch)

- **The start-up-check phase** (`Benzene.Core.MessageHandlers/StartUpChecks/*`). Every failure reported at once, the exception text names the softening knob, `TerminalMiddlewareStartUpCheck`'s `UseSqs(sqs => { })` rationale, `DuplicateTopicStartUpCheck` explaining why the runtime was inconsistent, `EmptyHandlerRegistryStartUpCheck` logging rather than throwing with the reason stated, `UnroutedHttpEndpointCheck`'s message ("Add a `[Message("topic")]` attribute … or register it explicitly with …"). This is §4.1's "names what was looked for, where, and what to add" done properly. F1/F2/F7/F8 are about *coverage*, not design.
- **`BenzeneHost` and `BenzeneWebHost`** (`Benzene.HostedService/BenzeneHost.cs`, `Benzene.AspNet.Core/BenzeneWebHost.cs`). The XML docs show the explicit form verbatim, say "nothing here can do anything you could not write yourself", say when *not* to use the shorthand, and expose `Build` as the escape hatch and test seam. This is the reference implementation of "the ladder is visible from the top"; the other ports should copy the prose.
- **`examples/Asp/Benzene.Example.Asp.Minimal/Program.cs:5-8`** — "DELIBERATELY the explicit form … The one-line shorthand over exactly these calls is `BenzeneWebHost.RunAsync<StartUp>(args)`". Exactly the comment §4.1 requires; the only example in the territory that has it.
- **`BenzeneStartUp.GetConfiguration()` virtual default** and its remark ("23 of the 50 StartUps … A steer should cost a line when you want something different"). Correct reasoning; F5 is only that the templates were not updated to collect.
- **`AddMessageHandlers` guaranteeing `AddBenzene()`** (`DI/Extensions.cs:175-182`) — "the old footgun surfaced far from its cause" — and the transports' `Use*` self-registering their `Add*`. The framework already removed the ceremony; F6 is the templates still paying it.
- **The handler shape.** `[Message("hello:world")]` + `IMessageHandler<TReq,TRes>` + `BenzeneResult.Ok(...)`: three lines of pure intent, byte-identical across 12 templates, with a CI diff check (`templates/README.md:112-130`) that keeps it so. The message-handlers.md/message-result.md pair documents every rung underneath it (finders, mappers, renderers, status mapping) without leaking any of it into the handler.
- **The test-host shape** `Create<StartUp>().WithServices(...).WithConfiguration(...).Build*Host()` — the same three calls on eight transports, each `Build*` running `.WithStartUpChecks()` so a wiring bug is "a red unit test rather than something the developer meets on a deployed function". F11 asks only for the missing rows.
- **`AspMessageHandlerResultSetter` / `ResponseIfHandledMessageHandlerResultSetter`** — the fall-through design that lets Benzene coexist with controllers is right and tested; only a comment (F14) misdescribes it.
- **`UseTopicFrom` / `UsePresetTopic`** and the "scoped DI state, not context" rule in `Benzene.Abstractions.Middleware/CLAUDE.md` — a clean explicit form (a custom `IMessageTopicGetter<TContext>`) with an inline shorthand composed on top of the same holder. Model for future conventions.
- **`BenzeneResult.Problem`/`Set` throwing `ArgumentException` naming the fix** when a custom status has no explicit success classification (`message-result.md:232-236`) — a start-up-shaped guarantee at the one place it cannot be start-up.
