# Startup Diagnostics — moving setup errors off the message path

**Date:** 2026-07-29
**Question:** which setup mistakes are only discoverable when a message arrives, why, and how much of
that can be pulled forward to build / composition / warm-up time?
**Method:** every claim below is either read out of the source (path:line cited) or reproduced by
running code against this branch with the .NET 10 SDK. Repros are noted as **[repro]**.

---

## Verdict

The problem is **not** that Benzene lacks startup checks. It has five of them, in five packages, each
opt-in, each with a different name and severity, and **none of them wired into any host**:

| Check | Package | Behaviour | Who calls it |
|---|---|---|---|
| `UnroutedHttpEndpointCheck` | `Benzene.Http` | throws | auto — but only when the route table is first built, i.e. **first request** |
| `ValidateOutboundRouting()` | `Benzene.Clients` | throws | nobody |
| `FindUnmappedResponseHandlers()` / `Log…` | `Benzene.ResponseEvents` | advisory | nobody |
| `FindPipelineOrderingIssues()` / `Log…` | `Benzene.Diagnostics` | advisory | nobody |
| `BENZ001` duplicate topic | `Benzene.CodeGen.SourceGenerators` | compile error | only `test/Benzene.Core.Test` references the analyzer |

And the one mechanism that *does* run automatically at Lambda INIT — `IServiceResolverFactory.WarmUp()`
(`src/Benzene.Aws.Lambda.Core/AwsLambdaHost.cs:43`) — is **structurally incapable** of reporting
anything, because its runner swallows every exception
(`src/Benzene.Core.MessageHandlers/WarmUp/BenzeneWarmUpExtensions.cs:51`) and it is off unless
`AddBenzeneWarmUp()` was called.

So the recommendation is *consolidation and activation*, not invention. Details in §5.

Two findings dominate everything else and are stated up front:

> **(A) `docs/getting-started-aws.md` does not work as written.** Followed verbatim it throws the
> maintainer's exact failure #1 on the first request, and the registration hint tells you to add a
> call you already made. **[repro]** — §4.1. The same omission is in `getting-started.md`,
> `getting-started-kafka.md`, `getting-started-rabbitmq.md`, `azure-functions.md`.
>
> **(B) `ServiceProviderOptions.ValidateOnBuild = true` already catches failure #1** at container-build
> time, naming *both* root causes, with **zero new Benzene code** — one line in
> `MicrosoftServiceResolverFactory`. It passes clean on `examples/Aws` today. **[repro]** — §2.2.

---

## 1. Taxonomy — what can only surface when a message arrives, and why

### 1.0 The structural cause

Three deliberate design choices, each individually right, combine into "silently valid until a
message arrives":

1. **Middleware is resolved lazily, per link, per message.**
   `MiddlewarePipeline.CreateChain` (`src/Benzene.Core.Middleware/MiddlewarePipeline.cs:41-56`)
   resolves each middleware *inside* the closure for that link. The comment there justifies it
   correctly: a short-circuited middleware is never constructed, and `UseExceptionHandler` can cover a
   downstream construction failure. Consequence: **no middleware's constructor dependencies are ever
   touched until a message reaches that link.**
2. **Discovery is lazy and singleton-cached.** `MessageHandlerDefinitionIndex.GetIndex()`
   (`…/MessageHandlerDefinitionIndex.cs:70-99`) and `RouteFinder`'s constructor
   (`src/Benzene.Http/Routing/RouteFinder.cs:27`) run on first resolve, which is the first message.
   Every discovery-time exception (duplicate topic, unrouted `[HttpEndpoint]`) is therefore a
   *first-message* exception.
3. **The pipeline builder is discarded.** `CreateMiddlewarePipeline`
   (`src/Benzene.Core.Middleware/DependencyExtensions.cs:34-40`) builds and drops the builder,
   returning an `IMiddlewarePipeline<TContext>` that exposes no way to enumerate its items. Every
   transport's per-message sub-pipeline (`UseSqs`, `UseApiGateway`, `UseSns`, …) goes through this.
   Consequence: **nothing downstream of composition can walk the pipeline tree.** This is the exact
   blocker `work/debuggability-assessment.md` already identified for the deferred ordering rule
   ("needs a pipeline-introspection seam that spans the sub-pipeline boundary").

### 1.1 Class A — missing DI registration, discoverable at container build

The container is fully described at build time; nothing about these needs a message.

| # | Case | Today's symptom | Cite |
|---|---|---|---|
| A1 | `AddBenzene()` omitted (hand-composed host, **or the published AWS/Kafka/RabbitMQ/Azure quickstarts**) | first message: `BenzeneException: Unable to resolve type MessageRouter<…>` → `IDefaultStatuses` | `…/DI/Extensions.cs:85` is the *only* registration of `IDefaultStatuses` |
| A2 | A handler's own constructor dependency not registered | first message on **that topic only** | `MessageHandlerFactory.CreateMessageHandlerByType` `…/MessageHandlerFactory.cs:111` |
| A3 | Custom `TContext` with `UseMessageHandlers` but no `Add<Transport>()` | first message: unresolvable `IMessageHandlerResultSetter<TContext>` / `IMessageTopicGetter<TContext>` — `AddContextItems` (`…/DI/Extensions.cs:114-126`) supplies open generics for `IMessageGetter<>`/`IRequestMapper<>` etc. but **not** for the setter, topic getter, or version getter | ibid. |
| A4 | Middleware added by a `Use*` whose `Add*` was not called | first message, at that link only | `MiddlewarePipeline.cs:52` |

