# Ergonomics review — go-live summary (2026-09-02)

**Reviewer:** the cross-language ergonomics champion (owner of design-principles.md §4.1, the
shorthand ladder), coordinating six territory reviews of `benzene-dotnet` at `f3f1be5`.
**Question asked:** is the .NET port ready to go live, judged on ergonomics — ceremony versus magic,
the ladder from shorthand to explicit form, start-up checks for every convention, and honest docs.
**Method:** read-only. No `dotnet` SDK in the sandbox; every claim is a source trace. The
coordinator independently re-verified the ten headline blockers marked ✔ below against the cited
lines. Nothing in `src/`, `docs/`, `examples/`, `templates/` or `deploy/` was changed.

## Verdict: NOT READY — 14 go-live blockers, 75 should-fix, 47 polish

The ladder itself is sound. Every routine capability has an explicit form, nearly every one has a
shorthand, and the shorthands the reviewers traced (`BenzeneHost`, `BenzeneWebHost`,
`AwsLambdaHost`, `UseKafka`/`UseRabbitMq`/`UseAspNet`, the eight Azure trigger declarations, the
eleven-transport outbound-routing core, the generated client SDK) are honest compositions of public
API with the explicit form named in their own docs. The start-up-check phase is, in the AWS
reviewer's words, "the best implementation of §4.1 rule 3 I have seen in any port".

The blockers are at the edges of that ladder, and they cluster into four themes. Most are cheap.
About five are real code changes. None are architectural.

| Territory | Doc | Blockers | Should-fix | Polish |
|---|---|---|---|---|
| First service, rungs 1–3 | `ergonomics-review-first-service-2026-09.md` | 2 | 9 | 8 |
| AWS Lambda | `ergonomics-review-aws-2026-09.md` | 1 (+1 conditional) | 14 | 8 |
| Azure + Google Functions | `ergonomics-review-azure-google-2026-09.md` | 2 | 12 | 6 |
| Self-host, Kubernetes, middleware families | `ergonomics-review-selfhost-middleware-2026-09.md` | 2 | 15 | 10 |
| Outbound clients, codegen, CLI, testing | `ergonomics-review-clients-codegen-cli-2026-09.md` | 4 | 15 | 10 |
| Mesh + Cloud Service, rungs 4–5 | `ergonomics-review-mesh-2026-09.md` | 3 | 10 | 5 |

## The 14 blockers, ranked

Ranked by how many newcomers hit them and how badly. "✔" = coordinator re-verified the cited lines.

### Theme 1 — Entry points that bypass the start-up checks (the framework's own best feature)

The port's rule-3 story is that a mis-wired service fails before the first message. Five documented
entry points do not run the checks, and one class of misconfiguration is invisible to all of them.

1. **E1 ✔ The documented first ASP.NET service skips the checks.** `IApplicationBuilder.UseBenzene(Action)`
   (`src/Benzene.AspNet.Core/BenzeneExtensions.cs:106-111`) never calls `RunStartUpChecks`; only the
   `StartUp` overload at `:178` does. `docs/getting-started-aspnet.md` §4, the `benzene.asp`
   template and `examples/Grpc` all use the action overload — so the "no cloud account required"
   path is the one where a bad handler surfaces on the first request. It also reopens a copy of
   the container into a second provider (`AspApplicationBuilder.cs:73-85`). Fix: one line in the
   overload plus a pre-Build inline overload; re-point doc, template and example. *(first-service F1)*
2. **E2 ✔ The real Google Cloud Functions hosts never run the checks; their test helpers do.**
   `GoogleCloudFunctionHost.cs:37`, `GooglePubSubFunctionHost.cs:35` build without
   `.WithStartUpChecks()`; `grep RunStartUpChecks src/Benzene.GoogleCloud.Functions.*` is empty.
   *(azure-google F2)*
3. **E3 A missing handler constructor dependency is found by the first message, not at start-up,
   on every host.** The checks construct middleware, not handlers; `ValidateOnBuild` is opt-in and
   no real host sets it. Found independently by two reviewers. Fix: a `HandlerResolutionStartUpCheck`
   beside the existing four (code sketched in first-service F2), or `validateOnBuild: true`.
   *(first-service F2, aws 2)*
4. **E4 `InlineSelfHostedStartUp.Build()` / `BuildHostedService` skip the checks** — the rung the
   worker guide promotes for "unit-testing the wiring" cannot catch a wiring bug
   (`src/Benzene.SelfHost/InlineSelfHostedStartUp.cs:29-45`). *(selfhost 4 — graded should-fix
   there; promoted here because it is the same defect as E1/E2)*
