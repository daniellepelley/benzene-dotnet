# Ergonomics review: outbound clients, contracts, codegen, CLI, testing support

**Scope:** benzene-dotnet @ `f3f1be5`, reviewed against
`docs/specification/design-principles.md` §4.1 ("The shorthand ladder") in the spec repo.
**Method:** every claim below is traced to a file:line. No `dotnet` SDK was available in the review
environment, so nothing was compiled or run; every "this compiles / this throws" statement is
**trace-only** and marked as such where it matters.

## Executive verdict

- **Go-live blockers: 4** — two transports have no outbound-routing rung while the docs say they do
  (B1); the `benzene` CLI hangs when invoked bare and has no `--help` path (B2); the contract-artifacts
  doc's own CI snippet exits non-zero (B3); `docs/clients.md`, the outbound doc, contradicts the code in
  six places (B4).
- **Should-fix: 15** — the biggest are a convention with no start-up check (S1), a doc that describes a
  validation mechanism that does not exist in that shape (S2), no public test double for the one
  interface every caller depends on (S3), and a batch-send ladder with only a bottom rung (S4).
- **Polish: 10.**
- The good news is real: the outbound-routing core (`AddOutboundRouting` → `IBenzeneMessageSender` →
  `.UseX(...)` → `.Convert(...)` → `UseXClient()`) is a textbook §4.1 ladder for eleven transports, the
  generated client is honestly composed from public API, and every codegen failure I traced names what
  it looked for.
- Verdict for this territory: **NEEDS CHANGES** for go-live (B1-B4); the should-fix list is a
  post-go-live backlog with the exception of S1/S2, which are the §4.1 start-up-check rule applied to
  the most common misconfiguration.

---

## Findings, by severity

### B1. AWS Lambda and gRPC have no outbound-routing rung; the docs say they do  [ladder-broken]

**Grade:** BLOCKER
**§4.1 clause:** "Every capability a service needs *routinely* MUST have a shorthand. A capability
that exists only in explicit form is unfinished, not minimal." Also "The ladder MUST be visible from
the top" — the doc names a rung that is not there.

**Evidence.**
- `src/Benzene.Clients.Aws.Lambda/Extensions.cs:50-65` — the only `UseAwsLambda` overloads are on
  `IMiddlewarePipelineBuilder<IBenzeneClientContext<T, Void>>`. There is no
  `IMiddlewarePipelineBuilder<OutboundContext>` overload; `grep -n "OutboundContext"` on the file
  returns nothing.
- `src/Benzene.Grpc.Client/Extensions.cs:51-63` — same shape: `UseGrpc<T>` on
  `IBenzeneClientContext<T, Void>` only, and its converter "always maps the response to `Void`"
  (`docs/clients.md:520`).
- `src/Benzene.Clients.Aws.Lambda/CLAUDE.md` — "**No `OutboundContext` overload of `.UseAwsLambda(...)`
  yet** — deliberately deferred".
- `docs/clients.md:26` says each AWS package "gives the `.UseSqs(...)`/`.UseSns(...)`/
  `.UseEventBridge(...)`/`.UseAwsLambda(...)` route extensions".
- `docs/reference/packages.md:150` — "Invoke another AWS Lambda (`AwsLambdaBenzeneMessageClient`,
  `.UseAwsLambda(...)`)" in a table where every sibling's `.UseX(...)` is the route extension.
- `docs/clients.md:282` — "`.UseAwsLambda(...)` and the gRPC equivalent are not yet implemented on the
  outbound routing pipeline".
- `docs/clients.md:337` — the fallback: "There's no built-in decorator chain anymore for a
  directly-resolved `IBenzeneMessageClient` — if you need retry/correlation/trace-context on one of
  these, either write a small wrapper implementing `IBenzeneMessageClient` yourself ...".

**What the user experiences.** A generated client (`MessageClientSdkBuilder.cs:223`,
`_sender.SendAsync<...>(topic, message, headers)`) targets `IBenzeneMessageSender`. If the service it
calls is a Lambda — the port's flagship host — or a gRPC service, no route can be registered for that
topic, so the generated client cannot be used at all through the documented path. The user drops to
`AwsLambdaBenzeneMessageClient` and loses `.UseRetry`, `.UseCorrelationId`, `.UseW3CTraceContext`,
`.UseOutbox`, `.UseClaimCheck` and the start-up route check — every cross-cutting feature the routing
path exists to provide. This is the one case where the framework has taken a steer (`IBenzeneMessageSender`)
and made the two most important AWS/gRPC targets cost more than the declined steer.

**Proposed change.** The plumbing already exists. `DefaultBenzeneMessageSender.SendAsync`
(`src/Benzene.Clients/DefaultBenzeneMessageSender.cs:51-59`) already deserialises a raw
`BenzeneMessageClientResponse` once `TResponse` is known — precisely the envelope
`AwsLambdaBenzeneMessageClient` speaks — so a typed request/response Lambda route needs only an
`OutboundLambdaContextConverter : IContextConverter<OutboundContext, LambdaSendMessageContext>` that
builds the `BenzeneMessageClientRequest` and chooses `InvocationType` from whether a response is wanted,
following the `OutboundSqsContextConverter` recipe. gRPC is the same recipe over `GrpcSendMessageContext`.

```csharp
// Before (today): a Lambda target cannot be routed; the user hand-registers the client and
// forfeits every outbound middleware.
services.AddScoped<IBenzeneMessageClient>(x =>
    new AwsLambdaBenzeneMessageClient("orders-service", x.GetService<IAmazonLambda>(), x.GetService<ILogger<AwsLambdaBenzeneMessageClient>>()));

// After: the same one-line rung every other transport has.
x.AddOutboundRouting(routing => routing
    .Route("order:create", p => p.UseW3CTraceContext().UseAwsLambda("orders-service"))   // typed response works
    .Route("greet",        p => p.UseGrpc(routes => routes.Add<HelloRequest, HelloReply>("greet", "/greet.Greeter/SayHello"))));
```

Until that ships, `docs/clients.md:26` and `docs/reference/packages.md:150` must stop listing
`.UseAwsLambda(...)` beside the route extensions.

---

### B2. `benzene` with no arguments loops forever; there is no `--help` path  [ceremony / magic]

**Grade:** BLOCKER
**§4.1 clause:** "The ladder MUST be visible from the top ... An escape hatch nobody can find is
indistinguishable from no escape hatch." For a CLI, `--help` *is* the top of the ladder. Also rule 3:
the failure must name what was looked for.

**Evidence (trace-only).**
- `src/Benzene.CodeGen.Cli/Program.cs:15-30` — `if (args.Length == 0) { do { var stringArgs =
  Console.ReadLine(); try { await consoleApplication.ExecuteAsync(stringArgs); } catch (Exception ex)
  { Console.Error.WriteLine(ex); } } while (true); }`. Under a non-interactive stdin (CI, a pipe,
  `dotnet tool run benzene` from MSBuild with input closed) `ReadLine()` returns `null` immediately;
  `ExecuteAsync((string)null)` → `CommandSplitter.Split(null)` → `args.Length` on `null`
  (`Parsing/CommandSplitter.cs:10`) throws `NullReferenceException`, which the loop catches, prints,
  and repeats — a tight infinite loop writing stack traces to stderr, never exiting.
- `src/Benzene.CodeGen.Cli.Core/Parsing/CommandParser.cs:11` — `Name = args.ElementAt(0)`. So
  `benzene --help` becomes command name `--help`; `CommandRouter.cs:19-30` prints "Command --help not
  found", prints the command list, then **throws**, so `Program.cs:37-41` exits **1**. `benzene -h`,
  `benzene build --help`: the latter never reaches help at all — `PayloadMapper` ignores unknown
  attributes and `BuildCommand` runs, failing with "No spec source given" (`SpecSourceResolver.cs:25-30`).
- The only working path is `benzene help` / `benzene help build` (`HelpCommand.cs:21-47`), which no
  doc in this repo mentions (grep for "benzene help" across `docs/` and the two package READMEs: 0).

**What the user experiences.** The first thing every newcomer types (`benzene`, `benzene --help`) either
hangs or exits non-zero. In `Benzene.CodeGen.Build.targets:95` the tool is invoked from MSBuild; a
misconfigured `$(BenzeneCliCommand)` that resolves to a bare `benzene` with no args would hang the build.

**Proposed change.**