Most first-party `Use*` calls do register their own `Add*` (`UseSqs` →
`app.Register(x => x.AddSqs(...))`, `src/Benzene.Aws.Lambda.Sqs/Extensions.cs:32`; `UseMessageHandlers`
→ `AddMessageHandlers`, `…/Extensions.cs:87`), so A4 is mostly a hand-written-middleware problem. A1
and A2 are the live ones.

**Detectable without a message: yes, at container build.** See §2.2.

### 1.2 Class B — the wiring is *complete* but *wrong*, and nothing ever complains

These are worse than A: they never produce an error at all, at any time.

**B1 — A pipeline with no terminal middleware.** **[repro]** `UseSqs(sqs => { })` — forgetting
`.UseMessageHandlers()` — composes cleanly, and every SQS record comes back as a batch-item failure
with **no log line, no exception, nothing**:

```
=== composition completed with no error ===
=== pipeline completed ===
response body: {"batchItemFailures":[{"itemIdentifier":"m1"}]}
```

The chain seed is `() => Task.CompletedTask` (`MiddlewarePipeline.cs:53`), so a pipeline that runs off
the end is legal; `SqsApplication` then reads `context.MessageResult?.IsSuccessful != true`
(`src/Benzene.Aws.Lambda.Sqs/SqsApplication.cs:75`) and reports the record as failed. Every message
retries to the DLQ. Indistinguishable from a poison-message problem — but it is a poison *deployment*.
An empty-item count check won't catch it: `UseSqs` auto-adds `UseBenzeneInvocation`
(`src/Benzene.Aws.Lambda.Sqs/Extensions.cs:34`), so the sub-pipeline has exactly one item.

**B2 — Two handlers on the same topic, silently deduped across finders.** **[repro]** A handler
discovered by `[Message("hello:world")]` and a *different* handler registered explicitly for the same
topic via `AddMessageHandler<ShadowHandler, …>("hello:world")` produce **no error**; the explicit one is
silently dropped and the reflection one answers (HTTP 200, `{"message":"Hello world!"}`).
`MessageHandlerDefinitionIndex` does `.GroupBy(x => (x.Topic.Id, x.Topic.Version)).Select(x => x.First())`
(`…/MessageHandlerDefinitionIndex.cs:91-92`) with no duplicate check. Contrast
`ReflectionMessageHandlersFinder.FindDefinitions()` (`…/ReflectionMessageHandlersFinder.cs:86-100`),
which *does* throw for the same collision — but only within its own scan. Same mistake, opposite
outcome, depending on which finder you used.