5. **E5 Azure Functions runs its checks inside the scoped app factory** — on the first invocation and
   every invocation, not at host start (`HostBuilderExtensions.cs:36-44`). *(azure-google F3 —
   should-fix there; same theme)*

### Theme 2 — Rungs that are broken or held hostage

6. **E6 ✔ Declared Azure Timer trigger silently discards the schedule.** The generator binds the SDK
   `TimerInfo` and calls the overload that synthesises an empty `TimerTriggerInfo`
   (`MessagingTransports.cs:417-422`, `Timer/Extensions.cs:73-76`); the doc's own `UseTick` sample
   reads `IsPastDue` / `ScheduleStatus?.Next`, which are always false / null on the steer path. The
   test at `AzureFunctionTriggerGeneratorTest.cs:204` pins the degraded behaviour. *(azure-google F1)*
7. **E7 ✔ The Lambda test host cannot carry three of eight event sources.** `AwsLambdaBenzeneTestHost`
   serialises with Newtonsoft (`:23,29`); the EventBridge/Kinesis/DynamoDB event models are
   System.Text.Json-attributed, so `AsEventBridge()`/`AsDynamoDb()`/`AsKinesis()` never reach a
   router. The Minimal example's own helper hand-builds a dictionary to work around it, and the
   guide names a `SendSnsAsync` that does not exist. *(aws 1)*
8. **E8 AWS Lambda and gRPC have no outbound-routing rung, and `docs/clients.md` says they do.** A
   generated client cannot reach the flagship host through `IBenzeneMessageSender`; the fallback
   loses every outbound middleware. The envelope plumbing already exists
   (`DefaultBenzeneMessageSender.cs:51-59`). *(clients B1)*
9. **E9 ✔ Mesh host Test Console posts `mesh:dispatch` to a path where nothing dispatches.** The UI
   is told `MeshUiExtensions.DefaultDispatchUrl` = `/benzene/invoke`; the dispatch envelope and its
   guard mount at `/mesh/dispatch`; `/benzene/invoke` is mounted only when a fleet source is set and
   then routes only `MeshCollectorHandlers.Queries` (`deploy/Mesh/Benzene.Mesh.Host/Startup.cs:365-444`).
   The acceptance test pins the wrong attribute value instead of round-tripping. *(mesh B1)*
10. **E10 The service-side `WithCollector(url)` has nowhere to land in the shipped operator host.**
    `Benzene.Mesh.Host` has no push collector (`ValidFleetSources = none/xray/tempo/jaeger`); the
    operator must hand-roll `examples/K8sMesh/Mesh/Startup.cs` (142 lines) to receive a one-line
    feed. *(mesh B2)*
11. **E11 Register + heartbeat has no public explicit form.** `MeshAnnouncer` and
    `CloudServiceDescriptorSource` are `internal`; the shorthand holds the capability hostage (§4.1's
    exact phrase), and it is what makes rung-2 heartbeating impossible without copying framework
    code (`examples/Mesh/Shared/EnvelopeHost.cs:106-151` does exactly that). *(mesh B3)*
12. **E12 ✔ `benzene` with no arguments loops forever on a closed stdin, and `--help` exits 1.**
    `Program.cs:15-30` is a `do { ReadLine() } while (true)`; only `benzene help` works and no doc
    says so. *(clients B2)*

### Theme 3 — Docs that deny or contradict a shipped capability

13. **E13 ✔ Three guides say worker test hosts do not exist; four packages ship them and the
    templates use them.** `getting-started-kafka.md:221-223`, `getting-started-worker.md:251-253,
    576-581`, `testing-benzene.md:98-111` versus `BuildKafkaWorkerHost` / `BuildRabbitMqWorkerHost`
    (+ Service Bus, Event Hub), all running `.WithStartUpChecks()`. Zero doc hits for either method.
    The Kafka example hand-rolls a live-broker harness — the outcome §4.1 predicts. *(selfhost 1)*
14. **E14 ✔ `docs/contract-artifacts.md`'s own run and CI snippets exit non-zero** — both pass
    `--service-version` without `--version-scheme`, which `EmitOptions.ValidateVersion` rejects; the
    flag table omits the flag. *(clients B3)*

