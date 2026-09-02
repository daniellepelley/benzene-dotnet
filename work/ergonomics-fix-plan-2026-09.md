# Ergonomics fix plan — go-live (2026-09)

**Status: READY FOR EXECUTION — not yet started.** Covers the 133 findings of the 2026-09 ergonomics
review (14 blockers, 75 should-fix, 44 polish), graded against `design-principles.md` §4.1 "The
shorthand ladder". Task board **#318–#450**, continuing from the round-18 bug-fix plan (#292–#317).

Source docs, all at `work/`:

- `ergonomics-review-summary-2026-09.md` — the go-live verdict and the ranked blocker list (E1–E14)
- `ergonomics-review-first-service-2026-09.md` (F1–F17)
- `ergonomics-review-aws-2026-09.md` (findings 1–23)
- `ergonomics-review-azure-google-2026-09.md` (F1–F19)
- `ergonomics-review-selfhost-middleware-2026-09.md` (findings 1–27)
- `ergonomics-review-clients-codegen-cli-2026-09.md` (B1–B4, S1–S15, P1–P10)
- `ergonomics-review-mesh-2026-09.md` (B1–B3, S1–S10, P1–P5)

Every finding cites file:line and most carry a before/after snippet. **This plan does not restate
them** — each WP names the findings it closes and the doc to read.

**Context the fixer must internalize before touching anything:**

1. **Nothing here was executed.** The review sandbox had no .NET SDK, so every claim is a source
   trace. The first step in every WP is to reproduce the finding in a dotnet-capable environment.
   If a finding does not reproduce, record `[NOT A BUG]` with the evidence and move on — do not
   "fix" a trace error.