```csharp
// Program.cs
static async Task<int> Main(string[] args)
{
    if (args.Length == 0 || args[0] is "--help" or "-h" or "-?" or "help")
    {
        await consoleApplication.ExecuteAsync(new[] { "help" }.Concat(args.Skip(1)).ToArray());
        return args.Length == 0 ? 1 : 0;   // bare invocation: usage + non-zero, never a REPL by default
    }
    if (args is ["--version" or "-v"]) { Console.WriteLine(typeof(Program).Assembly.GetName().Version); return 0; }
    ...
}
// CommandBase.ExecuteAsync: if commandArguments.Attributes contains "help" or "h" → print GetHelp(), return.
// Keep the REPL behind an explicit `benzene repl` (or `--interactive`) if it is wanted at all.
```

Also make `CommandSplitter.Split` null-safe. Not a substitute for the above: the loop should not exist
on the default path.

---

### B3. The contract-artifacts doc's own snippets exit 2  [invisible-ladder]

**Grade:** BLOCKER (it is the headline "get your contract into CI" path, and it is copy-paste broken)
**§4.1 clause:** rule 4 — the documentation of the shorthand must be correct about what it composes.

**Evidence (trace-only).**
- `src/Benzene.Descriptor/EmitOptions.cs:138-143` — `if (!hasScheme) return $"--service-version
  '{ServiceVersion}' needs --version-scheme (integer, semver or lexicographic). mesh.md §2.5 defines no
  default ..."`; `Program.cs:28-33` prints it and `return 2`.
- `docs/contract-artifacts.md:47` — `benzene-descriptor --assembly path/to/YourService.dll --service
  your-service --service-version 1.0.0` (no scheme → exit 2).
- `docs/contract-artifacts.md:110` — the CI step: `benzene-descriptor --assembly ... --service-version
  ${{ github.sha }}` (no scheme → exit 2; and a git SHA is not `semver`, so the correct fix is
  `--version-scheme lexicographic`).
- `docs/contract-artifacts.md:53-62` — the flag table has no `--version-scheme` row at all, while the
  package README (`src/Benzene.Descriptor/README.md:145-146`) says it is "**required whenever a version
  is declared**".

**What the user experiences.** They paste the CI step, the job fails with an error about a flag the
consumer-facing doc never mentions, and they go and read the package README to find out why.

**Proposed change.** `docs/contract-artifacts.md`:

```bash
# Before
benzene-descriptor --assembly bin/Release/net10.0/YourService.dll --service your-service --service-version ${{ github.sha }}
# After
benzene-descriptor --assembly bin/Release/net10.0/YourService.dll --service your-service \
  --service-version ${{ github.sha }} --version-scheme lexicographic
```

and add the `--version-scheme` row to the table with the "required whenever a version is declared"
sentence. Line 47 likewise (`--version-scheme semver`).

---

### B4. `docs/clients.md` contradicts the code in six places  [invisible-ladder]

**Grade:** BLOCKER (aggregate; this is *the* outbound document and it is wrong about which rungs exist)
**§4.1 clause:** "A shorthand's documentation MUST name the explicit form it composes."

| # | `docs/clients.md` | What the code says |
|---|---|---|
| 1 | `:3` "over SQS, SNS, AWS Lambda, Kafka, EventBridge, gRPC, or HTTP" | Route rungs exist for SQS, SNS, EventBridge, Service Bus, Event Hubs, Event Grid, Queue Storage, Pub/Sub, Kafka, RabbitMQ, HTTP, in-process (12); **not** for Lambda or gRPC (B1). Azure (4), Pub/Sub, RabbitMQ and in-process are omitted from the sentence. |
| 2 | `:26` "`.UseAwsLambda(...)` route extensions" | Does not exist on `OutboundContext` (`Benzene.Clients.Aws.Lambda/Extensions.cs`). |
| 3 | `:282` "EventBridge has `OutboundEventBridgeContextConverter` but is reached through `Benzene.Clients.Aws.EventBridge`'s own route extension" — listed under "not yet implemented" | `Benzene.Clients.Aws.EventBridge/Extensions.cs:98-106` is exactly the `OutboundContext` `.UseEventBridge(source, eventBusName?, healthCheck)` rung, and `examples/AwsMesh/Shared/MeshServiceWiring.cs:178` uses it. |
| 4 | `:320` "useful for AWS Lambda/Kafka/EventBridge/gRPC (no route extension yet)" | Kafka has one (`docs/clients.md:245` itself, `Benzene.Kafka.Core/Kafka/Extensions.cs:84,109`); EventBridge has one (row 3). |
| 5 | `:160-167` `ValidateOutboundRouting()` is "entirely opt-in" and reflects over "any type with a public static `string[] RequiredTopics` field (not just generated clients — you can add your own `*Routing` class with the same shape)" | Auto-registered as an `IStartUpCheck` by `AddOutboundRouting` (`Benzene.Clients/DependencyInjectionExtensions.cs:32-34`) and attribute-gated: `ValidateOutboundRoutingExtensions.cs:57` `if (type?.GetCustomAttribute<OutboundRoutingContractAttribute>() == null) continue;`. See S2. |
| 6 | Pub/Sub (0 mentions), Step Functions (0 mentions of "Step Functions", the package name appears once in the install table), in-process (one passing mention at `:276`, no section), `UseParallel` (0), `Benzene.Outbox`/`UseOutbox` on a route (0), batch send (0), the `SendAsync(..., version:)` overload (`Benzene.Clients/ClientExtensions.cs:67-72`) (0) | All shipped, all public, all used by examples. |

Smaller doc-honesty items in the same file, trace-only:
- `:371` `services.AddSqsMessageClient(queueUrl, pipeline => pipeline.UseSqsClient());` — the extension
  is on `IBenzeneServiceContainer` (`Benzene.Clients.Aws.Sqs/Extensions.cs:132`), not
  `IServiceCollection`; as written it only compiles if `services` is already the Benzene container.
- The constructor snippets at `:381` (SNS), `:434` (Kafka), `:444` (EventBridge, `eventBusName:` named
  argument), `:460` (gRPC) **do** match the real constructors (`SnsBenzeneMessageClient.cs:38`,
  `KafkaBenzeneMessageClient.cs:39`, `EventBridgeBenzeneMessageClient.cs:32-33`,
  `GrpcBenzeneMessageClient.cs:41-42`). `:345` passes `ILogger<T>` to a `string, IAmazonLambda, ILogger`
  constructor (`AwsLambdaBenzeneMessageClient.cs:33`) — compiles by covariance.

**Fix:** rewrite `:3`, `:26`, `:280-282`, `:318-320`, `:158-167`; add sections for Pub/Sub, in-process,
Step Functions ("not routable, and why"), `UseParallel`, batch, and the versioned send.

---

### S1. DI-resolved SDK handles are never verified at start-up  [magic]

**Grade:** SHOULD-FIX (high)
**§4.1 clause:** "The price of a convention is a start-up check ... A convention that can first fail
on the message path has not paid for itself."

**Evidence.** The shorthand rung on every DI-handle transport resolves the SDK client lazily, at send time:
- `src/Benzene.Clients.Aws.Sqs/Extensions.cs:36-42` — `app.Register(x => x.AddScoped(resolver => new
  SqsClientMiddleware(resolver.GetService<IAmazonSQS>(), ...)))`; the same shape at
  `Benzene.Clients.Aws.Sns/Extensions.cs:35-41`, `Benzene.Clients.Aws.EventBridge/Extensions.cs:25-31`,
  `Benzene.Clients.GoogleCloud.PubSub/Extensions.cs:32-37`, `Benzene.Clients.Http/Extensions.cs:81-86`,
  `Benzene.Kafka.Core/Kafka/Extensions.cs:109-113`, `Benzene.Grpc.Client/DependencyInjectionExtensions.cs:35`.
- The only start-up check on the outbound side, `OutboundRoutingStartUpCheck.cs:23-26`, calls
  `resolver.ValidateOutboundRouting()` — topics only. Nothing resolves a route's pipeline.
- The examples show what users must do instead: `examples/AwsMesh/Shared/MeshServiceWiring.cs:112-123`
  registers `IAmazonSQS`/`IAmazonSimpleNotificationService`/`IAmazonEventBridge` conditionally, with a
  comment explaining that a missing handle must not stop start-up.

**Misconfiguration that gets through.** Omit `services.AddSingleton<IAmazonSQS>(...)` and register
`.Route("order:create", p => p.UseSqs(url))`. Start-up passes (the topic exists). The auto-wired
`SqsHealthCheck` (`Extensions.cs:169-171`) will also only fail when the deep health check is polled.
The first `SendAsync("order:create", ...)` — typically inside a handler, in production — throws the
container's "no service registered for IAmazonSQS" from inside the pipeline.