Also in this theme, graded blocker by their reviewers and endorsed here as a single doc-honesty
work item: `common-middleware.md:143-146` says W3C trace continuation is HTTP-only while the code is
generic (`Benzene.Diagnostics/W3CTraceContextExtensions.cs:38`) and `monitoring.md` says so ✔
*(selfhost 2)*; `docs/clients.md` contradicts the code in six places *(clients B4)*; `hosting.md`
shows a `UseBenzene<TStartUp>` implementation that no longer exists *(first-service F10)*;
`health-checks.md` shows an `IHealthCheck` the code no longer has *(selfhost 16)*; the AWS guide's
`UseBenzeneInvocation()` and `AddMessageHandlers()` troubleshooting advice is inverted *(aws 12, 9)*;
K8sMesh/compose READMEs and two CI workflows curl `/mesh/refresh` without the header the guard
demands, and the AwsMesh README claims R1–R8 while the served descriptor reports R6/R8 missing
*(mesh S7)*.

**One reviewer conflict, resolved:** the first-service doc states the `<!-- compile: … -->` markers
are "consumed by nothing under `.github/workflows/`". They are consumed by
`test/Benzene.Core.Test/Docs/DocSnippetsCompileTest.cs`, which runs in the normal test job. The
real gap is narrower: the broken snippets above carry no marker. The fix is to mark every snippet a
newcomer would paste, not to build new tooling.

### Conditional blocker

- **AWS runtime story.** The guide and `examples/Aws` deploy to `dotnet10`; the template and the
  serverless cookbook say `dotnet8`; the ASP.NET cookbook and `AwsMesh` use `provided.al2023`. If
  a managed `dotnet10` Lambda runtime does not exist at go-live, the guide's deploy step fails.
  Unverifiable from the sandbox — someone with AWS access decides. *(aws 4)*

## The should-fix backlog, by the count that argues for it

§4.1's examples rule: duplicated plumbing is a framework bug, and the number of copies is the
evidence. The reviewers counted. Each row below is a missing shorthand, listed with the copies that
prove it and the shape the reviewers propose. Full before/after snippets are in the territory docs.

| Missing shorthand | Copies found | Proposed | Doc |
|---|---|---|---|
| Default `GetConfiguration()` (every override equals the base default) | 13 templates + 6 AWS examples | make the base implementation the default; delete the overrides | first-service F5, aws 8 |
| `UseBenzeneObservability()` for the `UseW3CTraceContext().UseBenzeneEnrichment().UseBenzeneMetrics()` / `UseLogResult(_ => { })` prelude | 7 chains, 12 template copies, `UseBenzeneEnrichment()` ×29, OTel block ×8 files | one call, one package | first-service F4, selfhost 13, aws 6, mesh S2 |
| `BenzeneFunctionsHost` (Azure `Program.cs` preamble) | 8 examples + 2 templates that disagree | mirror `BenzeneHost`/`BenzeneWebHost` | azure-google F6 |
| Use `AwsLambdaBootstrap.RunAsync` instead of hand-written bootstrap loops | 7 | re-point examples/templates at the existing shorthand | aws 5 |
| Rung-1 in-process host (`BenzeneMessageApplication` + container) | 7 hand-rolled, Python port does it in 2 lines | `BuildBenzeneMessageHost()` / `BenzeneMessageApplication.Create(...)` | first-service F3, cross-port note below |
| Cloud Run `PORT` plumbing | 9–10 | host default | azure-google F7, selfhost |
| `ServiceHealthCheck` class | 8 | ship it | selfhost 17, azure-google F15 |
| Cloud Service preamble (`UseBenzeneCloudService(name, c => c.WithServiceVersion().WithInstanceId().WithPlacement().WithHealthChecks().WithHandlers())`) | 4 (+7 partial) | placement/identity detection defaults | mesh S2 |
| Mesh-host preamble + `MeshRefreshHandler` + dummy `MeshServiceRegistry` + UI/SpecUI/Artifacts triple | 5 / 4 / 5 / 5 | `UseMeshDashboard`, an overload without the dummy registry, fix the wrong default topic | mesh S3 |
| `MeshServiceWiring.cs` 309-line hand-rolled seam | shared by 6 services | extract to a package, as `Benzene.Mesh.Artifacts` was | aws 6 |
| Public `FakeBenzeneMessageSender` / `NullBenzeneMessageClient` | 2 identical + 2 hand-rolled | ship in `Benzene.Clients` or `Benzene.Testing` | clients S3, P8; azure-google F14 |
| Hand-registered AWS SDK clients, no start-up check on the handle | 7 | `AddAwsSqs()`-style registrations + a check that names the fix | aws 7, clients S1 |
| Redundant `AddBenzene()` / transport `Add*` / `AddHttpMessageHandlers` that the `Use*` calls already make | 11 / 3 / 3 / 2 (+3 templates) | delete from examples; say so once in the doc | first-service F6, selfhost 12 |
| Worker health for Kubernetes probes (`UseAspNet` shape) | K8sTransports ships a bare TCP probe | `/livez`/`/readyz` defaults as ApiGateway already has | selfhost 3 |