**B3 — A handler that no finder sees.** `AddMessageHandlers(assemblies)` registers the union finder via
`TryAddSingleton` (`…/DI/Extensions.cs:181`), so the *first* call's assembly set wins for that
registration. The `examples/Aws` code carries a comment recording exactly this bug in the past
(`examples/Aws/Benzene.Examples.Aws/DependenciesBuilder.cs:96-101`: "omitting this project's own
assembly here left `PublishOrderCreatedMessageHandler` undiscoverable … despite compiling and looking
wired"). Symptom: 404 / "No handler found for topic".

**B4 — A validator that is never applied.** `ValidationMiddleware.HandleAsync` does
`TryGetService<IValidator<TRequest>>()` and, on null, just calls `next()`
(`src/Benzene.FluentValidation/ValidationMiddleware.cs:29-49`). A validator written but placed in an
assembly outside the `AddFluentValidation` scan means **no validation, forever, with no signal**.

**B5 — An unrecognised Lambda event returns HTTP 200 / empty body.** **[repro]**
`AwsLambdaEntryPoint.FunctionHandlerAsync` guards with `if (context.Response != null)`
(`src/Benzene.Aws.Lambda.Core/AwsLambdaEntryPoint.cs:48`) before throwing its otherwise-excellent
"The event type has not been recognized…" message (line 53) — but `AwsEventStreamContext`'s constructor
sets `Response = new MemoryStream()` (`…/AwsEventStream/AwsEventStreamContext.cs:26`) and nothing in
`src/**` ever assigns `Response = null`. **The guard is unreachable and the message is dead code.**
Observed: `=== UNRECOGNISED EVENT: returned normally, 0 bytes, no exception ===`. A Lambda pointed at a
pipeline that doesn't handle its event source (or an SQS record missing `eventSource`, which is
`SqsLambdaHandler.CanHandle`'s test, `…/SqsLambdaHandler.cs:48-52`) succeeds silently.

**B6 — `[HttpEndpoint]` with no matching route.** `AspNetMessageTopicGetter`/the API Gateway equivalent
return `new Topic(null)` on no match, and `MessageRouter` turns that into the *"Topic is missing"*
branch (`…/MessageRouter.cs:90-100`) — whose remedy text talks about producer attributes and
`UsePresetTopic`, neither of which is relevant to a mistyped HTTP route. Wrong advice for that path.

**Detectable without a message: mostly yes** — B1/B2/B3/B4 at composition or warm-up; B5 is a two-line
bug fix, not a diagnostic; B6 is an error-message fix.

### 1.3 Class C — the failure is visible only through `ILogger`

`SqsApplication`'s per-record catch resolves `ILogger<SqsApplication>` and logs
(`src/Benzene.Aws.Lambda.Sqs/SqsApplication.cs:80-89`). **[repro]** with no logging provider the whole
pipeline being broken is *byte-identical* on the wire to a message that didn't route — both give
`{"batchItemFailures":[{"itemIdentifier":"m1"}]}`. With a provider you get the full chain. Same shape in
`SnsApplication:70`, `KinesisStreamApplication:101`, `DynamoDbApplication:57`,
`KafkaApplication:97`, `EventGridApplication:69`, `ServiceBusApplication:120`, `S3Application:67`,
`QueueStorageApplication:71`, `PubSubMiddlewareApplication:59`.

The specific gap: **an infrastructure exception (`BenzeneException` from a failed resolve) is treated
exactly like a business exception.** The first is never retryable and affects every record; the second
is per-message. Redriving a wiring failure to the DLQ is the worst possible response.

### 1.4 Class D — genuinely message-time only

Be honest about these; no check can pull them forward.

- **Unroutable topic** — the topic string arrives on the message. `MessageRouter:106,110` already
  distinguishes *missing* topic from *no handler*, and both messages are good. What a startup check
  *can* add is context on the "no handler" branch: an empty registry (wiring never ran) is knowable at
  warm-up and is a different bug from a typo.
- **Payload/schema mismatch, media-format negotiation, version selection** — all depend on message
  content (`MediaFormatNegotiator.SelectRead`, `VersionSelector`).
- **Anything conditional on message headers** (auth, idempotency keys, correlation).
- **`UseBenzeneInvocation` ordering across the sub-pipeline boundary** — already correctly deferred in
  `work/debuggability-assessment.md`; it becomes checkable *only* if §1.0(3) is fixed.

### 1.5 Class E — the test-suite variants of the same disease

- **E1 — `UseMessageHandlers()` with no arguments scans `AppDomain.CurrentDomain.GetAssemblies()`**
  (`…/Extensions.cs:58`). That is why `test/Benzene.Core.Test/Autogen/Schema/OpenApi/SpecTest.cs:77,87,
  107,117,127,137` hard-code `Assert.Equal(6, document.Components.Schemas.Count)` over the whole test
  assembly and break when any `[Message]` handler is added anywhere. The assertion is a symptom; the
  disease is the ambient-scan default. Fix the tests by giving those hosts an explicit type list
  (`UseMessageHandlers(new[]{ typeof(X) })`) — a test-only change, no product risk.
- **E2 — Test doubles drifting from their decoder** (the `Benzene.Azure.Function.AspNet` wire-key case).
  Structural fix: the `*.TestHelpers` package for a transport should be the only place a wire key is
  spelled, and it should spell it by referencing the transport package's constant. Enforceable cheaply
  by an assertion test per transport ("the builder's output round-trips through the real getter"),
  which is worth adding to `test/Benzene.Core.Test` per transport rather than by convention alone.

---

## 2. Where each class can be caught

### 2.1 Build time (Roslyn analyzer)

**Precedent exists and is under-exploited.** `Benzene.CodeGen.SourceGenerators` already emits
**BENZ001 "Duplicate message topic"** as a compile **error**
(`src/Benzene.CodeGen.SourceGenerators/MessageHandlerSourceGenerator.cs:13-19,121`), packaged under
`analyzers/dotnet/cs`. But **nothing consumes it except `test/Benzene.Core.Test`** — not the templates,
not the examples, not transitively from `Benzene.Core.MessageHandlers`.

Analyzable at build time (whole-compilation, no DI knowledge needed):

- **BENZ001 duplicate `[Message]` topic** — already written, just not delivered. *Ship it.*
- **BENZ002 `[HttpEndpoint]` without `[Message]`** — currently `UnroutedHttpEndpointCheck`, a
  first-request throw. The rule is purely syntactic; an analyzer is strictly better (Error, in the
  IDE, before you run). Keep the runtime check as the belt-and-braces path for explicitly-registered
  handlers.
- **BENZ003 handler type implements `IMessageHandler<,>` but carries neither `[Message]` nor any
  explicit registration** — warning, not error (explicit registration is legal and invisible to the
  analyzer), but it catches B3's most common shape.

Not analyzable: anything depending on which `Add*`/`Use*` calls actually ran — that's a data-flow
problem across a fluent builder, and any analyzer attempting it will produce false positives on the
"one codebase, several deployables" pattern.

**Verdict: yes, worth it — but almost entirely as a *distribution* change, not new analyzer code.**
Make `Benzene.CodeGen.SourceGenerators` a `PrivateAssets="all"` dependency of
`Benzene.Core.MessageHandlers` (or at minimum add it to every template and example). Effort: hours.

### 2.2 Container-build time — `ValidateOnBuild`

`MicrosoftServiceResolverFactory(IServiceCollection)` calls plain `container.BuildServiceProvider()`
(`src/Benzene.Microsoft.Dependencies/MicrosoftServiceResolverFactory.cs:22`) — validation off.

**[repro], the maintainer's failure #1, with `ValidateOnBuild = true`, at build time:**

```
Some services are not able to be constructed
  (… 'IMessageHandlerDefinitionLookUp' … Unable to resolve service for type 'IVersionSelector' …)
  (… 'IMessageHandlerFactory' … Unable to resolve service for type 'IDefaultStatuses'
      while attempting to activate 'MessageHandlerFactory'.)
```

That is **strictly better than the runtime error**: it names *both* missing services, it happens
before any message, and it costs one line.

Coverage and limits, measured not assumed:

- ✅ Catches A1 and A2 (closed-generic descriptors with a constructor).
- ❌ Skips **open generics** — `MessageRouter<>` is registered as `TryAddScoped(typeof(MessageRouter<>))`
  (`…/DI/Extensions.cs:159`), so the router itself is not validated; it was caught here only because
  its collaborators are closed registrations.
- ❌ Skips **factory-registered descriptors** (`AddScoped<T>(Func<IServiceResolver,T>)`), which Benzene
  uses heavily. That both limits coverage *and* keeps the false-positive surface small.
- ✅ **[repro] `ValidateOnBuild = true, ValidateScopes = false` passes clean on the whole
  `examples/Aws` StartUp.** No false positives on the flagship app.
- ⚠️ **`ValidateScopes = true` fails `examples/Aws` today — and it is right to.** It finds a real
  captive dependency: `HealthCheckBuilder` registers `AddSingleton<IHealthCheckFinder, HealthCheckFinder>()`
  (`src/Benzene.HealthChecks/HealthCheckBuilder.cs:24`) while `IDependencyHealthCheck` is scoped
  (`src/Benzene.HealthChecks.Core/DependencyHealthCheckExtensions.cs:36`). Fix the lifetime first;
  then `ValidateScopes` can follow.

Not portable: Autofac has no direct equivalent, so this must be per-container (`Benzene.Microsoft.Dependencies`
only), which is fine — it is the default container for every host.

### 2.3 Composition time (while `Use*`/`Add*` run)

Only two things are genuinely knowable here and not later:

- **Pipeline shape** — order, and "does this pipeline end in a terminal middleware" (B1). Knowable only
  at composition because the builder is destroyed at `Build()` (§1.0(3)).
- **Which `Add*` calls ran** — recordable, but §2.2 makes it redundant for Class A.

Composition time is **the wrong place to throw**: a `Configure` method that mounts several transports
is half-built while it runs, and the "one codebase, several deployables" pattern means a legitimate
deployable mounts a subset. Composition should *record*, and warm-up should *judge*.

### 2.4 Warm-up time (container built, no message yet)

This is where the interesting checks belong, and the seam already exists — but three things block it:

1. **The runner swallows everything** (`BenzeneWarmUpExtensions.cs:44-54`). Correct for *warming*
   (a failed JIT-warm is harmless); fatal for *checking*.
2. **It is opt-in** via `AddBenzeneWarmUp()`, so by default nothing runs at INIT at all.
3. **Only `AwsLambdaHost` calls it.** `Benzene.AspNet.Core`, `Benzene.HostedService`, and
   `Benzene.Azure.Function.Core`'s `UseBenzene<TStartUp>` never do
   (`src/Benzene.AspNet.Core/BenzeneExtensions.cs:98`, `src/Benzene.HostedService/HostBuilderExtensions.cs:12`,
   `src/Benzene.Azure.Function.Core/HostBuilderExtensions.cs:21`).

Note the accidental behaviour today: `SerializationWarmUpTask` and `ValidationWarmUpTask` both call
`IMessageHandlersFinder.FindDefinitions()`, so with warm-up enabled the **duplicate-topic
`BenzeneException` from `ReflectionMessageHandlersFinder` is already thrown at INIT — and then
swallowed**, only to be re-thrown on the first message. The information is there; the runner throws it
away.

What warm-up can own that nothing else can:

| Check | Why warm-up | Verdict |
|---|---|---|
| Dry-resolve every middleware in every pipeline (A1–A4, incl. open generics `ValidateOnBuild` skips) | needs the built container **and** the pipeline tree | needs §1.0(3) seam |
| Terminal middleware present per pipeline (B1) | ditto | needs seam + a terminal marker |
| Duplicate topic across finders (B2) | needs all finders resolved | free today, just stop swallowing |
| Empty handler registry (B3) | needs the finder | free today |
| `[HttpEndpoint]` handlers vs routes (existing `UnroutedHttpEndpointCheck`) | forces `RouteFinder` construction at INIT rather than first request | free today — just resolve `IRouteFinder` |
| `ValidateOutboundRouting` / `FindUnmappedResponseHandlers` / `FindPipelineOrderingIssues` | already written | free today, just call them |

**The single missing primitive is the pipeline-introspection seam.** `MiddlewarePipeline<TContext>`
already stores `Func<IServiceResolver, IMiddleware<TContext>>[]`; nothing exposes it, and sub-pipelines
are unreachable because `CreateMiddlewarePipeline` drops the builder. The cheapest fix that preserves
the existing lazy-resolution semantics: have `MiddlewarePipelineBuilder` register its own factory array
into the shared `IRegisterDependency` (every builder already holds one, and `CreateMiddlewarePipeline`
passes it down — `DependencyExtensions.cs:37`), so a warm-up task can enumerate every pipeline in the
process without changing how any of them execute. Note `BenzeneApplicationBuilder.Create<TContext>()`
(`src/Benzene.Core.Middleware/BenzeneApplicationBuilder.cs:35`) currently makes a *new*
`RegisterDependency` per call, which fragments this — it should pass the shared one.

`PipelineOrderingDiagnosticsExtensions.FindPipelineOrderingIssues` is already 90% of the dry-resolve
loop; it resolves `items[i](serviceResolver)` and, on failure, sets `names[i] = null`
(`src/Benzene.Diagnostics/PipelineOrderingDiagnosticsExtensions.cs:55-65`). Correct for a name-reading
advisory; the *same loop* reporting the caught exception instead of discarding it is the whole of the
A1/A4 warm-up check.

### 2.5 Summary table

| Case | Build | Container build | Composition | Warm-up | Message-time only |
|---|:-:|:-:|:-:|:-:|:-:|
| A1 missing `AddBenzene()` | | **✔ free** | | ✔ | |
| A2 handler dep unregistered | | **✔ free** | | ✔ | |
| A3 custom context, no `Add*` | | ✔ partial | | ✔ | |
| A4 `Use*` without `Add*` | | ✔ partial | | **✔** | |
| B1 no terminal middleware | | | ✔ record | **✔ judge** | |
| B2 duplicate topic cross-finder | ✔ (attrs only) | | | **✔ free** | |
| B3 handler seen by no finder | ✔ warn | | | **✔ free** | |
| B4 validator never applied | | | | **✔** | |
| B5 unrecognised event → 200 | | | | | *bug fix, not a check* |
| B6 `[HttpEndpoint]` no route match | ✔ (BENZ002) | | | ✔ (exists) | |
| C infra vs business exception | | | | | **✔ message-time, fix the classification** |
| D topic/payload/version | | | | | **✔ genuinely** |

---

## 3. Fail-fast vs report

The governing constraint is real and must be honoured: **one codebase frequently builds several
deployables that each mount a subset of the handlers/transports.** A check that hard-fails a legitimate
subset is worse than the bug it prevents. That rules out, permanently:

- ✗ "every discovered handler must be routable from some mounted pipeline"
- ✗ "every registered `Add*` must have a matching `Use*`"
- ✗ "every topic in the assembly must be reachable"

The rule I would adopt: **throw only when the arrangement cannot possibly be intentional.**

| Check | Throw / Log / Opt-in | Justification |
|---|---|---|
| `ValidateOnBuild` (A1, A2) | **Throw**, on by default | An unconstructible service descriptor is never intentional. Empirically clean on `examples/Aws`. Provide `BenzeneOptions.ValidateContainerOnBuild = false` as the escape hatch, documented, not needed. |
| `ValidateScopes` | **Opt-in initially**, default-on after the `HealthCheckFinder` fix | It finds real bugs but Benzene has at least one; don't ship a check your own example fails. |
| Middleware dry-resolve (A4) | **Throw** | A middleware in a mounted pipeline that cannot be constructed will fail on the first message that reaches it. There is no arrangement where that is intended. Caveat: it must resolve on a *throwaway scope*, and it must not run middleware — construction only. |
| Terminal middleware missing (B1) | **Throw** | A pipeline that always runs off the end always fails the message. Not a legitimate subset — a legitimate subset omits the *pipeline*, not its terminal. |
| Duplicate topic across finders (B2) | **Throw** | Already throws within one finder (`ReflectionMessageHandlersFinder:96`). Consistency argues for the same outcome regardless of which finder found it. |
| Empty handler registry (B3, whole process) | **Log Error** | A service with zero handlers is almost certainly broken, but a mesh/probe-only deployable is a real shape. |
| Validator with no handler / handler with no validator (B4) | **Opt-in advisory** | Genuinely ambiguous — most request types legitimately have no validator. |
| `FindUnmappedResponseHandlers` | **Stay advisory** | Its own doc comment is right: transport-agnostic handlers make false positives structural. |
| `ValidateOutboundRouting` | **Keep throwing, but call it from warm-up** | Contract-derived, no ambiguity; today nobody calls it. |
| `FindPipelineOrderingIssues` | **Log Warning** from warm-up | Already correct; just needs to run. |

Two cross-cutting rules:

- **One kill switch, not fifteen.** `AddBenzeneStartUpChecks(x => x.Advisory())` /
  `.Disabled()` — a newcomer must be able to turn the whole thing off in one line when a check is
  wrong, or they will abandon rather than debug the debugger.
- **Never a per-message cost.** Every check runs once, on a throwaway scope, off the request path.

---

## 4. Error message quality

### 4.1 The evidence — three misleading hints stacked on one correct one

The registration-hint machinery (`src/Benzene.Core/DI/RegistrationCheck.cs`) is a genuinely good idea
with two bugs that make it actively harmful.

**[repro] `docs/getting-started-aws.md`, followed verbatim, first request:**

```
[BenzeneException] Unable to resolve type Benzene.Core.MessageHandlers.MessageRouter`1[[…ApiGatewayContext…]]
Benzene.Aws.Lambda.Core, Version=0.0.2.0, … is registered in .AddMessageHandlers(<assemblies>) from Benzene.Core.MessageHandlers.MessageRouter<>

You might be missing this in your dependency registration

    .UsingBenzene(x => x.AddMessageHandlers(<assemblies>))

[InvalidOperationException] Unable to resolve service for type 'IDefaultStatuses' while attempting to activate 'MessageHandlerFactory'.
```

The developer **did** call `.AddMessageHandlers(...)`. The fix is `.AddBenzene()`. **[repro]** proves
the machinery already knows this — `RegistrationErrorHandler.CheckType(typeof(IDefaultStatuses))`
returns, today, verbatim:

```
… is registered in .AddBenzene() from Benzene.Core.MessageHandlers.IDefaultStatuses
You might be missing this in your dependency registration
    .UsingBenzene(x => x.AddBenzene())
```

**Bug 1 — wrong precedence.** `RegistrationCheck.Describe` (`RegistrationCheck.cs:112`) prefers the
requested type and returns early if it matched: `var direct = …; return !string.IsNullOrEmpty(direct) ? direct : CheckException(exception);`.
When the requested type *is* registered by a call the developer already made and the failure is
**transitive**, the direct hint is a confident false positive that suppresses the true one. Fix:
walk the exception chain to the **innermost** recognised type first (that is the root cause), and
append the direct hint only as secondary context. ~10 lines, no API change.

**Bug 2 — the message renders its own fields transposed.** `GetMatches(x.Key, …)` passes the *package*
(assembly full name) as `type`, and `.Select(package => new RegistrationMatch(type, typeRegistrations.Key, package))`
passes the matched *type name* as `package` (`RegistrationCheck.cs:158,163`), while `FormatResponse`
prints `"{item.Type} is registered in {item.Method} from {item.Package}"` (`:192`). Result:
`"Benzene.Aws.Lambda.Core, Version=0.0.2.0, … is registered in .AddMessageHandlers() from …MessageRouter<>"`
— assembly and type swapped, and it names the *wrong assembly* besides.

**The compounding effect.** **[repro]** an `[HttpEndpoint]` handler missing its `[Message]` produces
~25 lines: two false "you might be missing `.AddHttpMessageHandlers()` / `.AddApiGateway()`" blocks
wrapping the genuinely excellent `UnroutedHttpEndpointCheck` message at the very bottom. A newcomer
reads top-down and acts on the first suggestion — which is wrong.

### 4.2 Proposed house style for Benzene startup/dispatch exceptions

Four parts, in this order, one sentence each:

1. **What failed, in the developer's vocabulary.** Their `[Message]` handler, their topic, their
   `Use*` call — not `IDefaultStatuses`, not `MessageHandlerFactory`.
2. **Where.** Which pipeline / transport / topic / handler type. `MessageRouter`'s handler-signature
   mismatch message (`MessageRouter.cs:122-124`) already does this well.
3. **The fix, as the literal call to add.** `.AddBenzene()`, `[Message("topic")]`,
   `.UseMessageHandlers()` — with the surrounding context (`services.UsingBenzene(x => x…)`).
4. **One link.** `See https://benzene.app/docs/…`.

And three prohibitions:

- **Never name an internal type as the subject.** Internal types may appear as *evidence* after the
  actionable sentence, never as the headline. `IDefaultStatuses` is the model of what not to do.
- **Never emit a hint you are not confident in.** A confident wrong hint costs more than no hint.
  If several registration calls match, say "one of" and list them; if the direct and transitive
  analyses disagree, lead with the transitive one.
- **Never wrap a good message in a generic one.** If an inner exception already satisfies (1)–(3),
  the outer `Unable to resolve type …` should say only "while resolving X (see inner exception)".

Two existing messages already meet the bar and should be the exemplars quoted in any style note:
`UnroutedHttpEndpointCheck` (`src/Benzene.Http/Routing/UnroutedHttpEndpointCheck.cs:66-68`) and
`MessageRouter`'s topic-missing text (`…/MessageRouter.cs:94-96`).

**One more message to fix:** `MessageRouter`'s `"No handler found for topic '{id}'"` (`:110`) should
distinguish the three cases the maintainer named. All three are knowable at that point from
`IMessageHandlerDefinitionLookUp`:

- registry empty → "no message handlers are registered at all — check `AddMessageHandlers(...)`
  received the assembly containing your handlers";
- topic id unknown, near-match exists → "did you mean `'order:create'`?" (Levenshtein over registered ids);
- topic id known, version not → "topic `'x'` is registered at versions [1, 2] but the message asked for 3".

---

## Implementation status (2026-07-29)

All eight recommendations were taken. Six landed as written, one landed differently on measured
grounds, and one was deliberately not implemented because this document itself says it needs a
decision it cannot make. Two claims in the analysis above turned out to be wrong when tested; both
are corrected here rather than quietly edited out.

| # | Status | Notes |
|---|---|---|
| 1. Fix the docs | **Done** | All five guides gained `.AddBenzene()`; the AWS quickstart lost the `.AddHttpMessageHandlers()` it did not need. A directive-driven check compiles every snippet marked as complete code, and the AWS quickstart is also *loaded and run* against a real API Gateway request — compiling alone would never have caught this bug. Removing `.AddBenzene()` from the markdown reproduces the reported failure verbatim. |
| 2. `ValidateOnBuild` | **Done, opt-in** | Default-on failed **67 tests across four projects**, and every one was a legitimately partial container. Partial composition is supported here, so a check that rejects one is worse than the bug it catches. Opt-in via `new MicrosoftServiceResolverFactory(services, validateOnBuild: true)`. `ValidateScopes` rides with it, after the `HealthCheckFinder` fix. |
| 3. `RegistrationCheck` bugs + dead guard | **Done** | Chain-first precedence, untransposed type/package, and `AwsEventStreamContext.Handled` so the "event type has not been recognized" message can finally fire. |
| 4. Ship the analyzer | **Done** | Referenced by `Benzene.Core.MessageHandlers`, plus `examples/Directory.Build.props` (analyzer assets do not flow along a ProjectReference chain). BENZ002 added. |
| 5. Warm-up as a check phase | **Done** | `IStartUpCheck`, on by default, run from every host and every `Build*` test-host extension, with one kill switch. Four checks wired: duplicate-topic (throws), empty-handler-registry (logs), http-routes, outbound-routing, plus the advisory unmapped-response-handlers. |
| 6. Pipeline-introspection seam | **Done (dry-resolve)** | `PipelineDescriptor` published at `Build()`; `pipeline-resolution` check constructs every middleware in every pipeline at start-up. ~16ms on `examples/Aws`, measured. **Correction:** this document says `BenzeneApplicationBuilder.Create<TContext>` "fragments this" by minting a fresh `RegisterDependency` — it does not. That type is a stateless adapter over the container, so a fresh one over the same container fragments nothing, and sub-pipelines already share the outer builder's registration path. No change to `BenzeneApplicationBuilder` was needed. The terminal-middleware check (B1) landed on the same seam: `ITerminalMiddleware` is a non-generic marker, carried by `MessageRouter<>`, `MiddlewareRouter<,>` (so every transport router inherits it, including on the outer event-stream pipeline), every client send middleware, `ContextConverterMiddleware`, `Split`, the health-check endpoints, and the spec/mesh UIs. ~9ms on `examples/Aws`, measured. |
| 7. Classify infra vs business exceptions | **Done** | Decision taken: an infrastructure failure fails the whole SQS invocation rather than dead-lettering one record at a time. **Two things this document got out of date on:** the "fail the whole invocation" mechanism already existed (`SqsBatchFailureMode.FailWholeBatch` + `SqsBatchProcessingException`) as a static config choice, so the work was deciding *when* to trigger it, not building it; and a classification vocabulary already existed in `MeshIssueClassification` (`config-wiring`/`dependency`/`exception`/`validation`) for mesh issue feeds, though it never reached the transport's retry decision. |
| 8. Test-suite hygiene | **Done** | `SpecTest` names its nine handlers instead of scanning the AppDomain; six TestHelpers builders now reference `BenzeneWireNames.DefaultTopic`, with per-transport round-trip assertions that hold whether they do or not. |

### What the terminal-middleware check found on the way in

The full suite was the measurement, the same as for `ValidateOnBuild`, and the blast radius was five
tests rather than sixty-seven — all of them the same shape, and all of them right to fail:

- **`UseLivenessCheck`/`UseReadinessCheck` alone is a complete pipeline.** The health-check endpoints
  are built from `FuncWrapperMiddleware`, which is also what every pass-through decorator is built
  from, so their intent lives inside a lambda and cannot be read from a type. That is what
  `TerminalFuncWrapperMiddleware` and the public `UseTerminal(...)` overloads exist for.
- **`UseApiGatewayCustomAuthorizer` produces the authorizer response** and nothing follows it — the
  same fix.

Two things changed that were not strictly the check's business but were found by it:

- **`Split` built its branch pipeline inside the message lambda**, so the chain was rebuilt on every
  message and the branch was invisible to both start-up checks. Built once at configuration time now.
- The marker doubles as documentation: "which middleware can end a pipeline" was previously only
  answerable by reading each one for whether it calls `next`.

The residual false positive is a user's own terminal middleware, unmarked. It is reported with the
remedy in the message, and the marker changes nothing about execution.

### A third correction, found while implementing 6 and 7

**The start-up check phase shipped mostly inert.** `TryAddSingleton<IStartUpCheck, X>` de-duplicates
by *service* type, so of the five checks registered across five packages, only the first was ever in
the container. Two of the three core checks silently did not exist and nothing said so. Fixed with
`TryAddSingletonImplementation`, which de-duplicates by implementation the way Microsoft DI's
`TryAddEnumerable` does; a test now asserts all three core checks are present.

**`InlineAwsLambdaStartUp.BuildHost()` never ran the checks.** That is the in-repo test host behind
hundreds of tests, so the one place a wiring bug was guaranteed *not* to be caught was the cheapest
place to catch it. It runs them now — which also means the suite is real evidence that the checks
pass, where before it was evidence of nothing.

### Two corrections to the analysis above

**§2.2 is wrong that `ValidateScopes = true` fails `examples/Aws` today.** It does not. The captive
dependency is real — `HealthCheckFinder` was a singleton over scoped `IEnumerable<IHealthCheck>` —
but the check only fires once a scoped health check is actually *registered*, and that example
registers none, so the enumerable resolves empty and nothing scoped is captured. Reproducing it took
a container with a scoped check in it. That repro is now a test.

**§5.4's `PrivateAssets="all"` analyzer dependency would not have delivered the analyzer.**
`PrivateAssets="all"` applies the rules to `Benzene.Core.MessageHandlers`'s own compilation and stops
them at the package boundary — the exact situation being fixed. The reference needs
`PrivateAssets="none"` (NuGet's default packs ProjectReferences as `exclude="Build,Analyzers"`), no
`ReferenceOutputAssembly="false"` (it suppresses the nuspec dependency outright), and
`Private="false"` (so a Roslyn component does not land in every publish folder). Each was checked
against the produced `.nupkg`.

### What delivering the analyzer turned up

Two bugs nobody could have hit while nobody consumed it. The generated
`AddGeneratedMessageHandlers` opened with `services.GetService<MessageHandlersList>()`, which
`IBenzeneServiceContainer` has never had — it registers services, it does not resolve them — so the
generated file did not compile, and every example failed to build on the first try. And generated
type names were unqualified, so a request type named `Request` resolved to the
`Benzene.Core.MessageHandlers.Request` *namespace* instead. Both fixed, with a test that compiles
the generated output.

The generated class is now `internal`: the generator runs in every consuming assembly, so two
handler-carrying projects in one solution would otherwise produce two public
`BenzeneGeneratedHandlersExtensions` in the same namespace and an ambiguous call at the composition
root.

---

## 5. Recommendation, sequenced

Opinionated, ordered by (time-to-diagnose saved) ÷ effort. Items 1–4 are days, not weeks, and need no
new abstraction.

**1. Fix the docs. (hours, zero risk, biggest single win)**
`getting-started-aws.md`, `getting-started.md`, `getting-started-kafka.md`,
`getting-started-rabbitmq.md`, `azure-functions.md` all omit `.AddBenzene()` and therefore do not run.
Add it. Then add a CI job that compiles the getting-started snippets — the examples all call
`.AddBenzene()` and work, so only the docs are wrong, which is exactly what nothing checks today.
(Consider also removing `.AddHttpMessageHandlers()` from the AWS quickstart: **[repro]** the app works
without it because `AddApiGateway()` registers the route finder. The guide omits the call you need and
includes one you don't.)

**2. Turn on `ValidateOnBuild`. (one line + tests)**
`src/Benzene.Microsoft.Dependencies/MicrosoftServiceResolverFactory.cs:22`. Catches the maintainer's #1
at container build with a better message than the runtime path, and passes clean on `examples/Aws`.
Leave `ValidateScopes` off; file the `HealthCheckFinder` singleton-consuming-scoped bug
(`src/Benzene.HealthChecks/HealthCheckBuilder.cs:24`) and turn `ValidateScopes` on after.

**3. Fix the two `RegistrationCheck` bugs and the dead `AwsLambdaEntryPoint` guard. (a day)**
- `Describe`: prefer the innermost exception-chain match over the requested type (§4.1 Bug 1).
- `FormatResponse`/`GetMatches`: untranspose type and package (§4.1 Bug 2).
- `AwsEventStreamContext.Response`: default to `null` (or add `bool Handled`) so the excellent
  "The event type has not been recognized…" message at `AwsLambdaEntryPoint.cs:53` can actually fire
  instead of a silent empty 200 (§1.2 B5).

**4. Ship the analyzer that already exists. (hours)**
Add `Benzene.CodeGen.SourceGenerators` as a `PrivateAssets="all"` analyzer dependency of
`Benzene.Core.MessageHandlers` (or at minimum to every template and example). BENZ001 (duplicate topic)
is written, tested, and reaching nobody. Add BENZ002 (`[HttpEndpoint]` without `[Message]`) — the rule
is already implemented in `UnroutedHttpEndpointCheck` and is purely syntactic.

**5. Make warm-up a check phase, and run it on every host. (a few days)**
- Rename the concept: warm-up stays best-effort, and a new `IStartUpCheck` runs beside it with
  exceptions **propagating**. Same runner, two lists.
- Turn it **on by default** (today `AddBenzeneWarmUp()` is opt-in and only `AwsLambdaHost` calls
  `WarmUp()` at all).
- Call it from `Benzene.AspNet.Core`, `Benzene.HostedService`, and `Benzene.Azure.Function.Core`'s
  `UseBenzene<TStartUp>`, and from `BenzeneTestHost` — so a wiring bug becomes a **red unit test**,
  which is the cheapest place of all to find it.
- Wire in, unchanged, the four checks that already exist: `ValidateOutboundRouting`,
  `LogUnmappedResponseHandlers`, `LogPipelineOrderingIssues`, and a forced `IRouteFinder` resolve
  (which promotes `UnroutedHttpEndpointCheck` from first-request to INIT).
- Add the two free ones: duplicate-topic-across-finders (B2) and empty-registry (B3).
- Single kill switch: `AddBenzeneStartUpChecks(x => x.Advisory() | x.Disabled())`.

**6. Add the pipeline-introspection seam, then the dry-resolve and terminal checks. (a week, needs a plan)**
`MiddlewarePipelineBuilder` publishes its factory array into the shared `IRegisterDependency`;
`BenzeneApplicationBuilder.Create<TContext>` stops minting a fresh one
(`src/Benzene.Core.Middleware/BenzeneApplicationBuilder.cs:35`). Then a warm-up check can, for every
pipeline including transport sub-pipelines: construct each middleware (throw with the failing pipeline
+ index named), and assert a terminal middleware exists (B1 — needs a small `ITerminalMiddleware`
marker on `MessageRouter<>` and friends). This also unblocks the `UseBenzeneInvocation` ordering rule
deferred in `work/debuggability-assessment.md`. This is the only item that adds surface area, and it is
the only one that closes B1 — the failure mode with the worst blast radius (silent DLQ of every
message).

**7. Classify infrastructure vs business exceptions at the transport boundary. (a few days)**
In every `*Application` per-record catch (§1.3), a `BenzeneException` originating from service
resolution is not retryable and affects the whole batch. It should be logged at a distinct, greppable
level *and* — for at least SQS — fail the whole invocation rather than DLQ every record individually.
Needs a product decision on batch semantics; flagging rather than prescribing.

**8. Test-suite hygiene. (hours, do alongside)**
Give `SpecTest`'s hosts explicit handler type lists instead of the ambient `UseMessageHandlers()`
AppDomain scan (§1.5 E1), so the six hard-coded schema counts stop being a tripwire for unrelated
work. Add a per-transport round-trip assertion pinning each `*.TestHelpers` builder to its real
getter (§1.5 E2).

---

## Appendix — what I verified by running it

Scratch projects (net10.0, project references into `src/`), not committed:

1. Hand-composed SQS Lambda without `.AddBenzene()` → reproduced the maintainer's #1 verbatim,
   including the misleading `.AddMessageHandlers()` hint, and confirmed
   `RegistrationErrorHandler.CheckType(typeof(IDefaultStatuses))` already returns the correct
   `.AddBenzene()` guidance.
2. `docs/getting-started-aws.md` steps 3–6 verbatim → `BenzeneException` on the first request; adding
   `.AddBenzene()` → HTTP 200. Removing `.AddHttpMessageHandlers()` → still 200.
3. Same app with `[Message]` removed → the three-hint stack of §4.1.
4. `ValidateOnBuild = true` on the broken quickstart → caught at build, naming `IVersionSelector` and
   `IDefaultStatuses`. On the fixed quickstart and on the full `examples/Aws` StartUp → passed.
   With `ValidateScopes = true` on `examples/Aws` → found the `HealthCheckFinder` captive dependency.
5. `UseSqs(sqs => { })` (no `UseMessageHandlers`) → `{"batchItemFailures":[…]}`, no log, no exception.
6. `AwsLambdaEntryPoint.FunctionHandlerAsync` with an unrecognised payload → returned 0 bytes, no
   exception; the "event type has not been recognized" guard never fires.
7. Reflection-discovered handler + explicit `AddMessageHandler<Shadow,…>` on the same topic → no error,
   shadow silently dropped.