**Fix.** Extend `OutboundRoutingStartUpCheck` to instantiate each route's middleware chain once on the
throwaway scope it already has (the runner gives it one: `BenzeneStartUpCheckExtensions.cs:69`). That
needs a public read model of the routing table (S9); today the only way to walk it is the reflection
in `src/Benzene.Descriptor/OutboundRouteInspector.cs:31-33,50-52`. The failure should read:

```
Benzene start-up check 'outbound-routing' failed: route 'order:create' (UseSqs) needs an IAmazonSQS
registered in the container and none is. Register one (e.g. services.AddSingleton<IAmazonSQS>(new AmazonSQSClient()))
or pass a client explicitly: .UseSqs(queueUrl, b => b.UseSqsClient(mySqs)).
```

---

### S2. `ValidateOutboundRouting` is documented as something it is not  [magic / invisible-ladder]

**Grade:** SHOULD-FIX (high)
**§4.1 clause:** rule 3 (the convention silently does nothing for the documented shape) and rule 4.

**Evidence.** `docs/clients.md:167` — "It reflects over every loaded assembly for any type with a
public static `string[] RequiredTopics` field (not just generated clients — you can add your own
`*Routing` class with the same shape) ... Entirely opt-in". Code:
`src/Benzene.Clients/ValidateOutboundRoutingExtensions.cs:55-63`:

```csharp
if (type?.GetCustomAttribute<OutboundRoutingContractAttribute>() == null) { continue; }
var field = type.GetField("RequiredTopics", BindingFlags.Public | BindingFlags.Static);
```

and `src/Benzene.Clients/DependencyInjectionExtensions.cs:32-34` — "Registered here so it simply
runs, at start-up, on every host." The package's own `CLAUDE.md` records the attribute gate as a
"**One behavior change**" from 2026-07-21; the public doc was never updated.

**Misconfiguration that gets through.** A user follows `:167`, writes `public static class
OrdersRouting { public static readonly string[] RequiredTopics = { "order:create" }; }` without the
attribute, forgets the route, and the check silently passes. First failure: `UnroutedTopicException`
on the message path (`DefaultBenzeneMessageSender.cs:34-37`).

**Fix.** Rewrite `docs/clients.md:158-167`:

```csharp
// The check runs automatically at start-up on every host that calls AddOutboundRouting(...).
// A hand-rolled holder must carry the attribute to be discovered:
[OutboundRoutingContract]
public static class OrdersRouting { public static readonly string[] RequiredTopics = { "order:create" }; }
// Soften or silence with services.AddBenzeneStartUpChecks(BenzeneStartUpCheckMode.Advisory | Disabled).
```

---

### S3. No public test double for `IBenzeneMessageSender`; two identical hand-rolled copies  [duplication x2, +2]

**Grade:** SHOULD-FIX
**§4.1 clause:** "Duplicated plumbing across examples is a framework bug ... the second copy is a
signal, the third is a backlog item."

**Evidence.**
- `examples/Aws/Benzene.Examples.Aws.Tests/Helpers/FakeBenzeneMessageSender.cs:16-29` and
  `examples/Azure/Benzene.Example.Azure.Test/Helpers/FakeBenzeneMessageSender.cs:14-27` — byte-identical
  bodies (the `diff` differs only in namespace, usings and the summary comment).
- `examples/K8sMesh/Service/Domain.cs:119-127` — `NullBenzeneMessageClient : IBenzeneMessageClient`.
- `test/Benzene.Core.Test/Clients/VersionSendExtensionsTest.cs:19` — `CapturingClient : IBenzeneMessageClient`.
- `src/Benzene.Testing/` ships `BenzeneTestHost`, `MessageBuilder`, `HttpBuilder` — nothing for the
  outbound side; `docs/testing-benzene.md` has no outbound section.
- `Mock<IBenzeneMessageSender>` appears in 2 further files.

**What the user experiences.** "How do I unit-test a handler that sends?" has no documented answer;
every team writes the same 12 lines. The in-process transport (`.UseInProcess()`) is the *real-handler*
double, not the capture double, and requires `AddInProcessMessaging` plus a handler pipeline.

**Fix.** Ship the twelve lines in `Benzene.Testing` (public, documented in `testing-benzene.md`):

```csharp
public sealed class RecordingBenzeneMessageSender : IBenzeneMessageSender
{
    public List<(string Topic, object Request, IDictionary<string, string> Headers)> Sent { get; } = new();
    public Func<string, object, object?> Respond { get; set; } = (_, _) => null;
    public Task<IBenzeneResult<TResponse>> SendAsync<TRequest, TResponse>(string topic, TRequest request, IDictionary<string, string>? headers = null)
    { Sent.Add((topic, request!, headers ?? new Dictionary<string, string>())); return BenzeneResult.Accepted((TResponse?)Respond(topic, request!) ?? default!).AsTask(); }
}
// usage: BenzeneTestHost.Create<StartUp>().WithServices(s => s.AddScoped<IBenzeneMessageSender>(_ => recorder))
```

and replace both example copies with it. A `NullBenzeneMessageClient`/`NullBenzeneMessageSender` in
`Benzene.Clients` covers the K8sMesh "no downstream configured" case (P8).

---

### S4. Batch send has only a bottom rung  [ladder-broken]

**Grade:** SHOULD-FIX
**§4.1 clause:** rule 1 (routine capability, shorthand missing) and rule 4 (undocumented).

**Evidence.**
- `src/Benzene.Clients/IBenzeneBatchMessageClient.cs` and six implementations
  (`SqsBatchMessageClient`, `SnsBatchMessageClient`, `EventBridgeBatchMessageClient`,
  `ServiceBusBatchMessageClient`, `EventHubBatchMessageClient`, `EventGridBatchMessageClient`).
- `grep -rn "Batch" src/*/Extensions.cs src/*/DependencyInjectionExtensions.cs` → no `Add*Batch*`,
  no `Use*Batch*`. The only way in is `new SqsBatchMessageClient(queueUrl, amazonSqs, topicAttributeKey,
  cancellation)` (`SqsBatchMessageClient.cs:42-43`).
- `IBenzeneMessageSender` has no batch member (`IBenzeneMessageSender.cs:12-29`), so batch is
  unreachable from the routing path and from generated clients.
- `docs/clients.md`: 0 mentions of "Batch". The capability is documented only in package `CLAUDE.md`s.
- Coverage is uneven: SQS, SNS, EventBridge, Service Bus, Event Hubs, Event Grid have it; Queue Storage,
  Pub/Sub, Kafka, RabbitMQ, HTTP, Lambda, gRPC, in-process do not.

**Fix.** Two rungs: `IBenzeneMessageSender.SendBatchAsync<TRequest>(string topic,
IReadOnlyCollection<TRequest> requests, headers?)` routed to the topic's pipeline (falling back to N
single sends for transports without a native batch, so the call always works and the transport decides
the cost), and per-transport `AddSqsBatchMessageClient(queueUrl)` DI seams for the standalone path.
Document both in `docs/clients.md`.

---

### S5. `benzene spec --file x --type openapi` silently returns the wrong document  [magic]

**Grade:** SHOULD-FIX
**§4.1 clause:** rule 3 — "produce an empty/wrong artifact silently".

**Evidence (trace-only).** `src/Benzene.CodeGen.Cli.Core/Commands/Spec/FileSpecSource.cs:19-27`
`GetSpecJsonAsync(SpecRequest request)` never reads `request.Type`/`request.Format`; it returns the file
verbatim. `SpecCommand.cs:16` passes `new SpecRequest(payload.Type, payload.Format)` with defaults
`benzene`/`json` (`Constants.cs:17,20`). So `benzene spec --file Orders.spec.json --type openapi
--format yaml` prints the `benzene`-type JSON with exit 0.

**Fix.** Either render the loaded `EventServiceDocument` through the same `SpecBuilder` the endpoint uses
for `openapi`/`asyncapi`/`yaml` (the offline OpenAPI artifact is otherwise unobtainable — see the
capability table), or fail: `--file only carries the 'benzene' document; --type openapi needs --url or
--lambda-name`.

---

### S6. CLI flag surface is inconsistent, boolean flags are silently ignored, and help omits what a newcomer needs  [ceremony / magic]

**Grade:** SHOULD-FIX