Other should-fixes that are not duplication but late failure or parity — the fix-round plan should
carry them as their own WPs:

- **Conventions with no check:** platform `Use*` no-op when nothing is mounted (first-service F7);
  declared Azure trigger vs configured pipeline never cross-checked, second trigger of the same type
  silently collapses (azure-google F4, F5); `BenzeneKafkaConfig.Topics` vs registered `[Message]`
  topics (selfhost 6); `UseFluentValidation()` AppDomain scan with no "found zero" signal
  (selfhost 7); `UseMessageHandlers()` AppDomain scan steered to by the Azure/Google guides with only
  an advisory log (azure-google F9); mesh host `services[].specUrl` documented "Required" but
  unvalidated, shipped samples point at pre-§5 paths (mesh S4); `WithCollector("nonsense")` never
  validates, never logs, retries forever, reports R6 satisfied (mesh S5); `ValidateOutboundRouting`
  documented as opt-in and field-name-only, actually auto-registered and attribute-gated (clients S2).
- **Failures that name what was looked for but not what to add:** every middleware family's
  missing-store failure (`UseOutbox()` without `AddOutbox()` fails on `OutboxOptions` with no hint);
  `RequirePolicy(name)` is the in-repo model that gets it right (selfhost 5).
- **Transport parity:** health auto-wire on Kafka/RabbitMQ/ServiceBus but not SQS/EventHub;
  `UseBenzeneInvocation()` seeded on Kafka/SQS/EventHub but not RabbitMQ/ServiceBus, so enrichment
  silently drops `invocationId`; SQS worker has no `BuildSqsWorkerHost` and costs 15 lines where
  Lambda SQS costs one; `IBenzeneWireNames` honoured by workers but not Functions triggers;
  `AsEventHubBenzeneMessage()` exists in two packages with two incompatible wire shapes (selfhost
  8–10, aws 11, azure-google F8, F13).
- **Client-family parity:** batch send has only a bottom rung, no DI seam, undocumented (clients S4);
  `Convert<,>` is a public extension in six packages, an ambiguity hazard on the documented
  drop-one-level rung (clients S13); `benzene spec --type openapi` silently returns the wrong
  document (S5); CLI flag names differ per subcommand and boolean flags are silently ignored (S6).
- **Mesh operator parity:** two OIDC implementations with incompatible policy vocabularies; dispatch
  knobs spelled three ways, bad values rejected on the Host but silently ignored on Lambda; four
  names for "artifact prefix"; the mesh host is not itself a Cloud Service so Helm probes with
  `tcpSocket` (mesh S8, S9). Helm is parity-by-construction and is the model.
- **NuGet packaging for go-live:** 120 of 178 packages carry the generic description; umbrellas exist
  only for AWS; six package-naming-rule violations; 26 TestHelpers shipped, 10 documented (clients S12).

## Cross-port parity (the champion's own check)

Sampled the same capability across the four ports' README quickstarts.

- **Rung 3, one handler over HTTP:** .NET is competitive only via `BenzeneWebHost.RunAsync<StartUp>`
  (1 line) — but needs a `StartUp` class of three overrides where TypeScript needs 3 lines total
  and Python 1. Two of those three overrides are the default-`GetConfiguration` duplication above.
  Naming parity is good: `UseMessageHandlers` ↔ `useMessageHandlers`, `BenzeneMessageApplication` ↔
  Python's `BenzeneMessageApplication`, `[Message]`/`[HttpEndpoint]` ↔ `@message`/`@httpEndpoint`.
- **Rung 1, invoke a pipeline in process with no host:** Python's headline "60-second" example is
  two lines. In .NET it needs a DI container, a `BenzeneStartUp` or `InlineSelfHostedStartUp`, about
  six lines, and is documented only inside the worker guide — not in `getting-started.md`'s
  platform table or `index.md`. §2 promises "a rung-1 pipeline is still real Benzene"; in .NET it is
  the least visible rung. This is the one genuine same-capability-different-cost disparity found,
  and it lines up with the first-service reviewer's F3 (hand-rolled ×7).