2. **This is ergonomics, not correctness.** Round 18's bug-fix plan (#292–#317) is a *separate*
   round against the same tree. WP-A here and WP-B there both touch `Benzene.Cache`/`RateLimiting`
   territory only incidentally; the real overlap is listed under Coordination notes. **Run round 18
   first if both are in flight** — it fixes a security bug (#292) and this plan renames nothing it
   depends on.
3. **Public API is changing.** WP-C, WP-E, WP-F, WP-G add public types and extension methods, and
   WP-F changes CLI exit codes. All are additive except the CLI's default-path behaviour. Nothing
   here is a wire/spec change (three spec-level questions are filed at the end, for the `benzene`
   repo).
4. **The counts are the argument.** For every duplication finding, the definition of done includes
   *deleting the copies* in the same commit. A shorthand that ships without its copies being removed
   has not closed the finding — the next reviewer will count them again.

## Phase split

**Phase 1 (#318–#356) blocks go-live.** All 14 blockers plus the should-fix items that are the same
defect wearing a different grade. Estimated one focused fix round; most are one-line code changes
plus doc edits.

**Phase 2 (#357–#450) is the launch backlog.** Real ergonomics debt, none of it a reason to hold the
release. Sequenced so the highest-count duplication goes first.

## Task board mapping

| WP | Phase | Tasks | Area | Findings closed |
|----|-------|-------|------|-----------------|
| A | 1 | #318–#324 | Start-up checks reach every entry point | E1–E5; first-service F1, F2, F7, F8; azure-google F2, F3; selfhost 4; aws 2 |
| B | 1 | #325–#330 | Broken rungs: Lambda test host, Azure Timer | E6, E7; aws 1; azure-google F1, F5 |
| C | 1 | #331–#334 | Outbound routing rung for Lambda + gRPC | E8; clients B1, S4 (bottom rung), S9 |
| D | 1 | #335–#337 | CLI front door | E12; clients B2, S5, S6, P2, P3 |
| E | 1 | #338–#344 | Mesh: dispatch path, collector sink, public announce | E9, E10, E11; mesh B1, B2, B3, S6 |
| F | 1 | #345–#356 | Doc honesty sweep | E13, E14; selfhost 1, 2, 16; clients B3, B4, S15; first-service F9, F10; aws 4, 12, 13; azure-google F11 |
| G | 2 | #357–#372 | Shorthands by duplication count | first-service F3–F6; aws 5, 6, 7, 8, 14; azure-google F6, F7, F14, F15; selfhost 13, 17; clients S3, S10, P8; mesh S2, S3 |
| H | 2 | #373–#384 | Conventions that need a check | first-service F7 (follow-on); selfhost 5, 6, 7; azure-google F4, F9; clients S1, S2; mesh S4, S5; aws 9, 18 |
| I | 2 | #385–#396 | Transport + worker parity | selfhost 3, 8, 9, 10; aws 3, 11, 15; azure-google F8, F12, F13; clients S14 |
| J | 2 | #397–#404 | Client family + contract surface | clients S7, S8, S11, S13; aws 10; first-service F11 |
| K | 2 | #405–#412 | Mesh config parity + the host as a Cloud Service | mesh S1, S7, S8, S9, S10 |
| L | 2 | #413–#420 | NuGet packaging for go-live | clients S12; naming-rule violations; TestHelpers docs |
| M | 2 | #421–#450 | Polish sweep | all 44 POLISH findings, by review-doc ID |

## Execution protocol (standard — same as rounds 11–18, two additions)

1. **One isolated git worktree per work package**, all detached from the same base commit on `main`
   (record it at kickoff). `git worktree add --detach <path> <commit>`. **Never `git stash`.**
2. **Reproduce first.** For a code finding, write the failing test. For a doc finding, run the
   snippet or the command and capture the output. For a duplication finding, run the grep and record
   the count — that count is the acceptance criterion.
3. **Scoped builds only** — build/test the specific test project, never the whole solution, while
   other WPs run in parallel (the host OOM-kills concurrent full-solution builds; verified every
   round). The coordinator runs ONE centralized baseline after the last merge: full `Benzene.sln`
   build, `Benzene.Core.Test`, `Benzene.Grpc.Test`, `Benzene.Mesh.Test`, `Benzene.Mesh.Host.Test`,
   `Benzene.Conformance.Test`, `Benzene.Examples.sln` build, `templates/Benzene.Templates.sln` build.
4. **Subagents cannot receive background-task notifications.** Run every build/test as a single plain
   foreground Bash call; never use run_in_background or Monitor-style polling.
5. **New — the examples and templates are part of the deliverable.** §4.1 holds example code to a
   stricter rule than production code. A WP that adds a shorthand and leaves the hand-rolled copies
   in place is not done. Every WP below states its copy-count acceptance criterion.
6. **New — every doc snippet a newcomer would paste gets a `<!-- compile: … -->` marker.**
   `test/Benzene.Core.Test/Docs/DocSnippetsCompileTest.cs` already compiles marked snippets in the
   normal test job; the review found the broken ones are precisely the unmarked ones. Adding the
   marker is how a doc fix is verified, not a separate task.
7. **Definition of done per WP**: fix + regression tests green + copy counts at zero + dated entries
   appended to `work/outstanding-bugs.md` (immediately before `## Open — maintainer decisions`) +
   `docs/capability-matrix.md` rows updated + the package's own `CLAUDE.md` updated where the ruling
   says so. Commit citing the task numbers and the review-doc finding IDs.
8. **Coordinator merges sequentially** (WP-A first), hand-reconciles `capability-matrix.md` and
   `outstanding-bugs.md`, runs the centralized baseline, then pushes to `main` AND
   `claude/benzy-dotnet-publicity-plan-ujib55`.

---

# PHASE 1 — go-live blocking

## WP-A — Start-up checks reach every entry point (#318–#324)

**Why first.** The port's headline ergonomic promise is that a mis-wired service fails before the
first message, and the review found five documented entry points where it does not. Every other WP
benefits from the checks actually running.

**Files.** `src/Benzene.AspNet.Core/BenzeneExtensions.cs` (the `Action` overload, ~106-111; the
`StartUp` overload's runner at ~178), `src/Benzene.AspNet.Core/AspApplicationBuilder.cs` (~73-85),
`src/Benzene.GoogleCloud.Functions.Http/GoogleCloudFunctionHost.cs:37`,
`src/Benzene.GoogleCloud.Functions.PubSub/GooglePubSubFunctionHost.cs:35`,
`src/Benzene.SelfHost/InlineSelfHostedStartUp.cs:29-45` (+ `BuildHostedService`),
`src/Benzene.Azure.Function.Core/HostBuilderExtensions.cs:36-44`,
`src/Benzene.Core.MessageHandlers/StartUpChecks/` (new check beside the existing four),
`src/Benzene.Core.MessageHandlers/DI/Extensions.cs:222-229` (registration),
`src/Benzene.Grpc.AspNet/` (`BuildGrpcHost`), `src/Benzene.Http/Routing/HttpRouteStartUpCheck.cs`
(reference for the gRPC equivalent).

**Rulings:**

1. **(#318, E1)** `IApplicationBuilder.UseBenzene(Action<IAspApplicationBuilder>)` runs
   `RunStartUpChecks()` after the configuration action, exactly as the `StartUp` overload does at
   `:178`. Also add the pre-`Build()` inline overload the review proposes
   (`WebApplicationBuilder.UseBenzene(Action)`), so the action-shaped path can register into the
   host's own `IServiceCollection` instead of the cloned provider `AspApplicationBuilder.cs:73-85`
   opens. Re-point `docs/getting-started-aspnet.md` §4, the `benzene.asp` template and
   `examples/Grpc` at whichever shape survives. Read that overload's XML doc first — it already
   explains the two-provider hazard for the sibling; the same words apply here.
2. **(#319, E2)** `GoogleCloudFunctionHost` and `GooglePubSubFunctionHost` call
   `.WithStartUpChecks()` on the resolver factory they build, matching every other host. Their
   TestHelpers already do, and claim parity — that claim becomes true. Add a test that constructs
   `GooglePubSubFunctionHost<>` at all (the review found none exists).
3. **(#320, E3)** New `HandlerResolutionStartUpCheck` in
   `src/Benzene.Core.MessageHandlers/StartUpChecks/`, registered in `RegisterHandlerFinderInfrastructure`
   beside `DuplicateTopicStartUpCheck` / `EmptyHandlerRegistryStartUpCheck` /
   `PipelineResolutionStartUpCheck` / `TerminalMiddlewareStartUpCheck`. It resolves (constructs, never
   dispatches) every discovered handler type in the runner's throwaway scope and aggregates failures
   into one message naming topic → handler type → innermost exception message. Code sketch in
   first-service F2. Handlers are already registered scoped as concrete types, so this needs no new
   registration. Wire it to the existing Advisory/Disabled knob for the rare deliberate case.
   **Do not** flip `ValidateOnBuild` globally — its opt-in status is a measured decision recorded in
   `MicrosoftServiceResolverFactory.cs:21-47`.
4. **(#321, E4)** `InlineSelfHostedStartUp.Build()` and `BuildHostedService` run the checks. This is
   the rung `docs/getting-started-worker.md` promotes for unit-testing the wiring.
5. **(#322, E5)** Azure Functions runs its checks once at host start, not inside the scoped
   `IAzureFunctionApp` factory. The review notes an `IHostedService` is the INIT hook the current
   comment says does not exist — use it, and delete the comment.
6. **(#323, first-service F7)** A platform `Use*` that mounts nothing is a start-up failure, not a
   silent forever-idle process. The convention is "the wrong platform's `Use*` is a no-op"; the check
   is "at least one transport actually mounted".
7. **(#324, first-service F8)** gRPC compiles its route table on the first RPC while
   `docs/getting-started-grpc.md` claims start-up. Force compilation in a start-up check, mirroring
   `HttpRouteStartUpCheck` (which exists for exactly this reason on the HTTP side).

**Red-green recipe (one shape, applied to every host).** For each of ASP.NET (both overloads),
Google HTTP, Google Pub/Sub, Azure Functions, `InlineSelfHostedStartUp`, `BuildHostedService`, AWS
Lambda (already green — the control), and every `Build*Host` test helper: a `StartUp` whose handler
takes an `IGreeter` that is **not** registered must fail at build/init with a message naming the
topic, the handler type and `IGreeter`. Today only the Lambda path fails at all, and none fail for
this reason. Add the nine templates' own "delete the `IGreeter` line" case as a template test.

**Verify:** `dotnet test test/Benzene.Core.Test -c Release --filter "FullyQualifiedName~StartUpCheck"`,
`dotnet test test/Benzene.Grpc.Test -c Release`, `dotnet test test/Benzene.Aws.Tests -c Release`,
plus `dotnet build templates/Benzene.Templates.sln`.

**Watch for:** #320 will surface real handler-construction failures in examples and tests that have
been passing. Each one is a genuine find — fix the registration, do not weaken the check.

---

## WP-B — Broken rungs: Lambda test host and Azure Timer (#325–#330)

**Files.** `src/Benzene.Aws.Lambda.Core.TestHelpers/AwsLambdaBenzeneTestHost.cs:21-29`, the seven
`Benzene.Aws.Lambda.*.TestHelpers` packages' `MessageBuilderExtensions`,
`examples/Aws/Benzene.Examples.Aws.Minimal.Tests/Helpers/AwsEventBuilder.cs` (deleted by this WP),
`docs/getting-started-aws.md` §6,
`src/Benzene.Azure.Function.SourceGenerators/Transports/MessagingTransports.cs:417-422`,
`src/Benzene.Azure.Function.Timer/Extensions.cs:73-76`,
`test/.../AzureFunctionTriggerGeneratorTest.cs:204`.

**Rulings:**

1. **(#325, E7 / aws 1)** `AwsLambdaBenzeneTestHost` serialises with System.Text.Json — the same
   family the routers read with (`AwsLambdaMiddlewareRouter.cs:25`). Benzene's own event models are
   STJ-attributed (`[JsonPropertyName("detail-type")]` etc.); Newtonsoft ignores those attributes, so
   `AsEventBridge()`/`AsDynamoDb()`/`AsKinesis()` currently go over the wire with the wrong keys and
   are never claimed. The `Amazon.Lambda.*Events` POCOs carry no serializer-specific attributes and
   are unaffected. Alternative if the swap proves risky: add a `Stream`/`string` overload and have
   each `As*()` hand back wire JSON — but prefer the swap, it is the one place that lies.
2. **(#326, aws 1)** Ship the missing `Send*Async` rungs for parity with `SendSqsAsync` /
   `SendApiGatewayAsync`: `SendSnsAsync`, `SendEventBridgeAsync`, `SendS3Async`, `SendDynamoDbAsync`,
   `SendKinesisAsync`. Each is one line over `SendEventAsync<TResponse>(builder.AsXxx())`. The AWS
   guide §6 already promises `SendSnsAsync`; this makes the doc true rather than deleting the claim.
3. **(#327, aws 1)** One test per event source through `BuildAwsLambdaTestHost()` — the construction
   the guide recommends — so this cannot regress. Then delete
   `examples/Aws/.../Helpers/AwsEventBuilder.cs` (85 lines) and re-point the Minimal example's tests.
   **Acceptance: the hand-built `Dictionary<string, object>` event count goes to 0.**
4. **(#328, E6 / azure-google F1)** The generated Timer trigger forwards the schedule. Today
   `MessagingTransports.cs:417-422` binds the SDK `TimerInfo` and calls the overload that synthesises
   an empty `TimerTriggerInfo`, so `IsPastDue` is always false and `ScheduleStatus` always null on
   the steer path — while the explicit form the docs show forwards it. Map `TimerInfo` →
   `TimerTriggerInfo` and pass it. `AzureFunctionTriggerGeneratorTest.cs:204` currently pins the
   degraded behaviour: invert it, with a comment saying what it used to assert and why that was wrong.
5. **(#329, azure-google F5)** A second declared trigger of the same type must not silently collapse
   into the first. Either emit the `name` discriminator the two `Use*` overloads already accept and
   route on it, or fail the generator with a BENZ diagnostic naming both declarations. A generator
   diagnostic is the cheaper honest answer if routing is a bigger change than it looks — decide from
   the code, and record which in the commit.
6. **(#330)** Re-point `docs/getting-started-aws.md` §6 and `docs/azure-functions.md`'s Timer section
   at what now exists, with `<!-- compile: -->` markers.

**Verify:** `dotnet test test/Benzene.Aws.Tests -c Release`, `dotnet test test/Benzene.Core.Test -c
Release --filter "FullyQualifiedName~Aws|FullyQualifiedName~AzureFunctionTriggerGenerator"`,
`dotnet test examples/Aws/**/*.Tests` via `dotnet build Benzene.Examples.sln` then the example test
projects.

---

## WP-C — The outbound routing rung for Lambda and gRPC (#331–#334)

**Files.** `src/Benzene.Clients.Aws.Lambda/Extensions.cs:50-65` (+ new
`OutboundLambdaContextConverter`), `src/Benzene.Grpc.Client/Extensions.cs:51-63` (+ the
`GrpcSendMessageContext` equivalent), `src/Benzene.Clients/DefaultBenzeneMessageSender.cs:51-59`
(read only — the envelope plumbing already exists), `src/Benzene.Clients.Aws.Sqs/`'s
`OutboundSqsContextConverter` (the recipe to copy), `docs/clients.md:26,282,337,520`,
`docs/reference/packages.md:150`, both packages' `CLAUDE.md`.

**The finding (E8 / clients B1).** `.UseAwsLambda(...)` and `.UseGrpc(...)` exist only on
`IBenzeneClientContext<T, Void>`, not on `OutboundContext`, so no route can be registered for a
Lambda or gRPC target. A generated client SDK targets `IBenzeneMessageSender` and therefore cannot
call the port's flagship host through the documented path; the user drops to
`AwsLambdaBenzeneMessageClient` and forfeits `.UseRetry`, `.UseCorrelationId`,
`.UseW3CTraceContext`, `.UseOutbox`, `.UseClaimCheck` and the start-up route check. Both packages'
`CLAUDE.md` call the gap "deliberately deferred" — the review's point is that two docs advertise it
as shipped.

**Rulings:**

1. **(#331)** `OutboundLambdaContextConverter : IContextConverter<OutboundContext,
   LambdaSendMessageContext>` following `OutboundSqsContextConverter`: build the
   `BenzeneMessageClientRequest`, choose `InvocationType` from whether a response is wanted.
   `DefaultBenzeneMessageSender` already deserialises the raw `BenzeneMessageClientResponse` once
   `TResponse` is known, so typed request/response works with no new envelope code.
2. **(#332)** The same recipe over `GrpcSendMessageContext`. If the gRPC converter genuinely cannot
   carry a typed response (`docs/clients.md:520` says it "always maps the response to `Void`"),
   ship the `Void` route and say so in one line in the doc — do not leave the rung missing.
3. **(#333, clients S4)** Give batch send a DI seam and a routing rung while in this file family; it
   currently has only the bottom rung (`new SqsBatchMessageClient(...)`) and is undocumented.
4. **(#334, clients S9)** Expose a public read model for the outbound routing table. The descriptor
   currently reflects over private `_routes`, which is the same hostage shape §4.1 forbids.

**Red-green.** A route registered for a Lambda target sends and receives a typed response through
`IBenzeneMessageSender`, with `.UseRetry()` and `.UseW3CTraceContext()` in the pipeline observably
running (assert the retry count and the traceparent header on the captured invoke). Red today: the
overload does not exist, so the test does not compile — that is the reproduction.

**Verify:** `dotnet test test/Benzene.Core.Test -c Release --filter "FullyQualifiedName~Clients"`,
`dotnet test test/Benzene.Grpc.Test -c Release`.

---

## WP-D — The CLI front door (#335–#337)

**Files.** `src/Benzene.CodeGen.Cli/Program.cs:12-45`,
`src/Benzene.CodeGen.Cli.Core/Parsing/{CommandSplitter,CommandParser}.cs`,
`src/Benzene.CodeGen.Cli.Core/CommandRouter.cs:19-30`, `.../Commands/HelpCommand.cs`,
`.../Commands/Spec/FileSpecSource.cs:19-27`, `.../Commands/HealthCheckCommand.cs`,
`src/Benzene.CodeGen.Build/Benzene.CodeGen.Build.targets:95` (the MSBuild caller).

**The finding (E12 / clients B2).** Verified in source: `Program.cs:15-30` is
`if (args.Length == 0) { do { ReadLine(); try { ExecuteAsync(stringArgs); } catch { print; } } while (true); }`.
The class comment calls this "the interactive REPL … it runs forever". Under a closed stdin (CI, a
pipe, MSBuild) `ReadLine()` returns `null` immediately, `CommandSplitter.Split(null)` throws on
`args.Length`, the catch prints, and the loop spins forever writing stack traces. Separately
`benzene --help` is parsed as a command named `--help`, prints "not found", then **throws**, so the
process exits 1. Only `benzene help` works, and no doc mentions it.

**Rulings:**

1. **(#335)** Bare `benzene` prints usage and exits non-zero; `--help` / `-h` / `-?` / `help` print
   help and exit **0**; `--version` / `-v` prints the assembly version and exits 0. Keep the REPL
   only behind an explicit `benzene repl` (or delete it — check whether anything uses it; the
   MSBuild target does not). Make `CommandSplitter.Split` null-safe regardless, as defence.
2. **(#336)** `<command> --help` reaches that command's help instead of running the command with
   unknown attributes silently ignored (`PayloadMapper` currently drops them — clients S6). While
   there: reject unknown flags by name rather than ignoring them, and make boolean flags actually
   bind. `benzene-descriptor`'s unknown-flag failure should name the flag (clients P2).
3. **(#337, clients S5)** `benzene spec --file x --type openapi` currently returns the benzene-type
   document regardless of `--type`, because `FileSpecSource` ignores the request. Either honour
   `--type` or fail naming what was asked for and what the file contains. Fix
   `HealthCheckCommand`'s null-client hint (clients P3) in the same pass.

**Red-green.** Shell-level tests: `benzene` with stdin closed exits within a timeout with a non-zero
code and prints usage (red today: hangs); `benzene --help` exits 0 (red today: 1);
`benzene build --help` prints build's help (red today: runs and fails with "No spec source given").

**Verify:** `dotnet test test/Benzene.Core.Test -c Release --filter "FullyQualifiedName~Cli"`, plus a
manual `dotnet run --project src/Benzene.CodeGen.Cli < /dev/null` that must terminate.

---

## WP-E — Mesh: dispatch path, collector sink, public announce (#338–#344)

**Files.** `deploy/Mesh/Benzene.Mesh.Host/Startup.cs:365-444`,
`src/Benzene.Mesh.Ui/MeshUiExtensions.cs:157-164`, `src/Benzene.Mesh.Dispatch/MeshDispatchGuardOptions.cs:30`,
`deploy/Mesh/Benzene.Mesh.Host/MeshAuthGate.cs:69`,
`deploy/Mesh/Benzene.Mesh.Host.Test/MeshUiWiringAcceptanceTest.cs:116`,
`deploy/Mesh/Benzene.Mesh.Host/MeshSourceRegistrar.cs:42`, `src/Benzene.Mesh.Collector/`,
`src/Benzene.CloudService/{MeshAnnouncer,CloudServiceDescriptorSource,CloudServiceBuilder,Extensions}.cs`,
`src/Benzene.Mesh.Wire/Extensions.cs:25,63,139`,
`examples/K8sMesh/Mesh/Startup.cs:76-81,108-112`,
`examples/Mesh/Benzene.Examples.Mesh.Shared/EnvelopeHost.cs:106-151`, `deploy/Mesh/README.md`.

**Rulings:**

1. **(#338, E9 / mesh B1)** One constant for the dispatch path, shared by the UI attribute, the
   guard, the envelope and `MeshAuthGate`. Today the UI is handed
   `MeshUiExtensions.DefaultDispatchUrl` = `/benzene/invoke` while the guard and envelope mount at
   `MeshDispatchGuardOptions.Path` = `/mesh/dispatch`; `/benzene/invoke` is mounted only when a fleet
   source is configured and then routes `MeshCollectorHandlers.Queries` only, which does not include
   `benzene:mesh:dispatch`. Either delete `DefaultDispatchUrl` and make the parameter required, or
   redefine it as `MeshDispatchGuardOptions.Path`'s default — the former is safer. Fix the constant's
   XML doc, which claims dispatch "typically rides the same message endpoint fleet queries use"
   while both shipped consumers deliberately give it its own door.
2. **(#339)** Replace `MeshUiWiringAcceptanceTest.cs:116`'s pinned string with a **round trip**: the
   URL the UI is handed must be a path that answers `benzene:mesh:dispatch` in the same host. That
   assertion is what would have caught this.
3. **(#340, E10 / mesh B2)** Add `fleet.source: "collector"` to `Benzene.Mesh.Host`, composed from
   the exact public calls `examples/K8sMesh/Mesh/Startup.cs` hand-rolls: register
   `MeshCollectorStore` + `IMeshFleetReadModel` (+ the collector usage source), and route
   `MeshCollectorHandlers.All` rather than `.Queries` when the collector is the source. Reuse the
   existing `auth.ingestion` section for the ingest topics — services self-reporting is exactly the
   case it exists for. **Acceptance: `K8sMesh/Mesh/Startup.cs` loses its hand-rolled collector
   wiring, and `examples/Mesh/.../Aggregator/Startup.cs` loses its bespoke `EnvelopeHost` path.**
4. **(#341, E11 / mesh B3)** Promote the announce leg to public API in `Benzene.Mesh.Wire`:
   `UseMeshAnnounce<TContext>(info, descriptor, collectorEnvelopeUrl, healthChecks, heartbeatInterval)`.
   `MeshAnnouncer` and `CloudServiceDescriptorSource` are `internal`, so `WithCollector`'s register +
   heartbeat loop cannot be written from public API — §4.1's definition of a hostage. The descriptor
   and trace legs are already public; this completes the set. `UseBenzeneCloudService` then *composes*
   it with no behaviour change. **Acceptance: `EnvelopeHost.StartAnnouncing` (45 hand-copied lines)
   is deleted**, and the rung-2 story becomes three public calls, documented.
5. **(#342, mesh S6)** Turning on dispatch is three calls that must agree on a path; `docs/mesh-ui.md`
   names only `UseMeshDispatch()`, which mounts no route. Ship `UseMeshDispatchEndpoint(options)` (or
   equivalent) that does guard + handler + envelope in one call, with the explicit three named in its
   XML doc.
6. **(#343)** `deploy/Mesh/README.md` documents `WithCollector` ↔ `fleet.source: collector` as a
   pair — the producer and consumer ends of one convention, per §4 "both sides of the wire".
7. **(#344)** Add the acceptance test that a service calling `WithCollector(hostUrl)` against a Host
   configured with `fleet.source: collector` appears in the Fleet read model. That is the end-to-end
   this WP exists to make true.

**Verify:** `dotnet test deploy/Mesh/Benzene.Mesh.Host.Test -c Release`, `dotnet test
test/Benzene.Mesh.Test -c Release`, `dotnet build Benzene.Examples.sln`, and
`Benzene.Mesh.Host --validate-config` against `deploy/Mesh/mesh.sample.json`.

---

## WP-F — Doc honesty sweep (#345–#356)

Docs only, no source changes — but it closes two blockers and eight should-fixes, and it is the
cheapest user-visible win in the plan. One worktree, one commit per doc family.

**Rulings, each one "make the doc match the code that exists after Phase 1":**

1. **(#345, E13 / selfhost 1)** `getting-started-kafka.md:221-223`,
   `getting-started-worker.md:251-253,576-581` and `testing-benzene.md:98-111` currently say there is
   no `BenzeneTestHost` for workers. `BuildKafkaWorkerHost`, `BuildRabbitMqWorkerHost` and the
   Service Bus / Event Hub equivalents ship, run `.WithStartUpChecks()`, and the templates already
   use them; `grep BuildKafkaWorkerHost docs/` returns nothing. Replace those paragraphs with the
   template's shape and add a worker section to `testing-benzene.md`. Keep the live-broker path as
   the integration tier, not the only option.
2. **(#346, E14 / clients B3)** `docs/contract-artifacts.md:47,110` pass `--service-version` without
   `--version-scheme`; `EmitOptions.ValidateVersion` rejects that combination with a message naming
   the three valid schemes. Fix both snippets and add `--version-scheme` to the flag table.
3. **(#347, selfhost 2)** `common-middleware.md:143-146` says W3C trace continuation is HTTP-only.
   `Benzene.Diagnostics/W3CTraceContextExtensions.cs:38` is generic over `TContext`, and
   `monitoring.md` plus the cookbook say so. Fix the one that is wrong.
4. **(#348, clients B4)** `docs/clients.md` contradicts the code in six places (transport list,
   `.UseAwsLambda` route, EventBridge and Kafka "not implemented", `ValidateOutboundRouting`
   semantics) and never covers Pub/Sub, Step Functions, in-process, `UseParallel`, batch or versioned
   send. Rewrite against the code as it stands after WP-C.
5. **(#349, aws 12)** The AWS guide says `UseBenzeneInvocation()` does not reach SQS/SNS/Kafka; every
   transport except API Gateway auto-wires it. Say that, and name API Gateway as the exception.
6. **(#350, aws 9)** Both AWS docs say the no-argument `AddMessageHandlers()` "scans the calling
   assembly". It registers no discovery at all (`DI/Extensions.cs:165-173`) — advice that silently
   removes every handler. Fix, and drop the stale "locked finder" comment in `examples/Aws`.
7. **(#351, first-service F10 / selfhost 16)** `hosting.md` shows a `UseBenzene<TStartUp>()`
   implementation that no longer exists; `health-checks.md` shows an `IHealthCheck` the code no
   longer has. Regenerate both from source.
8. **(#352, first-service F9)** One first-ASP.NET-service shape, not three. The guide, the template
   and the example the guide links to currently differ. Pick the shape WP-A leaves standing and make
   all three identical.
9. **(#353, aws 13)** `aws-iam-permissions.md` is missing the Kinesis, EventBridge and DynamoDB
   Streams trigger permissions and every DynamoDB/S3 store permission the guide sends readers there
   for.
10. **(#354, aws 4)** Settle the runtime story: the guide and `examples/Aws` say `dotnet10`, the
    template and serverless cookbook say `dotnet8`, the ASP.NET cookbook and `AwsMesh` use
    `provided.al2023`. **This needs someone with AWS access to confirm the managed `dotnet10`
    runtime exists.** If it does not, the guide's deploy step fails for every reader and this becomes
    a blocker — resolve it before launch either way.
11. **(#355, azure-google F11)** Every Azure cookbook shows only the hand-written trigger, never the
    declared form the guides steer to; the Cosmos cookbook's `Configure` signature cannot compile.
12. **(#356)** Add `<!-- compile: … -->` markers to every snippet a newcomer would paste, across all
    guides. `DocSnippetsCompileTest` already compiles marked snippets; the broken ones the review
    found are precisely the unmarked ones. **Acceptance: every fenced `csharp` block in
    `docs/getting-started*.md` and `docs/cookbooks/*.md` either carries a marker or a one-line comment
    saying why it cannot compile standalone.**

**Verify:** `dotnet test test/Benzene.Core.Test -c Release --filter "FullyQualifiedName~Docs"` (this
is the job that grows the most in this WP), plus the existing `AwsQuickstartRunsTest`.

---

# PHASE 2 — launch backlog

Sequenced by value. None of these blocks the release; all of them are the difference between a
framework that reads as finished and one that reads as almost finished.

## WP-G — Shorthands, ordered by duplication count (#357–#372)

Each row is a missing shorthand the review proved by counting copies. **The count going to zero is
the test.** Full before/after snippets are in the cited findings.

| # | Shorthand | Copies today | Finding |
|---|---|---|---|
| #357 | Default `GetConfiguration()` — make the base implementation the default, delete every override that equals it | 13 templates + 6 AWS examples | first-service F5, aws 8 |
| #358 | `UseBenzeneObservability()` over the `UseW3CTraceContext().UseBenzeneEnrichment().UseBenzeneMetrics()` + `UseLogResult(_ => { })` prelude | 7 chains, 12 template copies, `UseBenzeneEnrichment()` ×29, OTel block ×8 files | first-service F4, selfhost 13, aws 6, mesh S2 |
| #359 | `BuildBenzeneMessageHost()` / `app.UseBenzeneMessage(p => …)` — the rung-1 in-process host | 7 hand-rolled, none running start-up checks | first-service F3 |
| #360 | `BenzeneFunctionsHost` — the Azure `Program.cs` preamble | 8 examples + 2 disagreeing templates | azure-google F6 |
| #361 | Re-point examples/templates at the existing `AwsLambdaBootstrap.RunAsync` | 7 hand-written loops | aws 5 |
| #362 | Cloud Run `PORT` plumbing as a host default | 9–10 | azure-google F7 |
| #363 | Ship `ServiceHealthCheck` | 8 copies | selfhost 17, azure-google F15 |
| #364 | `AddAwsSqs()`-style SDK client registrations + a start-up check naming the fix | 7 hand-registered | aws 7, clients S1 |
| #365 | Cloud Service preamble: placement/identity detection defaults | 4 (+7 partial) | mesh S2 |
| #366 | `UseMeshDashboard` (UI + SpecUI + Artifacts), an `AddMeshAggregator` overload without the dummy registry, and fix the wrong default refresh topic | 5 / 5 / 5 | mesh S3 |
| #367 | Extract `MeshServiceWiring.cs` (309 lines) to a package, as `Benzene.Mesh.Artifacts` was extracted before | shared by 6 services | aws 6 |
| #368 | Public `FakeBenzeneMessageSender` / `NullBenzeneMessageClient` in `Benzene.Testing` | 2 identical + 2 hand-rolled | clients S3, P8; azure-google F14 |
| #369 | Delete redundant `AddBenzene()` / transport `Add*` / `AddHttpMessageHandlers` / `AddGrpcMessageHandlers` from examples and templates; state the rule once in the docs | 11 / 3 / 3 / 2 (+3 templates) | first-service F6, selfhost 12 |
| #370 | An OTel-on-Lambda shorthand | 137 lines of example plumbing | aws 14 |
| #371 | `examples/K8sMesh/Service`: same information declared twice in one file; helper class copied across 8 files | ×8 / ×10 | selfhost 17 |
| #372 | Kafka example ledger: `DependenciesBuilder.cs` is 4 intent lines to 21 plumbing, incl. an unused config and an HTTP `/spec` on a Kafka-only worker | — | selfhost 14, 15 |

**Ruling that applies to all of them.** A new shorthand must pass §4.1's three tests: a user could
have written it from public API, you can drop exactly one level from it, and every rung it lands on
is public and documented. Each one's XML doc names the explicit form it composes —
`BenzeneHost`/`BenzeneWebHost`/`AwsLambdaBootstrap` are the in-repo models.

---

## WP-H — Conventions that need a check (#373–#384)

Every item: the convention exists and is steered to; the failure currently arrives on the message
path or never. Give each one a start-up check whose message names **what was looked for, where, and
what to add**. `RequirePolicy(name)` is the in-repo model that already gets this right.

- **#373** (selfhost 5) Missing-store failures across the middleware families — `UseOutbox()` without
  `AddOutbox()` fails at start-up on `OutboxOptions` with no hint. None of Kafka.Core, RabbitMq,
  Idempotency, Outbox, ClaimCheck, Auth, Cache, RateLimiting, Resilience ships a `RegistrationsBase`.
- **#374** (selfhost 6) Kafka: nothing ties `BenzeneKafkaConfig.Topics` to registered `[Message]` topics.
- **#375** (selfhost 7) `UseFluentValidation()` scans `AppDomain.CurrentDomain.GetAssemblies()` with
  no signal when it finds zero validators.
- **#376** (azure-google F4) A declared `[assembly: Benzene*Trigger]` and its configured `Use*()`
  pipeline are never cross-checked.
- **#377** (azure-google F9) `UseMessageHandlers()`'s all-assembly scan is steered to by both guides
  with only an advisory log — and a troubleshooting entry that contradicts it.
- **#378** (clients S1) DI-resolved SDK handles (`IAmazonSQS` and siblings) are never verified at
  start-up. Pairs with #364.
- **#379** (clients S2) `ValidateOutboundRouting` is documented as opt-in and field-name-only; it is
  auto-registered and attribute-gated, so a hand-rolled routing class written per the doc is silently
  ignored. Fix the code or the doc — decide which is the intended contract, then make both agree.
- **#380** (mesh S4) Host `services[]`: `specUrl`/`healthUrl` documented "Required" but unvalidated →
  first-poll `Unreachable`; duplicate names last-wins; `pollIntervalSeconds ≤ 0` silently becomes 1 s.
  Also update `mesh.sample.json`, Helm `values.yaml` and `CONFIG.md`, which still use pre-§5
  `/spec?type=benzene` + `/healthcheck` paths — a conforming Cloud Service polled with the shipped
  sample is unreachable. Consider the proposed `services[].url` base-URL key (the derivation already
  exists twice in the estate).
- **#381** (mesh S5) `WithCollector("nonsense")` never validates, never logs (the package has no
  `ILogger`), retries every 2 s forever, and the profile still reports R6 satisfied.
- **#382** (aws 9) Two competing `ConfigureServices` shapes with no check on their interaction.
- **#383** (aws 18) Three AWS conventions with no start-up check, and an unrecognised-event message
  that names nothing.
- **#384** (first-service F7 follow-on) Extend #323's "nothing mounted" check to name which
  platform's `Use*` calls were seen and no-opped.

---

## WP-I — Transport and worker parity (#385–#396)

The same capability must cost the same across transports. From the review's parity matrices:

- **#385** (selfhost 8) Health auto-wiring exists on Kafka/RabbitMQ/Service Bus, not SQS/Event Hub.
- **#386** (selfhost 10) `UseBenzeneInvocation()` is seeded by Kafka/SQS/Event Hub workers but not
  RabbitMQ/Service Bus, so `UseBenzeneEnrichment()` silently drops `invocationId` on two transports.
- **#387** (selfhost 9) No `BuildSqsWorkerHost`, and `UseSqs` does not register the application
  singleton the pattern relies on.
- **#388** (selfhost 3) Worker health for Kubernetes probes has no shorthand and no documented path
  for the `UseAspNet` shape the K8s guide teaches; `K8sTransports/k8s/app.yaml:34-36` ships a bare
  TCP probe. ApiGateway has `/livez`/`/readyz` defaults; AspNet does not.
- **#389** (aws 11) Consuming SQS from a worker costs ~15 lines of SDK plumbing versus one line on
  Lambda, and the two SQS packages disagree on the wire-names override.
- **#390** (aws 3) The Lambda host ships no logging provider, so five services re-add one and a
  broken SQS pipeline is silent by default.
- **#391** (azure-google F13) `IBenzeneWireNames` is honoured by the self-hosted consumers but not the
  Functions triggers; `topicPropertyKey` exists on `UseServiceBus` but not `UseEventHub`.
- **#392** (azure-google F8) `AsEventHubBenzeneMessage()` exists in two packages with two
  incompatible wire shapes, and the property-routed trigger path has no test helper.
- **#393** (azure-google F12) Outbox relay on Azure Functions exists only as an undocumented
  hand-composition, though both halves ship.
- **#394** (clients S14) Rung-parameter parity gaps that change behaviour across the client family.
- **#395** (aws 15) IaC parity: the SQS/SNS templates ship none, the API Gateway template ships some,
  and the Terraform generator is used by no example and named in no guide.
- **#396** (aws 17, selfhost 24) Verb-shape drift across the eight `UseXxx` and their test helpers.

---

## WP-J — Client family and contract surface (#397–#404)

- **#397** (clients S7) The producer half of contract artifacts costs four steps, the consumer half
  two. Give the producer a shorthand.
- **#398** (clients S13) `Convert<TContext, TContextOut>` is a public extension in six packages — an
  ambiguity hazard on the documented drop-one-level rung. One home.
- **#399** (clients S11) The outbound boilerplate ledger in the examples.
- **#400** (clients S10) "What it talks to" is declared twice (`AddResponseEventDeclarations` +
  `.Route`) in all three cloud mesh examples.
- **#401** (aws 10) Example tests hand-roll SQS/SNS/API Gateway events and the test-host wrap the
  framework ships (×3, ×13).
- **#402** (first-service F11) Test-rung ceremony parity: 3 lines on Lambda, 13 in-process, 8 for a
  worker, "use `WebApplicationFactory`" on ASP.NET. Closes with #359.
- **#403** (clients S15) Two docs disagree on which hash the contract-drift check compares.
- **#404** (clients S8) The descriptor emits `transports: []` for three of four hosts. **Note: the
  proposed fix (`null` for "unknown" vs `[]` for "none") is a wire-shape change — see spec items
  below. In this repo, only fix the cases where the host genuinely knows its transports.**

---

## WP-K — Mesh config parity and the host as a Cloud Service (#405–#412)

- **#405** (mesh S8) One vocabulary across Host config, Terraform variables and Helm values: two OIDC
  implementations with incompatible policy vocabularies (domains+groups vs exact emails); dispatch
  knobs spelled three ways with bad values rejected on the Host and silently ignored on Lambda; the
  Host has no refresh guard while every example has one; four names for "artifact prefix". **Helm is
  parity-by-construction and is the model to converge on.**
- **#406** (mesh S9) The mesh host is not itself a Cloud Service, though §5.2 says it should be —
  which is why Helm probes it with `tcpSocket`.
- **#407** (mesh S10) `UseBenzeneCloudService` is HTTP-only, so Lambda direct-invoke re-wires health,
  spec and handlers by hand and K8sMesh services carry two envelope endpoints with peers pointed at
  the off-standard one.
- **#408** (mesh S1) The rung-4/5 ladder is invisible from the docs site: `UseBenzeneCloudService`
  appears in zero guide pages, `WithCollector` in zero docs, and the explicit form is named only in a
  `CLAUDE.md`.
- **#409** (mesh S7) The K8sMesh README and two CI workflows `curl -XPOST /mesh/refresh` with no
  `X-Benzene-Refresh` header against a guard that returns 403; the compose README curls
  `/mesh/refresh` on a Host whose route is `/mesh/aggregate`; the AwsMesh README claims R1–R8 while
  the served descriptor reports `profile.missing: ["R6","R8"]`.
- **#410–#412** Reserved for what #405 uncovers — config convergence usually finds more.

---

## WP-L — NuGet packaging for go-live (#413–#420)

- **#413** (clients S12) 120 of 178 packages carry the generic hosting description. Every package a
  user would search for needs its own one-line description, tags and README.
- **#414** Umbrella/meta-packages: two exist, both AWS. Ship one per platform so a user takes one
  dependency.
- **#415** Six violations of the `AGENTS.md` package-naming rule (family-first vs platform-first),
  listed in clients S12. Renames are breaking — decide before 1.0, not after.
- **#416** 26 TestHelpers packages ship, 10 are documented.
- **#417–#420** Reserved: prerelease→1.0 versioning story, the `-alpha` suffix in every doc, and the
  `docs/cli.md` that three places link to and does not exist (clients P4).

---

## WP-M — Polish sweep (#421–#450)

All 44 POLISH findings, tracked by review-doc ID rather than individually planned here:
first-service F12–F17; aws 16–23; azure-google F15–F19; selfhost 18–27; clients P1–P10; mesh P1–P5.
Mechanical, low-risk, and a good first WP for a new contributor. Two worth pulling forward because
they mislead a reader: `examples/Asp/.../Startup.cs:111-115` states the router is "unconditionally
terminal … always answers, even NotFound" while the code, the doc and the test say the opposite for
unmatched routes (first-service F14); and `UseMessageHandlers(_ => { })` appears 10 times when the
no-arg overload is identical (first-service F13, aws 16).

---

## Coordination notes

- **Merge order:** WP-A, then WP-B/C/D/E in parallel (file-disjoint), then WP-F, then Phase 2 in
  table order. WP-F must land after A–E because several of its docs describe behaviour those WPs
  change.
- **File overlaps to watch.** WP-A and WP-B both touch AWS TestHelpers (A adds a start-up-check
  assertion, B changes the serializer) — B rebases on A. WP-E and WP-K both touch
  `deploy/Mesh/Benzene.Mesh.Host/Startup.cs`; E goes first and K rebases. WP-C and WP-J both touch
  `docs/clients.md` — J takes C's rewrite as its base. WP-G #357 (delete `GetConfiguration`
  overrides) touches every template, so it must not run concurrently with WP-A's template test work.
- **Round-18 overlap.** Round 18's WP-B touches `Benzene.RateLimiting`/`Benzene.Cache.Core` and its
  WP-J touches `examples/AwsMesh/deploy/*`. This plan touches neither in Phase 1. If both rounds run
  together, round 18 merges first.
- **Regression guard.** After all merges, every round 11–18 regression test must still pass. WP-A in
  particular adds a check that runs on every existing host and test host — expect it to surface real
  registration gaps in the example and test corpus; each is a find, not a reason to weaken the check.
- **Acceptance greps.** Record these before and after; each must reach the stated target:
  `GetConfiguration()` overrides equal to the default → 0; `UseBenzeneEnrichment()` → 1 per pipeline
  via the new shorthand; hand-written Lambda bootstrap loops → 0; `Program.cs` Azure preambles → 0;
  `class ServiceHealthCheck` → 1; `FakeBenzeneMessageSender` → 1 (in `Benzene.Testing`);
  `StartAnnouncing` in examples → 0; hand-built AWS event dictionaries → 0.
- **`[OPEN]` entries to record** in `outstanding-bugs.md`: whether the CLI REPL should survive at all
  (WP-D); whether `.UseGrpc` can carry a typed response or ships `Void`-only (WP-C #332); whether the
  six package renames happen before 1.0 (WP-L #415); the AWS managed-runtime question (WP-F #354),
  which is the one item that could still turn into a blocker.
- **Filed for the `benzene` (spec) repo, not this port:** whether health exposure should be implicit
  as it is in the Python port (§1/§5.2); `mesh.md` §2's `produces`/`outbound-registry` versus the
  .NET `WithConsumes` field name, with the conformance fixture as arbiter (mesh P5); and whether the
  descriptor should emit `transports: null` for "unknown" versus `[]` for "none" (clients S8).
  Nothing in this review required a change to §4.1 itself.