**Evidence (trace-only).**
- Source flags: `build`/`spec` accept `--file|--url|--mesh|--lambda-name` (`BuildPayload.cs`,
  `SpecPayload.cs`, enforced by `SpecSourceResolver.cs`); `healthcheck` accepts **only**
  `--profile/--lambda-name` (`HealthCheckPayload.cs:8-11`, Lambda-only `HealthCheckClient.cs`);
  `profile-check` accepts **only** `--url` (`CloudServiceProfileCheckPayload.cs:7`). The same idea ("which
  service") has three different flag sets.
- Boolean flags: `AttributesParser.cs:18-21` stores a bare `--warn-only` as `key → null`;
  `Parsing/Extensions.cs:14` then returns the default `""`; `DiffCommand.cs:65` tests
  `payload.WarnOnly == "true"`. So `benzene diff --baseline a --current b --warn-only` **fails on a breaking
  change**, silently ignoring the flag. Same for `--strict` (`HealthCheckCommand.cs:54`) and
  `--no-traceparent-probe` (`CloudServiceProfileCheckCommand.cs:40`). The help text
  (`Constants.cs:50`) says "Equivalent to --fail-on none" and never says a value is required.
- Help: `HelpGenerator.cs:17-25` prints `--{name}` and the description only — no defaults (although
  `ArgAttribute.DefaultValue` exists), no required marker (no such concept on `ArgAttribute`), no
  "exactly one of" group. Every doc and the MSBuild targets use single-dash flags (`docs/client-sdks.md:179,
  214, 267`; `Benzene.CodeGen.Build.targets:95` `-file ... -output ... -service-name`), the help and every
  error message use double-dash (`Constants.cs`, `SpecSourceResolver.cs:28`). The parser accepts both
  (`AttributesParser.CleanKey`), so this is presentation drift, but it is drift a newcomer notices first.
- `HealthCheckCommand` does not validate `--lambda-name`; `AmazonLambdaClientFactory.CreateClient`
  returns `null` for an unknown profile (`:24`) and the resulting failure is wrapped as "is
  `UseHealthCheck()` registered and the function name/profile correct?" (`HealthCheckClient.cs:37`) —
  the hint points at the wrong end.

**Fix.** (1) Give `HealthCheckPayload` the same four sources via `SpecSourceResolver`-style resolution
(HTTP: POST the `benzene:healthcheck` envelope to `/benzene/invoke`, which `HttpBenzeneMessageHealthCheck`
already does). (2) `ArgAttribute { bool IsFlag; bool Required; }`; `PayloadMapper` maps a bare flag to
`"true"`; `CommandBase.ExecuteAsync` validates `Required` and prints `GetHelp()` on failure. (3) Print
`[default: x]` and `(required)` in `HelpGenerator`; pick one dash style and use it in help, errors, docs
and targets.

---

### S7. The producer half of contract artifacts costs four steps; the consumer half costs two  [ceremony]

**Grade:** SHOULD-FIX
**§4.1 clause:** "What a steer should cost: declaration, not wiring."

**Evidence.**
- Consumer (`Benzene.CodeGen.Build`): PackageReference + one `<BenzeneServiceContract>` item
  (`src/Benzene.CodeGen.Build/CLAUDE.md:11` — "Targets-only NuGet ... the whole package"; README `:19-33`).
- Producer (`Benzene.Descriptor`): `dotnet tool install`, then **copy or `<Import>` the `.targets` by
  hand** ("A NuGet tool package does not auto-import its `.targets`" — `src/Benzene.Descriptor/README.md:191-194`,
  `docs/contract-artifacts.md:95-97`), then `<BenzeneEmitDescriptor>true</BenzeneEmitDescriptor>`, then the
  CI upload. `examples/AwsMesh/Payments` imports the targets from source; `examples/Directory.Build.props`
  overrides `BenzeneDescriptorCommand` — the ceremony is visible in the repo's own example.

**Fix.** Ship `Benzene.Descriptor.Build` as a targets-only PackageReference (the exact shape
`Benzene.CodeGen.Build` already has) that imports automatically and defaults `BenzeneDescriptorCommand`
to `dotnet tool run benzene-descriptor`. Before/after for a producer csproj:

```xml
<!-- Before -->
<Import Project="$(NuGetPackageRoot)benzene-descriptor/x.y.z/build/Benzene.Descriptor.targets" />
<PropertyGroup><BenzeneEmitDescriptor>true</BenzeneEmitDescriptor></PropertyGroup>
<!-- plus: dotnet tool install, plus the version-scheme flags -->

<!-- After -->
<PackageReference Include="Benzene.Descriptor.Build" Version="x.y.z" PrivateAssets="all" />
<PropertyGroup><BenzeneEmitDescriptor>true</BenzeneEmitDescriptor></PropertyGroup>
```

---

### S8. The descriptor silently emits `transports: []` for three of four hosts  [magic]

**Grade:** SHOULD-FIX
**§4.1 clause:** rule 3.

**Evidence.** `src/Benzene.Descriptor/HostAdapters.cs:33-40,63-72` — `NeutralHostAdapter.CanHandle`
is `true`; it is selected for any assembly not referencing `Benzene.Aws.Lambda.Core`, and returns
`TransportsResolved: false`. README `:198-200`: "other hosts (self-host worker, ASP.NET, Azure
Functions) fall back to the neutral core (full logical contract, but `spec.json`'s `transports: []`)".
Exit code is 0; nothing on stderr; the artifact carries no marker.

**Misconfiguration that gets through.** An Azure Functions service opts into `BenzeneEmitDescriptor`,
publishes a `spec.json` whose `transports` is empty, and the mesh/codegen consumer cannot tell "this
service has no inbound transports" from "the tool could not see them".

**Fix.** Write `benzene-descriptor: host adapter 'neutral' selected (no adapter for this host yet);
spec.json 'transports' will be empty. Pass --host to force an adapter.` to stderr, and prefer
`"transports": null` (unknown) over `[]` (none) in the neutral case — a wire-shape question for the
spec repo, filed separately.

---

### S9. The outbound routing table has no public read model  [ladder-broken]

**Grade:** SHOULD-FIX
**§4.1 clause:** "Every rung you land on is public, documented API — not an internal type".

**Evidence.** `src/Benzene.Descriptor/OutboundRouteInspector.cs:31-33`:

```csharp
var routes = sender.GetType().GetField("_routes", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(sender)
    as IReadOnlyDictionary<string, IMiddlewarePipeline<OutboundContext>>;
```

and `:50-52` reflecting `_reversedItems` off the pipeline, with the header comment "SPIKE-GRADE: this
reaches into the built outbound routing table by reflection because today's outbound model retains no
introspectable transport/destination read-model." `OutboundRoutingTopics` (`Benzene.Clients/OutboundRoutingTopics.cs`)
exposes topic names only. `DefaultBenzeneMessageSender` is `internal` (`:13`).

**Fix.** `public sealed record OutboundRoute(string Topic, string Transport, string? Destination)` +
`IReadOnlyList<OutboundRoute> OutboundRoutingTopics.Routes`, filled by each `UseX` via
`app.Register(x => x.AddSingleton(new OutboundRouteInfo(...)))` the way `InProcessRouteReference`
already does (`Benzene.Clients.InProcess/Extensions.cs:36-40`). Unlocks S1, deletes the reflection, and
fixes the `in-process`/`inprocess` spelling split noted in `Benzene.Clients.InProcess/CLAUDE.md`.

---

### S10. "What it talks to" is declared twice, in all three cloud examples  [duplication x3]

**Grade:** SHOULD-FIX
**§4.1 clause:** "A service's own code should read as what it handles, what it talks to, and what it
needs — and contain approximately nothing else."

**Evidence.**
- `examples/AzureFunctionsMesh/Orders/StartUp.cs:29-38` — `AddResponseEventDeclarations(new
  ResponseEventDefinition("payment:take", typeof(OutboundTakePayment)), ...)` **and**
  `.Route("payment:take", ...)`: the same topic, twice, kept in sync by hand.
- `examples/GoogleCloudMesh/Orders/Startup.cs:24-31` — identical shape.
- `examples/AwsMesh/Shared/MeshServiceWiring.cs:105-107` and `:129-183` — the example invented an
  `OutboundSend` record (`examples/AwsMesh/Shared/OutboundSend.cs`, 79 lines) precisely to say it once
  and expand it into both calls, plus a `switch (send.Transport)` (`:166-180`).
- Per-route cross-cutting stamping is repeated by hand: `pipeline.UseW3CTraceContext().UseCorrelationId()`
  at `MeshServiceWiring.cs:144` inside a `foreach` — there is no route-defaults hook on
  `OutboundRoutingBuilder` (`Benzene.Clients/OutboundRoutingBuilder.cs:34-40` has `Route` only).

**Fix.** Two small additions to `OutboundRoutingBuilder`:

```csharp
// Before (AzureFunctionsMesh/Orders/StartUp.cs:29-38)
x.AddResponseEventDeclarations(
    (IMessageDefinition)new ResponseEventDefinition("payment:take", typeof(OutboundTakePayment)),
    new ResponseEventDefinition("order:placed", typeof(OutboundOrderPlaced)));
x.AddOutboundRouting(routing => routing
    .Route("payment:take", p => p.UseW3CTraceContext().UseCorrelationId().UseServiceBus(sb => sb.UseServiceBusClient()))
    .Route("order:placed", p => p.UseW3CTraceContext().UseCorrelationId().UseEventHub(eh => eh.UseEventHubClient())));

// After
x.AddOutboundRouting(routing => routing
    .UseDefaults(p => p.UseW3CTraceContext().UseCorrelationId())              // once, for every route
    .Route<OutboundTakePayment>("payment:take", p => p.UseServiceBus(sb => sb.UseServiceBusClient()))   // declares the event
    .Route<OutboundOrderPlaced>("order:placed", p => p.UseEventHub(eh => eh.UseEventHubClient())));
```

`Route<TMessage>` composes `Route` + `AddResponseEventDeclarations` — both public today, so it passes
the "could the user have written it" test.

---

### S11. Boilerplate ledger — outbound plumbing in the examples

Every line classified domain / intent / plumbing; plumbing is either a **missing shorthand** (MS) or a
**deliberate explicit demonstration** (DE, must say so in a comment).

| File | domain | intent | plumbing | The plumbing, and which category |
|---|---|---|---|---|
| `examples/Aws/Benzene.Examples.Aws/DependenciesBuilder.cs:71,94-95` | – | 2 | 1 | `AddSingleton(awsOptions.CreateServiceClient<IAmazonSQS>())` — MS only in the sense of S1 (no check); otherwise clean. |
| `examples/Azure/Benzene.Example.Azure/DependenciesBuilder.cs:55-58,74-75` | – | 2 | 4 | Build `ServiceBusClient`, `CreateSender`, two `AddSingleton` — stated design decision ("Benzene never wraps the connection-string choice", comment `:52-54`) → **DE, and it says so**. Acceptable. |
| `examples/AwsMesh/Shared/MeshServiceWiring.cs:100-184` + `OutboundSend.cs` | – | ~6 | ~85 | Conditional SDK registration (`:112-123`), `OutboundSend` enum/record/switch, per-route defaults in a loop, dual declaration → **MS** (S10, S1). No comment claims DE. |
| `examples/AzureFunctionsMesh/Shared/MeshServiceWiring.cs:101-137` | – | – | 37 | Three lazy-registration helpers `AddServiceBusSender`/`AddEventHubProducer`/`AddEventGridPublisher` → **MS** (pattern: "register the SDK handle from an env var, lazily so start-up survives without it"). Repeated as `AddPubSubPublisher` in `GoogleCloudMesh/Shared/MeshServiceWiring.cs:82-83` → **duplication x4** across two clouds. |
| `examples/AzureFunctionsMesh/Orders/StartUp.cs:29-38`, `GoogleCloudMesh/Orders/Startup.cs:24-31` | – | 4 | 3 | Dual declaration (S10) → **MS**. |
| `examples/Cqrs/Benzene.Example.Cqrs/Program.cs:41-70` | – | 4 | ~22 | Hand-built `MiddlewarePipelineBuilder<BenzeneMessageContext>` + `BenzeneMessageApplication` + `DeliverToReadSideAsync` adapter + two hand-written terminal `.Use(async (context, _) => { ...; context.Response = BenzeneResult.Accepted<Void>(); })` blocks — this **is** `Benzene.Clients.InProcess` (`AddInProcessMessaging` + `.UseInProcess()`), re-implemented in the example of the framework that ships it → **MS/example bug**. No DE comment. |
| `examples/Outbox/Benzene.Example.Outbox/Program.cs:42-50,131-138` | – | 2 | 2×5 | `.UseOutbox().OnRequest(context => { ...; context.Response = BenzeneResult.Accepted<Void>(); })` — a delegate terminal transport, twice; with Cqrs that is **4 copies** of "set `Response = Accepted<Void>` from a lambda" → missing `.UseDelegate(Func<OutboundContext, Task>)` / `.UseInProcess()`; **MS**. |
| `examples/Kafka/Benzene.Examples.Kafka.Producer/Program.cs:29-34` | 2 | 1 | 6 | Hand-builds `MiddlewarePipelineBuilder<KafkaSendMessageContext>` + `new KafkaBenzeneMessageClient(pipeline, NullLogger<>.Instance, serviceContainer.CreateServiceResolverFactory().CreateScope())` when `new KafkaBenzeneMessageClient(producer, logger, resolver)` (`KafkaBenzeneMessageClient.cs:39`) or a route exists → **MS**, or DE without the comment. |
| `examples/K8sMesh/Service/Startup.cs:84-92`, `Domain.cs:119-127` | – | 2 | 12 | `if (downstreamUrl != null) AddHttpBenzeneMessageClient else AddScoped<IBenzeneMessageClient>(NullBenzeneMessageClient)` + the null client → **MS** (P8). |
| `examples/CodeGen/Benzene.Examples.CodeGen.Client/Class1.cs` | 18 | 6 | 0 | Clean. Not built here (no SDK). |
| `examples/CodeGen/Benzene.Examples.CodeGen.Contracts.Consumer/Program.cs` | – | 3 | 1 | `IBenzeneMessageSender sender = null!;` — **DE, says so** (`:4-9`). |
| `examples/Versioning/**` | – | – | – | No outbound usage at all (grep: 0). The producer side of versioning — `SendAsync(..., version:)` — is demonstrated only in `K8sMesh/Service/Domain.cs:61`. |

**Verdict on examples:** the two flagship egress demos (Aws, Azure) are clean; every mesh example and
both pattern examples (Cqrs, Outbox) carry plumbing the framework should own. Not one plumbing block in
the mesh examples is marked as a deliberate explicit demonstration.

---

### S12. NuGet packaging for go-live  [ceremony]

**Grade:** SHOULD-FIX

**Counts (from `src/`):** 178 `.csproj`; 174 packable (`IsPackable` false on 4:
`Benzene.Azure.Function.SourceGenerators`, `Benzene.CodeGen.Cli.Core`, `Benzene.CodeGen.Markdown`,
`Benzene.CodeGen.Terraform`); 4 `PackAsTool`.
- **Description:** 120 of 178 have no `<Description>` of their own; they inherit the one sentence in
  `src/Directory.Build.props:15` ("Benzene is a hexagonal ... framework for C# — write your message
  handlers once and host them behind AWS Lambda, Azure Functions, ASP.NET Core ..."). So
  `Benzene.Clients.Azure.ServiceBus`, `Benzene.Clients.GoogleCloud.PubSub`, `Benzene.Grpc.Client`,
  `Benzene.Testing`, every `*.TestHelpers`, `Benzene.CodeGen.Cli` and `Benzene.Descriptor` all show the
  same hosting sentence on nuget.org. Every package in my territory is on that list except
  `Benzene.CodeGen.Build`.
- **Tags:** 2 of 178 override `PackageTags`; the shared list (`Directory.Build.props:16`) has no
  `client`, `sqs`, `sns`, `servicebus`, `eventhub`, `pubsub`, `grpc`, `codegen`, `cli`, `testing` tag.
- **README:** 6 packages carry their own (`CodeGen.Build`, `Descriptor`, `DataAnnotations`,
  `FluentValidation`, `JsonSchema`, `RateLimiting`); the other 168 pack the repo root README
  (`src/Directory.Build.targets:7-9`), which never mentions clients, codegen or the CLI.
- **Umbrellas:** 2 references-only packages exist — `Benzene.Aws.Lambda` (9 refs) and
  `Benzene.Clients.Aws` (5 refs). None for Azure Functions, Azure clients, Google Cloud, or the testing
  family. And `src/Benzene.Clients.Aws/CLAUDE.md` tells users "**New code** should reference the
  specific transport package(s) it needs, not this meta-package" — the umbrella exists and is
  discouraged in the same breath; AGENTS.md says it exists "to let a consumer take one dependency".
- **`*.TestHelpers`:** 26 shipped; `docs/reference/packages.md:252` says "One exists per transport" and
  lists 10.

**Naming-rule violations** (AGENTS.md "Package naming — family vs platform"), listed:

| Package | Rule broken |
|---|---|
| `Benzene.Grpc.Client` | An outbound client outside the `Benzene.Clients.*` family, and singular `Client` against the family's plural (`Benzene.Clients.Http`). |
| `Benzene.Kafka.Core`, `Benzene.RabbitMq` (contain `KafkaBenzeneMessageClient`/`RabbitMqBenzeneMessageClient` and the `.UseKafka`/`.UseRabbitMq` route rungs) | The "feature-first family" `Benzene.Clients.*` is not where the Kafka/RabbitMQ clients live, so a user cannot find the client by the family name. |
| `Benzene.HealthChecks.DynamoDb` vs `Benzene.HealthChecks.Azure.ServiceBus` | Same family, platform segment present on one and absent on the other. |
| `Benzene.Idempotency.DynamoDb`, `Benzene.Outbox.DynamoDb`, `Benzene.EventSourcing.DynamoDb` vs `Benzene.ClaimCheck.Aws.S3`, `Benzene.Mesh.Aws.S3`, `Benzene.Outbox.EntityFramework` | Feature-first families that drop the `.Aws.` segment for DynamoDB but keep it for S3. |
| `Benzene.Mesh.Usage.CloudWatch`, `Benzene.Mesh.Usage.ApplicationInsights` vs `Benzene.Mesh.Fleet.Aws.XRay` | Same family (`Benzene.Mesh.*`), platform segment absent vs present. |
| `Benzene.Aws.Lambda.XRay` | Platform-first for a cross-cutting tracing feature; the sibling `Benzene.OpenTelemetry` is feature-first. |

**Fix.** A one-line `<Description>` per package (start with the 16 client/codegen/testing packages);
family tags; `Benzene.Clients.Azure` and `Benzene.Azure.Function` umbrellas or an explicit statement
that umbrellas are AWS-only; decide the DynamoDB rule and rename before 1.0 (renames are free now and
never again).

---

### S13. `Convert<TContext, TContextOut>` is a public extension in six packages  [ladder-broken]

**Grade:** SHOULD-FIX (trace-only; cannot compile here)
**§4.1 clause:** the documented "drop one level" rung (`docs/clients.md:250, 263, 278` — "below that is
`.Convert(new OutboundXContextConverter(...), configure)`") must be reachable without an ambiguity error.

**Evidence.** Identical public signatures `Convert<TContext, TContextOut>(this
IMiddlewarePipelineBuilder<TContext>, IContextConverter<TContext, TContextOut>,
Action<IMiddlewarePipelineBuilder<TContextOut>>)` at `Benzene.Core.Middleware/Extensions.cs:431-493`
(canonical) **and** `Benzene.Clients.Aws.Sns/Extensions.cs:52`, `Benzene.Clients.Aws.EventBridge/Extensions.cs:33`,
`Benzene.Clients.Http/Extensions.cs:93,104`, `Benzene.Kafka.Core/Kafka/Extensions.cs:39`,
`Benzene.Grpc.Client/Extensions.cs:40`. SQS, Service Bus, Event Hubs, Event Grid, Queue Storage, Pub/Sub,
in-process, RabbitMQ do **not** redefine it and use the core one. A file importing
`Benzene.Core.Middleware` and any two of the redefining namespaces (e.g.
`examples/AwsMesh/Shared/MeshServiceWiring.cs` imports `Benzene.Clients.Aws.Sns`,
`Benzene.Clients.Aws.EventBridge` and `Benzene.Core.Middleware` at `:16-17,25`) and calling
`app.Convert(...)` would, by C# overload resolution for extension methods in equally-scoped namespaces,
be CS0121 ambiguous. That file happens not to call `.Convert`, so nothing catches it.

**Fix.** Delete the five copies; keep `Benzene.Core.Middleware`.

---

### S14. Rung-parameter parity gaps that change behaviour  [ceremony / magic]

**Grade:** SHOULD-FIX (each small; together they are the parity table's asymmetries)

- `Benzene.Clients.Azure.EventHub/Extensions.cs:115` — the `OutboundContext` `UseEventHub(producerClient,
  topicPropertyKey, healthCheck)` has **no `partitionKeyHeader`**, while `UseEventHub<T>(producerClient, ...,
  partitionKeyHeader)` (`:76`) and the `action` overload (`:93-96`) do. The package `CLAUDE.md` says without
  it "the per-partition ordering the consumer side advertises is unreachable end-to-end". The shorthand rung
  silently loses ordering; the user must drop a rung to get it back.
- `Benzene.Clients.Aws.Sns/Extensions.cs:64-68,79-81` — `UseSns<T>(..., string queueUrl, ...)`: the SNS
  topic ARN is named `queueUrl` in the signature and the `<param>` doc. IntelliSense lies.
- `Benzene.Clients.GoogleCloud.PubSub/Extensions.cs:23` — `new PubSubClientMiddleware(publisher)`: the only
  transport middleware that does not take `ICancellationTokenAccessor` (every sibling does, e.g.
  `Sqs/Extensions.cs:28`), so `.UseTimeout(...)` around a Pub/Sub route does not reach the SDK call — the
  exact bug the other transports fixed under #268.
- Standalone DI seams (`Add*MessageClient`) exist for SQS, Service Bus, Event Hubs, Event Grid, Queue
  Storage, HTTP, gRPC, Step Functions and **not** for SNS, EventBridge, Lambda, Pub/Sub, Kafka, RabbitMQ
  (grep in S4's evidence). SQS's seam requires the `action` argument (`Sqs/Extensions.cs:132`) — no
  `AddSqsMessageClient(queueUrl)` shorthand.
- `UseEventGridEventSchema` has only the `action` overload (`EventGrid/Extensions.cs:111-128`) — no
  instance shorthand, unlike its CloudEvents sibling.
- `Benzene.Clients.Aws.EventBridge/Extensions.cs:19-44` — three public methods with no XML doc; every
  sibling package documents them.

---

### S15. Two docs disagree on which hash the contract-drift check compares  [invisible-ladder]

**Grade:** SHOULD-FIX

**Evidence.** `docs/cookbooks/contract-testing.md:47` — "Both ends hash with the same
`CodeGenHelpers.GenerateHash`, so the hashes are directly comparable." `docs/client-sdks.md:68-73` —
"It's computed by `Benzene.CodeGen.Core.ContractHash` ... This is a change from the hash values Benzene
generated before ... (a non-portable, .NET-serializer-specific HMAC-SHA256 hash)". Code:
`src/Benzene.CodeGen.Client/MessageClientSdkBuilder.cs:198` `ContractHash.Compute(eventServiceDocument, ...)`.
The cookbook is stale. Also `contract-testing.md:27` `app.UseHealthCheck("get", "healthcheck", health => ...)`
— I could not locate that `(string, string, Action)` overload in `src/Benzene.HealthChecks` (trace-only,
unverified; may live in `Benzene.Http`).

---

### P1. `Benzene.CodeGen.Build` default `ServiceName` is probably `OrdersSpec`, not `Orders`  [magic, trace-only]

`Benzene.CodeGen.Build.targets:52` defaults `ServiceName` to `%(Filename)`; MSBuild's well-known
`Filename` for `orders.spec.json` is `orders.spec`. It is passed as `-service-name "orders.spec"`
(`:95`), and `ServiceNameFormatter.ToPascalCase` splits on `.` (`ServiceNameFormatter.cs:13-20`) →
`OrdersSpec`. The targets comment (`:19-20`, "`orders.spec.json` -> `Orders`") and README (`:40`, "the
file's own stem") say otherwise. The repo example sets `ServiceName` explicitly, so it is never exercised.
Fix: strip a trailing `.spec` in the target (or reuse `ServiceNameResolver.FileStem`).

### P2. `benzene-descriptor` unknown-flag failure does not name the flag

`EmitOptions.cs:84` `default: return null;` and `:91` (bad `--emit` value) both collapse to the generic
`Usage` line (`Program.cs:12-16`). Print `unknown option '--foo'` / `--emit must be spec|descriptor|both`.

### P3. `HealthCheckCommand` wraps a null client in the wrong hint

`AmazonLambdaClientFactory.cs:24` returns `null` for an unknown `--profile`; the eventual NRE is reported
as "is `UseHealthCheck()` registered and the function name/profile correct?" (`HealthCheckClient.cs:37`).
Throw `Profile '{name}' not found in the AWS credential store` at the factory.

### P4. Three places link to a `docs/cli.md` that does not exist

`docs/index.md:80` (admits it), `docs/contract-artifacts.md:199`, `src/Benzene.CodeGen.Build/build/Benzene.CodeGen.Build.targets:31`
("see docs/cli.md"), `src/Benzene.CodeGen.Build/README.md:74`. Verified missing. Either write it (the
flag table is already in `Constants.cs`; `HelpGenerator` could emit it) or remove the links before go-live.

### P5. Empty public type shipped "for later"

`src/Benzene.Clients/IBenzeneMessageClient.cs:13-16` — `public static class BenzeneMessageClientExtensions
{ }` "currently empty". Public surface is forever; delete it.

### P6. Generated code nits (trace-only)

`MessageClientSdkBuilder.cs:239` emits `using Benzene.Results;` into the interface file, where nothing
from that namespace is referenced (warning-level in a consumer with warnings-as-errors). The generated
`string HashCode` property (`:199`) reads as a typo for `GetHashCode()` to a stranger; `ContractHash`
would name what it is and match `Benzene.CodeGen.Core.ContractHash`.

### P7. The versioned-send shorthand is demonstrated once, outside the Versioning example

`ClientExtensions.cs:38-43, 67-72` add `version:` overloads; `examples/Versioning` never sends
(grep: 0); the only use is `examples/K8sMesh/Service/Domain.cs:61`. `docs/clients.md` does not mention
the overload (B4 row 6).

### P8. `NullBenzeneMessageClient` belongs in the framework

`examples/K8sMesh/Service/Domain.cs:119-127`. A "no downstream configured" no-op is a legitimate
production shape (the example says so); ship `Benzene.Clients.NullBenzeneMessageSender`/`Client` next to
S3's recorder.

### P9. `docs/DOCUMENTATION_QUICK_REFERENCE.md:52-54`

The package table lists `Benzene.Clients.Aws` under AWS and no client package under Azure, Google or
messaging — the contributor-facing cheat sheet does not know the client family exists outside AWS.

### P10. `in-process` vs `inprocess`

`Benzene.Clients.InProcess/CLAUDE.md` documents that `TransportNames.InProcess` is `"in-process"` while
`OutboundRouteInspector.ToTransportName` yields `"inprocess"` (and `"servicebus"`/`"eventhub"` against
`service-bus`/`event-hub`). Moot once S9 replaces the reflection with declared names.

---

## Client-family parity table

"Route rung" = an `IMiddlewarePipelineBuilder<OutboundContext>.UseX(...)` overload usable inside
`AddOutboundRouting(...).Route(topic, ...)`. Line counts are for the routing path: `R` = lines to
register (SDK handle + route; the shared `AddOutboundRouting(...)` wrapper not counted), `S` = lines to
send one message, `B` = lines to send a batch (standalone path; "–" = no batch client). "Doc" =
`docs/clients.md` has a per-transport section.

| Transport | Package | Route rung | SDK handle on the shorthand rung | Standalone `IBenzeneMessageClient` DI seam | Health check | Batch | Typed response | R / S / B | Doc |
|---|---|---|---|---|---|---|---|---|---|
| SQS | `Benzene.Clients.Aws.Sqs` | `UseSqs(url, topicAttr?, healthCheck=true)` + action | DI (`IAmazonSQS`) | `AddSqsMessageClient(url, action)` — action required | auto on route (dependency) | `new SqsBatchMessageClient(url, sqs)`; no DI | Void only | 2 / 1 / 3 | yes |
| SNS | `Benzene.Clients.Aws.Sns` | `UseSns(arn, topicAttr?, publishOptions?, healthCheck=true)` + action | DI | **none** (ctor only) | auto on route | `SnsBatchMessageClient`; no DI | Void only | 2 / 1 / 3 | yes |
| EventBridge | `Benzene.Clients.Aws.EventBridge` | `UseEventBridge(source, busName?, healthCheck=true)` + action | DI | **none** | auto on route | `EventBridgeBatchMessageClient`; no DI | Void only | 2 / 1 / 3 | listed as "not implemented" (`:282`) |
| Lambda | `Benzene.Clients.Aws.Lambda` | **none** (B1) | DI (`IAmazonLambda`) via `UseAwsLambda<T>` on `IBenzeneClientContext<T,Void>` | **none**; doc shows hand `AddScoped` (`:344`) | explicit only (`AddLambdaHealthCheck`) | – | yes (standalone class only) | 2 / 1 / – (standalone) | yes, as manual |
| Step Functions | `Benzene.Clients.Aws.StepFunctions` | **none**; not an `IBenzeneMessageClient` at all (`IStepFunctionsClient.StartExecutionAsync`) | DI | `AddStepFunctionsClient(arn, healthCheck=true)` | auto on DI seam | – | Accepted/empty | 1 / 1 / – | **no** |
| Service Bus | `Benzene.Clients.Azure.ServiceBus` | `UseServiceBus(sender, topicProp?)` + action | **instance** (`ServiceBusSender`) | `AddServiceBusMessageClient(action, topicProp?)` | **none in package** (separate `Benzene.HealthChecks.Azure.ServiceBus`) | `ServiceBusBatchMessageClient`; no DI | Void only | 4 / 1 / 3 | yes |
| Event Hubs | `Benzene.Clients.Azure.EventHub` | `UseEventHub(producer, topicProp?, healthCheck=true)` + action — **no `partitionKeyHeader`** on this rung | instance | `AddEventHubMessageClient(action, topicProp?)` | auto on route | `EventHubBatchMessageClient`; no DI | Void only | 3 / 1 / 3 | yes |
| Event Grid | `Benzene.Clients.Azure.EventGrid` | `UseEventGrid(source, publisher)` + action; `UseEventGridEventSchema(action)` only | instance | `AddEventGridMessageClient(source, action)` | **none** (deliberate, documented) | `EventGridBatchMessageClient`; no DI | Void only | 3 / 1 / 3 | yes |
| Queue Storage | `Benzene.Clients.Azure.QueueStorage` | `UseQueueStorage(queueClient, healthCheck=true)` + action | instance | `AddQueueStorageMessageClient(action)` | auto on route | – | Void only | 3 / 1 / – | yes |
| Pub/Sub | `Benzene.Clients.GoogleCloud.PubSub` | `UsePubSub(topic, topicAttr?)` + action | DI (`PublisherServiceApiClient`) | **none**; no `IBenzeneMessageClient` at all | **none** ("not built") | – | Void only | 2 / 1 / – | **no** |
| Kafka | `Benzene.Kafka.Core` | `UseKafka(keyHeader?)` + action | DI (`IProducer<string,string>`) | **none** (ctor `KafkaBenzeneMessageClient(producer, logger, resolver)`) | separate `AddKafkaDependencyHealthCheck(config)`; not on route | – | Void only | 2 / 1 / – | yes |
| RabbitMQ | `Benzene.RabbitMq` | `UseRabbitMq(channel, exchange?, topicHeader?)` + action | instance (`IChannel`) | **none** (ctor) | separate `AddRabbitMqDependencyHealthCheck`; not on route | – | Void only | 3 / 1 / – | yes |
| HTTP (envelope) | `Benzene.Clients.Http` | `UseBenzeneMessageOverHttp(url, healthCheck=true)` + action | DI (`HttpClient`) | `AddHttpBenzeneMessageClient(url, healthCheck=true)` | auto on both seams | – | **yes** | 2 / 1 / – | yes; only transport whose xml-doc names the rung below (`Http/Extensions.cs:145-149,172-178`) |
| In-process | `Benzene.Clients.InProcess` | `UseInProcess(name?)`, `UseInProcessFanOut(...)` | n/a (`AddInProcessMessaging` required; start-up check) | n/a | n/a | – | yes | 2 / 1 / – | one sentence (`:276`), no section |
| gRPC | `Benzene.Grpc.Client` | **none** (B1) | DI (`GrpcChannel`) | `AddGrpcClient(routes, healthCheck=true)` | auto on DI seam | – | yes (standalone class only) | 3 / 1 / – (standalone) | yes, as manual |

**Test double for code that sends:** none in public API for any transport (S3).

**Design decisions that explain an asymmetry and should be stated once, in `docs/clients.md`:**
- AWS/GCP/Kafka/HTTP/gRPC shorthands resolve the SDK handle from DI; Azure/RabbitMQ shorthands take an
  instance. Both families expose the other rung via the `action` overload, so the ladder is intact; the
  *default* differs per cloud and nothing says why (the Azure `CLAUDE.md`s say "never wrap the
  connection-string choice", but the AWS packages do not wrap it either).
- Step Functions is deliberately not a message client (its `CLAUDE.md` says so, honestly); the public doc
  does not say so at all.
- Event Grid has no health check because the data plane has no read (documented in `CLAUDE.md` only).

Asymmetries with **no** stated reason: SNS/EventBridge/Lambda/Pub/Sub/Kafka/RabbitMQ lacking a standalone
DI seam; Kafka/RabbitMQ health not auto-wired on the route while SQS/SNS/EventBridge/Event Hubs/Queue
Storage/HTTP are; Event Hubs' missing `partitionKeyHeader` on the route rung; Pub/Sub's missing
cancellation accessor.

---

## Capability → explicit form → shorthand → documented?

| Capability | Explicit form (all public?) | Shorthand | Doc names the rung below? |
|---|---|---|---|
| Send to a topic over transport X | `MiddlewarePipelineBuilder<OutboundContext>` + `.Convert(new OutboundXContextConverter(...), b => b.UseXClient(handle))` — public (S13 ambiguity aside) | `.Route(topic, p => p.UseX(...))` | Kafka/RabbitMQ/HTTP: yes (`clients.md:250,263,278`). SQS/SNS/Azure/Pub/Sub: the `action` overload is named; `.Convert` is not. |
| Cross-cutting on a send (retry, correlation, trace, outbox, claim check) | `IMiddleware<OutboundContext>` | `.UseRetry/.UseCorrelationId/.UseW3CTraceContext/.UseOutbox/.UseClaimCheck` | retry/correlation/trace: yes. outbox/claim-check on a route: cookbooks only. Route-wide defaults: **no rung** (S10). |
| Fan one message out to several transports | two routes + `Task.WhenAll` | `.UseParallel((name, cfg), ...)` | **not in `clients.md`** (xml doc only). |
| Batch send | `new XBatchMessageClient(...)` | **none** (S4) | **no**. |
| Typed request/response to a Lambda / gRPC service | `AwsLambdaBenzeneMessageClient` / `GrpcBenzeneMessageClient` ctor | **none on the routing path** (B1) | documented as the manual path. |
| Versioned send | headers dict with `benzene-version` | `SendAsync(topic, req, version: "1")` | **no** (P7). |
| Typed client for a service | hand-written class over `IBenzeneMessageSender` | `benzene build -output client|topic-client` / `<BenzeneServiceContract>` | yes (`client-sdks.md`); generated body is 1:1 with what a user would write (`MessageClientSdkBuilder.cs:97-102, 217-225`). |
| Register a generated client | `AddScoped<I,C>` | generated `Add{Service}ServiceClient()` / `Add{Service}Clients()` | yes, with the lifetime reasoning in the generated file (`:183-185`). |
| Validate every required route at start-up | `resolver.ValidateOutboundRouting()` | auto `IStartUpCheck` from `AddOutboundRouting` | **documented wrongly** (S2). |
| Verify the SDK handle at start-up | — | — | **no rung at all** (S1). |
| Dependency health for a route target | `AddSqsHealthCheck(url)` etc. on `IHealthCheckBuilder` | `healthCheck: true` default on the route rung | xml docs + `CLAUDE.md`; `clients.md` mentions it for HTTP only (`:276`). |
| Downstream contract drift | implement `IHasHealthCheck` + `AddContractCheck<T>` | `AddServiceCheck(name, client.HashCode)` | yes (cookbook), with a stale hash-algorithm sentence (S15). |
| Emit `spec.json`/`service.json` in CI | `benzene-descriptor --assembly ...` | `<BenzeneEmitDescriptor>true</BenzeneEmitDescriptor>` after a manual `<Import>` (S7) | yes, but both doc snippets exit 2 (B3) and the neutral host is silent (S8). |
| OpenAPI/AsyncAPI document offline | — (`--file` ignores `--type`, S5) | `benzene spec --url ... --type openapi` (needs a running service) | partially; no offline route. |
| Compatibility gate | `SchemaCompatibility.EnsureBackwardCompatible(...)` in a test | `benzene diff --baseline --current --fail-on` | yes. `--warn-only` silently ignored without a value (S6). |
| Generate handler stubs from a contract | `new MessageHandlerBuilder(ns).BuildCodeFiles(doc)` | `benzene build -output message-handlers` | yes. |
| Test a handler in isolation | `new Handler(deps).HandleAsync(msg)` | — (none needed) | `testing-benzene.md` does not say so; fine. |
| Test a pipeline in-memory | `MiddlewarePipelineBuilder<BenzeneMessageContext>` + `BenzeneMessageApplication` (Cqrs shows it: 5 lines) | `BenzeneTestHost.Create<StartUp>().BuildAwsLambdaHost()` + `SendBenzeneMessageAsync(MessageBuilder...)` | yes. |
| Test a hosted transport | build the real host | AWS: `SendSqsAsync(builder.AsSqs())`; Azure: `app.HandleEventHub(builder.AsEventHubBenzeneMessage())` — **`Send*` vs `Handle*`** for the same capability; Azure Function TestHelpers ship only `MessageBuilderExtensions`, AWS ship `BenzeneTestHostExtensions` too | yes (`testing-benzene.md:52-86`); the verb split is not explained. 16 of 26 TestHelpers packages undocumented. |
| Test code that sends | Moq / hand-rolled | **none** (S3) | **no**. |
| Route to a handler in the same process | hand-built `BenzeneMessageApplication` (what Cqrs does) | `AddInProcessMessaging(...)` + `.UseInProcess()` | packages.md + `CLAUDE.md`; `clients.md` has no section. |

---

## What is genuinely good

- **The outbound-routing core is the §4.1 ladder done right.** `IBenzeneMessageSender.SendAsync(topic,
  request, headers)` is exactly "what it talks to, and nothing else"; `.Route(topic, p => p.UseSqs(url))`
  is one line; the `action` overload is one rung down; `.Convert(converter, action)` is one rung below
  that; `UseSqsClient(instance)` is the floor. I wrote `UseSqs(url)` myself from public API
  (`app.Register(x => x.AddDependencyHealthCheck(r => new SqsHealthCheck(url, r.GetService<IAmazonSQS>(),
  HealthCheckMode.Reachability, "topic"), $"Sqs:{url}")); app.Convert(new OutboundSqsContextConverter(url),
  b => b.UseSqsClient());`) and it is the implementation (`Sqs/Extensions.cs:114-121, 166-172`). That
  passes all three of rule 2's tests. Eleven transports share the shape.
- **`Benzene.Clients.Http`'s XML docs are the model for the whole family** — each shorthand's `<remarks>`
  says literally what it composes ("it is exactly `app.UseBenzeneMessageOverHttp(url, builder =>
  builder.UseHttpClient())` plus the health-check registration. Drop one level to that ...").
  `Kafka.Core` and `RabbitMq` follow it. Copy that remark block onto every `UseX`.
- **The generated client is composition, not magic.** One constructor taking `IBenzeneMessageSender`,
  one `return _sender.SendAsync<TReq, TRes>("topic", message, headers);` per method, the DI registration
  in its own file with the lifetime reasoning in a comment shipped to the user
  (`MessageClientSdkBuilder.cs:183-185`). A user can drop one level (`IBenzeneMessageSender`) and keep
  going with the same envelope. `topic-client` mode scopes the contract hash and `RequiredTopics` to what
  the consumer actually calls — a real ergonomics win for coupling.
- **Codegen failures name what they looked for.** `--topics` with an unknown topic lists the valid ones
  (`TopicScope.cs:42-44`); `--file` missing names the path (`FileSpecSource.cs:23`); `--output` unknown
  lists the valid values (`CodeBuilderFactory.cs:66-67`); two spec sources at once lists both
  (`SpecSourceResolver.cs:32-37`); `benzene-descriptor` names the ambiguous `StartUp` types and the
  `--startup` flag to disambiguate (`DescriptorEmitter.cs:148-150`), and fails loudly on a Benzene version
  skew with both versions (`:118-121`).
- **`Benzene.CodeGen.Build` is what a targets-only shorthand should look like:** one item, incremental,
  a broken contract fails the build with the CLI's own message, and the `CLAUDE.md` records the two MSBuild
  traps it avoids so the next author does not re-learn them.
- **The start-up-check machinery exists and is wired into every host** (`BenzeneStartUpCheckExtensions.cs`,
  called from AspNet, Lambda, Azure Functions, HostedService, and `BenzeneTestHost`) with a one-line kill
  switch. S1/S2 are about *using* it for the outbound side, not building it.
- **Reserved topics are kept out of generated clients** (`client-sdks.md:229-263`) — the health check that
  used to force an outbound route on every consumer is gone, and the reasoning is written down where a
  stranger will find it.
- **Honest scoping is written down where it matters** (Step Functions "fire-and-forget for 1.0", Event
  Grid "no health check, and why", Queue Storage's Base64 trap, `in-process`'s "no per-topic handler
  validation") — in the package `CLAUDE.md`s. The remaining job is moving those sentences into the public
  doc a user reads.