- **Health:** Python exposes `benzene:healthcheck` implicitly; .NET requires `UseHealthCheck(...)`
  per pipeline or a host that auto-wires it (inconsistently, per selfhost 8). A §1 "opinionated"
  question for the spec, filed for the `benzene` repo, not the port.
- The Go README quickstart has no shorthand at all (explicit struct construction, pointing at
  `examples/helloworld/` for the lifecycle) — a §4.1 concern for the Go port, out of scope here.

## What to leave alone

Every reviewer wrote a "genuinely good" section; the union is the list of things a fix round must
not break: the start-up-check phase and its `pipeline-resolution`/`terminal-middleware` switch;
`BenzeneHost`, `BenzeneWebHost` and `AwsLambdaBootstrap` with their XML docs that name the explicit
form (rule 4 done right); the `<details>`-explicit-form pattern in the Azure trigger and
`correlation-ids.md` docs; the BENZ0001–0011 generator diagnostics; the eleven-transport outbound
routing core and the generated client SDK; `AwsQuickstartRunsTest` and `DocSnippetsCompileTest`;
`--validate-config` sharing one rule set with mesh-host start-up; `MeshAuthGate.Validate`'s
no-inert-options matrix; the three-line handler shape; `PlaceOrderMessageHandler.cs` reading as
pure domain.

## Recommended fix-round shape

Ten work packages, in this order. The first three clear the blockers and are mostly one-line code
changes plus doc edits; a fix round with a .NET SDK should clear them in one pass. Task-board
numbers to be assigned by that round's coordinator (the round-18 plan ends at #317).

1. **WP-1 Start-up checks everywhere** — E1, E2, E4, E5 (run the checks from every entry point),
   E3 (`HandlerResolutionStartUpCheck` or `validateOnBuild`), the "nothing mounted" check
   (first-service F7). Regression test: for every host and test host in the repo, a `StartUp` whose
   handler depends on an unregistered service must fail at build/init, never on the first message.
2. **WP-2 Doc honesty** — E13, E14 and the whole Theme-3 list; add `<!-- compile: -->` markers to
   every pasteable snippet so `DocSnippetsCompileTest` covers them; align guide/template/example
   to ONE first-service shape per host (first-service F9, selfhost 11); settle the AWS runtime story.
3. **WP-3 Broken rungs** — E6 (forward `TimerInfo`), E7 (System.Text.Json in the Lambda test host;
   add the missing `Send*Async`), E8 (Lambda + gRPC routing rung), E9 (one dispatch path constant
   shared by UI, guard and envelope, and a round-trip acceptance test), E12 (CLI: no-args prints
   help and exits 0; `--help`/`-h` work).
4. **WP-4 Mesh operator/service seams** — E10 (`fleet.source: "collector"` in the Host, composed from
   the K8sMesh calls), E11 (`UseMeshAnnounce` public in `Benzene.Mesh.Wire`), mesh S3/S4/S5/S6.
5. **WP-5 Shorthands by duplication count** — the table above, top to bottom; each one deletes its
   copies from examples and templates in the same commit (the count going to zero is the test).
6. **WP-6 Conventions with checks** — the "conventions with no check" list; every new check names
   what it looked for, where, and what to add, using `RequirePolicy` as the template.
7. **WP-7 Transport and worker parity** — health auto-wire, `UseBenzeneInvocation` seeding, wire-name
   overrides, `BuildSqsWorkerHost`, one `AsEventHubBenzeneMessage`.
8. **WP-8 Client family parity + CLI surface** — batch DI seam, `Convert<,>` namespace, `spec --type`,
   consistent flags, `ValidateOutboundRouting` semantics.
9. **WP-9 Mesh config parity** — one vocabulary across Host/Lambda/Helm; the Host as a Cloud Service.
10. **WP-10 NuGet packaging** — descriptions, umbrellas per platform, naming-rule violations,
    document the TestHelpers.

Items filed for the `benzene` (spec) repo, not this port: whether health should be implicit
(§1/§5.2); mesh.md §2 `produces`/`outbound-registry` vs the .NET `WithConsumes` field name (mesh
P5); the descriptor emitting `transports: null` vs `[]` for "unknown" under a neutral host
(clients S8). Nothing in this review required a change to §4.1 itself.
