# Round 18 review — this session's own fresh changes (AwsMesh Terraform/workflows + mesh Startup/handler,
benzene-ui feed-error threading)

Scope, per this round's brief: re-examine the changes made earlier in *this same session* — the highest-risk,
least-reviewed code in the estate right now — across two repos: `benzene-dotnet` (`examples/AwsMesh/deploy/
main.tf`+`variables.tf`, `examples/AwsMesh/Mesh/Startup.cs`, `examples/AwsMesh/Mesh/MeshAggregateHandler.cs`,
`.github/workflows/mesh-example-aws-deploy.yml`+`mesh-example-aws-logs.yml`) and `benzene-ui`
(`ServicePage.tsx`/`ServiceList.tsx`/`TopicList.tsx`/`EdgeList.tsx` and their new tests). Reviewed against
`benzene-dotnet` `main`/`7f642b2` and `benzene-ui` `main`/`884d702`.

No dotnet SDK is available in this environment, so the AWS/C#/Terraform half was traced by hand rather than
built or run — every finding below states the regression test that would prove it, for a future round with CI
access to add. The `benzene-ui` half **was** run: `npx tsc --noEmit` (clean) and the full `npx vitest run`
suite (563 passed, 5 skipped, 0 failed) were actually executed in this session, and Terraform's `-var` JSON
handling was empirically verified with a real `terraform apply` against a scratch config (see Finding 4's
"what I checked and ruled out").

---

## Finding 1 (headline) — raising the mesh Lambda's `reserved_concurrent_executions` from 1 to 10 (this
session's own fix) measurably weakens `MeshDispatchGuardOptions`' in-memory per-target dispatch rate limit,
an interaction the fix's own extensive comment never considers

**Severity: medium-high — a documented, security-relevant guard (`MaxPerMinutePerTarget = 30`, "the bound that
matters for the target") gets materially weaker as a side effect of a change that was reasoned about at length
for a completely different concern (read-path throttling).** `examples/AwsMesh/deploy/main.tf:716` and
`examples/AwsMesh/deploy/variables.tf:54-58` raise `aws_lambda_function.mesh`'s
`reserved_concurrent_executions` from a hardcoded `1` to `var.mesh_lambda_reserved_concurrency` (default
`10`). The 35-line comment above the resource (`main.tf:670-715`) carefully reasons about the read-throttling
problem it fixes and the aggregation-write race it deliberately still accepts — but never mentions the second
guard sharing this same Lambda's concurrency pool: `POST /mesh/dispatch`'s app-level rate limiter.

### The mechanism

`MeshDispatchGuardOptions` (`src/Benzene.Mesh.Dispatch/MeshDispatchGuardOptions.cs:20-51`) enforces
`MaxPerMinutePerIdentity = 10` and `MaxPerMinutePerTarget = 30` via `MeshDispatchRateLimiter`, an in-memory
counter resolved through DI — i.e. **per Lambda execution environment**, not per fleet. This is not a new fact
this session introduced; it is already documented in `main.tf`'s own comment on the dispatch route settings:

```
# This is also the layer that carries the real guarantee. The mesh's own per-identity limiter counts
# in memory, so on a host that scales to N instances it bounds one warm instance; API Gateway counts
# across all of them, and refuses before the invoke is billed.
```

What changed is *how many* concurrent warm instances can now exist. At `reserved_concurrent_executions = 1`
(the value before this session's fix), AWS Lambda can physically run **at most one invocation at a time** for
this function — so in ordinary sequential use (a human clicking Send repeatedly in the Test Console, or a
retry loop), the SAME warm execution environment is reused for essentially every dispatch, and the in-memory
`MaxPerMinutePerTarget = 30` counter behaves close to its documented intent. At `reserved_concurrent_executions
= 10`, up to 10 concurrent warm execution environments can exist, each with its **own independent**
`MeshDispatchRateLimiter` instance (a fresh DI container per environment). A burst of concurrent dispatch
requests aimed at one target can now be spread across up to 10 of these environments by AWS Lambda's own
invocation routing, each allowing 30/minute before ITS OWN counter trips — so the effective ceiling on one
target service is no longer "30/minute," it is "up to 10 × 30 = 300/minute," bounded from above only by
whatever throughput API Gateway's edge throttle on the dispatch route physically allows through.

That edge throttle (`aws_apigatewayv2_stage.mesh`'s `route_settings` for `mesh_dispatch`,
`main.tf:863-872`) is `mesh_dispatch_throttling_rate_limit = 2` rps steady-state
(`variables.tf:196-200`) — roughly 120 requests/minute of sustained throughput. So the practical new ceiling
on one target service is **up to ~120/minute** (bounded by the edge, not the app-level guard, which is the
edge throttle doing its job) — a **~4x weakening** of the documented 30/minute-per-target guarantee, reached
simply by a caller's requests happening to land on different warm instances, which requires no attacker
sophistication (an ordinary concurrent burst, e.g. several browser tabs or a retry-with-jitter loop, does
this without any deliberate evasion).

The API Gateway edge throttle is real protection and the finding is not "the target is unprotected" — it is
that **one of the two documented layers degraded from ~30/min to ~120/min as a side effect of a fix aimed at
an unrelated problem**, and that interaction is not mentioned anywhere in either the resource's own long
comment or `variables.tf`'s description of the new variable, both of which discuss only the read-throttling
motivation and the aggregation-write race. A future reader tuning `mesh_dispatch_max_per_target_per_minute`
(`variables.tf:214-218`, "Ten people each dispatching politely still add up at the service, and the service is
what this protects") would reasonably believe that value is what bounds per-target load, when at the current
default concurrency it is the edge throttle doing most of the real bounding.

### Concrete failure scenario

An operator sets `mesh_dispatch_max_per_target_per_minute = 5` (deliberately tight, e.g. to protect a
fragile downstream service during a demo) expecting AWS Lambda's own instance reuse to make that ceiling
apply consistently. Under a burst of concurrent requests (5 browser tabs, or a retry loop with jitter) that
happens to spread across several of the 10 available concurrent execution environments, the target can
receive up to `10 × 5 = 50` dispatches in the same minute before the (unrelated, coarser, not-configured-for-
this-target-specifically) API Gateway edge throttle becomes the sole effective bound — a 10x miss on the
operator's stated intent, invisible until it's observed live (exactly the class of bug the original #73/WP-E
fix was itself found through: live CloudWatch metrics, not code review).

### Recommended regression test (no dotnet SDK available to run it here)

A unit test against `MeshDispatchRateLimiter` directly (no Lambda needed) proving the documented claim
empirically: construct N independent `MeshDispatchRateLimiter` instances (simulating N concurrent warm
environments), round-robin `TryAcquire` calls for one target across them, and assert the aggregate number of
accepted calls before all N instances refuse exceeds `MaxPerMinutePerTarget` by a factor approaching N — e.g.
`test/Benzene.Mesh.Test/MeshDispatchRateLimiterMultiInstanceTest.cs`:
`TryAcquire_SameTargetAcrossNIndependentLimiterInstances_AcceptsUpToNTimesTheConfiguredCeiling`. This proves
the mechanism in isolation; the AWS-specific severity (how many environments Lambda will actually spin up
under a given burst shape) is not unit-testable and would need to stay as reasoning from AWS's own documented
concurrency-scaling behavior, as it is above.

### Recommendation

Not a one-line fix — a design call for whoever owns this example's mesh dispatch posture (comment already
flags Finding 1's sibling concerns as `infrastructure-product-owner`/mesh territory). Options worth
considering: (a) explicitly retune `mesh_dispatch_throttling_rate_limit`/`burst_limit` downward to compensate
now that the app-level per-target guard is proportionally weaker at concurrency 10 (the two layers should be
re-balanced together, not left as they were sized for concurrency 1); (b) note the interaction explicitly in
`main.tf`'s existing long comment and `mesh_dispatch_max_per_target_per_minute`'s description, so a future
reader tuning that variable isn't misled about what it actually bounds at the current concurrency; (c) if the
absolute per-target guarantee matters more than the read-throttling fix, split the Lambda as the resource's
own comment already floats as "the other real fix (out of scope for this pass)" — a dedicated low-concurrency
function for the two write paths (aggregation *and* dispatch) versus an uncapped one for reads.

---

## Finding 2 — `mesh_lambda_reserved_concurrency` has no `validation` block, unlike every other
sensitive numeric variable added alongside it in the same file; a value of `0` silently disables the mesh
Lambda's entire HTTP + scheduled-aggregation surface

**Severity: low-medium — an easy, undetected misconfiguration with total-outage blast radius, in code that
otherwise takes validation seriously.** `variables.tf:54-58` declares
`mesh_lambda_reserved_concurrency` as a bare `type = number, default = 10` with **no `validation` block** —
unlike `trace_sample_rate` (`variables.tf:76-85`) and `refresh_min_interval_seconds`
(`variables.tf:167-176`), both of which reject a value that would silently produce a bad outcome ("0 would
record nothing, leaving the mesh blind" / "must be zero... or positive"). AWS Lambda's own documented
semantics for `reserved_concurrent_executions = 0` is "the function cannot be invoked at all — every
invocation is throttled" (this is a legitimate AWS "kill switch" value, not an error the provider rejects).
Since this one Lambda now serves **everything** — the Mesh UI, every catalog artifact fetch, `/mesh/refresh`,
`/mesh/dispatch`, OIDC login/callback, and the EventBridge-driven scheduled aggregation pass — setting this
variable to `0` (a plausible typo when an operator means "disable dispatch" or "pause the schedule" and reaches
for the wrong variable, given how many `mesh_dispatch_*`/`mesh_lambda_*` variables now exist side by side in
this file) takes down the entire mesh estate with a clean `terraform apply` and no error, no warning, and no
signal in the plan output that anything is wrong.

### Concrete failure scenario

An operator means to pause the scheduled aggregation pass temporarily and, scanning `variables.tf` for
something with "mesh" and "lambda" in the name, sets `mesh_lambda_reserved_concurrency = 0` instead of
disabling `aggregate_schedule`. `terraform apply` succeeds cleanly. Every subsequent invocation — the
EventBridge schedule, every Mesh UI page load, every `/mesh/refresh`/`/mesh/dispatch` call — is throttled by
AWS at the platform level. CloudWatch Throttles jumps to 100% of attempted invocations; the Mesh UI shows a
generic 5xx/timeout with nothing pointing at this specific variable.

### Recommended regression test

Not a runtime test (this is a `terraform validate`/`terraform plan` concern, not code) — the fix itself
would be the test: add a `validation` block requiring `var.mesh_lambda_reserved_concurrency > 0` (mirroring
`trace_sample_rate`'s pattern), which Terraform enforces at plan time. A CI check (`terraform validate` +
`terraform plan -var mesh_lambda_reserved_concurrency=0` expected to fail) would prove the guard exists,
analogous to how a C# unit test would prove a code-level input validator.

### Recommendation

Add a `validation` block matching the file's own established convention for this class of variable:
`condition = var.mesh_lambda_reserved_concurrency > 0`, with an error message naming the outage this prevents
(mirroring `trace_sample_rate`'s "0 would record nothing, leaving the mesh blind" phrasing). Low effort,
directly consistent with the file's existing pattern, no design call needed.

---

## Finding 3 — `mesh_allowed_emails`'s workflow_dispatch input is documented as "not sticky," but nothing in
the deploy workflow's own output confirms what the *resulting* allowlist is after an apply — a maintainer who
redeploys for an unrelated reason (a code update) and forgets to re-pass the input silently reverts every
previously-granted teammate's access back to the single default owner, with zero signal in the run

**Severity: low-medium (documented, but the documentation is a weak safety net for a security-relevant, silent
access change).** `.github/workflows/mesh-example-aws-deploy.yml:24-27` and `:194-219` are honest about the
behavior in the input's own description: *"Not sticky - blank keeps variables.tf's own default
(daniellepelley@gmail.com only), so pass the FULL list again on each run you want it to stay set - a run with
one email drops any others not repeated here."* This is correct and matches how the `-var` flag interacts with
Terraform state (a `-var` override is never persisted; the next apply without it falls back to
`variables.tf`'s own `default`). The gap is not the mechanism — it is that the ONLY place this fact is stated
is a paragraph of GitHub Actions input help text, easy to skip when re-running a previously-configured
workflow with mostly-default inputs (e.g., re-running "Mesh Example AWS Deploy" just to push a new Lambda zip
after a code change, leaving `mesh_allowed_emails` at its blank default without realizing that field is not
"leave unchanged," it is "reset"). Nothing in the workflow's own run output (the "Terraform apply" step, or
the closing "Show URLs" step, `:247-249`) states what `mesh_allowed_emails` ended up being after the apply, so
a maintainer who does this has no way to notice the access-list reversion from the workflow's own logs — they
would find out only when a previously-added teammate reports being locked out.

### Concrete failure scenario

Week 1: an operator runs the deploy workflow with `mesh_allowed_emails = "owner@gmail.com,teammate@gmail.com"`
to add a colleague. Week 3: the operator (or a different maintainer unaware of week 1's input) re-runs the same
workflow with defaults (blank `mesh_allowed_emails`) to pick up an unrelated code change. The apply succeeds;
`MESH_ALLOWED_EMAILS` on the mesh Lambda silently reverts to `daniellepelley@gmail.com` only. The teammate's
next login attempt is refused with no indication anything changed on the infrastructure side — from their
perspective, access simply stopped working.

### Recommendation

Two independent, non-exclusive options, both worth a maintainer/product-owner decision rather than a
unilateral change here: (a) echo the resolved `mesh_allowed_emails` (from `terraform output`, if exposed, or
by having the workflow print `emails_json`/the fallback default it's about to apply) as a workflow step output
BEFORE `terraform apply` runs, so a maintainer sees "this run will set the allowlist to: [...]" and can abort
if that's not intended; (b) consider making the allowlist genuinely sticky by having the workflow read the
CURRENT `MESH_ALLOWED_EMAILS` value (e.g. via `terraform output` or a describe-function-configuration call)
and use it as the default when the input is blank, rather than falling through to `variables.tf`'s hardcoded
single-owner default — though this changes the "blank = explicit reset" semantics the input currently
documents, so it is a genuine design tradeoff, not a pure bugfix.

---

## Finding 4 — swept, no finding: `mesh-example-aws-logs.yml`'s new CloudWatch/API-Gateway metrics step

Verified the specific things this round's brief called out:

- **AWS CLI shorthand/`--query` correctness** — cross-checked every JMESPath expression against the real
  `GetMetricStatistics`/`GetApis` response shapes. `ExtendedStatistics.p50`/`p90`/`p99`
  (`mesh-example-aws-logs.yml:121,163`) correctly matches the exact percentile keys requested via
  `--extended-statistics p50 p90 p99`/`p90 p99`; combining `--statistics Average Maximum` with
  `--extended-statistics` in one call is valid (the real API accepts both `Statistics` and
  `ExtendedStatistics` in one `GetMetricStatistics` request); `AWS/Lambda`'s `ConcurrentExecutions` metric
  DOES support the `FunctionName` dimension (confirmed against AWS's documented per-function Lambda metrics —
  the code comment at `:138` calling this out as worth comparing against `reserved_concurrent_executions` is
  accurate); `AWS/ApiGateway`'s `Latency`/`IntegrationLatency`/`5xx` metric names and the `ApiId` dimension
  (`:118,141,159,170`) are the correct HTTP-API-v2 (not REST-API-v1) shapes — HTTP APIs use `ApiId`+optional
  `Stage`/`Route`, not the REST API's `ApiName`/`Stage`/`Method`/`Resource` set, and this file correctly uses
  the former.
- **`API_ID` lookup is hardcoded, not derived from the `function_name` input** (`:148`,
  `Items[?Name=='benzene-mesh-mesh-api']`) — a narrow but real inconsistency: this workflow's `function_name`
  input is user-configurable (default `benzene-mesh-mesh`, described generically as "Lambda function name to
  read logs from"), implying it can target a differently-named deployment, but the API Gateway section always
  looks for `benzene-mesh-mesh-api` regardless. Under this repo's actual single-project deploy workflow
  (`mesh-example-aws-deploy.yml` never exposes `project` as an input, so it is always the `variables.tf`
  default `"benzene-mesh"`) this is unreachable in practice — there is currently no way to end up with a
  differently-named API Gateway to look for. Noting it here rather than filing it as a finding: if a second
  mesh stack under a different `var.project` is ever deployed to the same account (this workflow's own
  `function_name` input already anticipates that use case), this diagnostic would silently report the
  **wrong** stack's API Gateway metrics (or skip with "no matching API found") rather than the target stack's
  — worth a `project`/`api_name` input alongside `function_name` if that scenario becomes real, but not a bug
  in what exists today.
- **`--query` filters that could silently match nothing or too much** — the `[?Sum > `0`]`/`[?Maximum > `0`]`
  filters (`:133,144,173`) are a deliberate "only show periods with activity" narrowing, not a correctness
  bug; an all-zero window legitimately prints nothing, which the surrounding `echo`s make clear rather than
  presenting as an error.
- **Shell-injection surface from `workflow_dispatch` inputs** — every input that reaches a shell command in
  this file is threaded through `env:` indirection (`FUNCTION_NAME`, `FILTER_PATTERN`, `SINCE_MINUTES`,
  `BUCKET`, `PREFIX`, `KEYS_INPUT`), never spliced directly into the YAML `run:` script text — the class of
  vulnerability that would let an input's value execute as *script source* (the severe GitHub Actions
  injection pattern) does not apply here. `SINCE_MINUTES` is used inside a bare bash arithmetic context
  (`$(( ... SINCE_MINUTES * 60 ... ))`, `:76,108,186`) with no numeric validation; I specifically tested
  whether this is exploitable for command injection (bash's arithmetic evaluator recursively re-evaluates a
  variable's string value as an expression, which is a known injection vector in some shells/patterns) —
  empirically, in this bash version (5.2.21), a value like `$(touch /tmp/pwned)` assigned to the variable
  produces a syntax error (`operand expected`) and does **not** execute the embedded command substitution;
  confirmed by checking the target file was never created. So this is at most a robustness issue (a
  non-numeric `since_minutes` input fails the step with a shell error) for a manually-triggered, environment-
  gated diagnostic workflow — not an injection vulnerability, and not filed as one.
- **`deploy/main.tf`/`mesh-example-aws-deploy.yml`'s `mesh_extra_services`/`mesh_allowed_emails` `-var`
  plumbing** (`:199-219`) — empirically verified rather than assumed: installed a scratch Terraform 1.9.8
  binary and ran a real `terraform apply -var 'mesh_extra_services=[{"name":"...","specUrl":"...",
  "healthUrl":"..."}]'` against a minimal config using the exact `list(object({name,specUrl,healthUrl}))`
  type from `variables.tf:62-67` — HCL's native expression syntax genuinely does accept JSON's `key: value`
  object-constructor colon syntax as an alternative to `=` (this is a real, if under-documented, HCL
  compatibility feature, not something I was able to recall with confidence beforehand, hence testing it),
  so the workflow's JSON-shaped `-var` values parse correctly. Both `env:`-indirected values
  (`MESH_EXTRA_SERVICES_INPUT`, `MESH_ALLOWED_EMAILS_INPUT`) are referenced only inside double-quoted bash
  variable expansions (`"$MESH_EXTRA_SERVICES_INPUT"`, `"$emails_json"`), which does not re-interpret the
  expanded content for further shell metacharacters (a value containing `$(...)`/backticks is passed through
  as inert literal text to Terraform, not executed) — correctly safe, and the code's own comment claiming this
  is accurate.

---

## Finding 5 — swept, no finding: `Mesh/Startup.cs`'s `ParseExtraServices()`/`BuildOidcOptions()` and DI wiring,
`MeshAggregateHandler.cs`'s `MeshExtraServicesSeed`

- **`ParseExtraServices()`** (`Startup.cs:306-324`) correctly special-cases blank/unset (`IsNullOrWhiteSpace`)
  as `null` (deliberately distinct from "set but empty," per its own doc comment) and catches exactly
  `JsonException` — verified `MeshRegistryJson.Deserialize` (`src/Benzene.Mesh.Contracts/MeshRegistryJson.cs`)
  only throws `JsonException`-family exceptions on malformed input (plain-property DTOs via
  `JsonSerializer.Deserialize`, no custom converters that could throw something else), so the catch clause is
  not narrower than what can actually be thrown.
- Confirmed the Terraform side (`main.tf:729-735`) ALWAYS sets `MESH_EXTRA_SERVICES` to valid JSON, even for
  the default empty list (`{"services":[]}`), so `ParseExtraServices()`'s `null`-return branch is dead code
  under Terraform-deployed configuration and only reachable when the Lambda is run outside this Terraform
  config — both branches are handled identically downstream (`MeshDiscoveryRunner.DiscoverAsync` treats a
  `null` seed and an empty-`Services` seed the same way), so this asymmetry has no behavioral effect.
- **JSON field-name casing** cross-checked end-to-end: Terraform's `jsonencode({name=..., specUrl=...,
  healthUrl=...})` (`main.tf:730-735`) produces exactly the camelCase keys
  `MeshRegistryJson`'s `JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }`
  (`MeshRegistryJson.cs:15-20`) expects for `RegistryEntryDto.Name`/`SpecUrl`/`HealthUrl` — no
  serialization-mismatch bug.
- **`MeshExtraServicesSeed`'s "own wrapper type" reasoning** (`MeshAggregateHandler.cs:79-89`) is verified
  correct against the actual DI container behavior it describes: Microsoft.Extensions.DependencyInjection does
  resolve a non-enumerable request for a type to the LAST registration regardless of lifetime, so a bare third
  `MeshServiceRegistry` singleton actually would have collided with the scoped dispatch-target registration in
  `Startup.cs:101-108` exactly as the comment claims — this is accurate, not just plausible-sounding prose.
- **`MeshDiscoveryRunner.DiscoverAsync`'s "seed wins on a name clash" contract** (invoked from
  `MeshAggregateHandler.HandleAsync:44`) matches its actual implementation
  (`src/Benzene.Mesh.Contracts/MeshDiscoveryRunner.cs:56-64`, seed entries populate `byName` before any
  provider runs, and providers use `ContainsKey` to avoid overwriting) — the doc comment on both sides is
  accurate to the code, not aspirational.
- No dangling `IDisposable`/unbounded-fan-out/ambient-cancellation gap found in either file — `Startup.cs`'s
  DI registrations are plain constructor/factory registrations with no I/O in the hot path beyond the
  already-reviewed `AddScoped<MeshServiceRegistry>` (S3 read per dispatch request, pre-existing, not part of
  this session's diff), and `MeshAggregateHandler.HandleAsync` correctly runs its S3 write and the aggregation
  pass concurrently via `Task.WhenAll` with no unbounded fan-out (bounded by however many services
  `_aggregator.RunOnceAsync` discovers, itself already reviewed and per-service-timeout-bounded in prior
  rounds — see `Benzene.Mesh.Aggregator/CLAUDE.md`).

---

## Finding 6 — swept, no finding (clean): `benzene-ui`'s feed-error threading (`ServicePage.tsx`,
`ServiceList.tsx`, `TopicList.tsx`, `EdgeList.tsx`)

This is genuinely clean, well-tested code — not a case of stopping early. Checked every specific question the
brief raised:

- **Prop threading is complete and consistent.** `TopicList`/`EdgeList` are the only two components rendering
  the topics/topology feeds' empty states with their own branch; grep confirms `<EdgeList` has exactly one
  call site (`ServicePage.tsx`) and `<TopicList` exactly two (`ServiceList.tsx`, `ServicePage.tsx`) — both
  `TopicList` call sites and the one `EdgeList` call site now pass `feedError`. No missed call site.
- **Interaction with `showUtility`**: none — when the `'topics'` feed fails, `selectTopics`/`selectVisibleServiceTopics`
  resolve to an empty array regardless of `showUtility` (the underlying data is simply absent), so the feed-error
  branch fires identically whether utility topics are shown or hidden; when the feed succeeds, `feedError` is
  `undefined` and the existing `showUtility`-filtered empty-message behavior is unchanged.
- **Interaction with the `UndeclaredService` branch** (`ServicePage.tsx:106-123`): none — that branch `return`s
  before the `TopicList`/`EdgeList` render path is ever reached, so an observed-but-undeclared service (a
  genuinely different kind of "unknown" — no manifest entry, not a feed-read failure) is unaffected by this
  change.
- **`selectVisibleServiceTopics` (`ServiceList`) vs. `selectTopicsForService` (`ServicePage`) — now
  consistent, not divergent.** Both selectors derive from the same `selectTopics`/`s.catalog.topics` root,
  so both collapse to an empty array under an identical failure the same way; `ServiceList.tsx:39` and
  `ServicePage.tsx:47-48` both read `selectFeedErrors` (the exact same selector, same `.find((e) => e.feed
  === 'topics')` pattern already used by the pre-existing `TopicCatalog.tsx:30`) rather than inventing a
  second definition of "feed failed," so the two service-facing surfaces and the estate-wide `TopicCatalog`
  table now report identically for the same underlying 503.
- **Architecture rules obeyed**: `TopicList`/`EdgeList` (in `controls/`) take the new `feedError` as a plain
  prop and never call `useAppSelector`/`useAppDispatch` — verified against the codebase's own enforced rule
  (`src/components/architecture.test.ts`'s `'only containers and pages touch the store'` test, which greps
  `primitives|controls|sections` for store hooks) rather than the slightly-stale prose in `benzene-ui/CLAUDE.md`
  ("containers/ is the only place..." — actually enforced as "containers AND pages," which pre-dates this
  session's diff and every other file under `pages/` already relies on). `ServiceList.tsx`/`ServicePage.tsx`
  are a container and a page respectively — exactly where store access belongs.
- **Actually executed, not just read**: ran `npx tsc --noEmit` (clean, zero errors) and the full `npx vitest
  run` suite in `benzene-ui` — **46 test files, 563 passed, 5 skipped, 0 failed**, including the three new/
  extended test files from this session's commit (`TopicList.test.tsx`, the `EdgeList.test.tsx` additions,
  the `pages.test.tsx` addition) and the full pre-existing suite (nothing this change touches regressed).
- **One minor, non-blocking coverage gap**: `src/components/containers/ServiceList.test.tsx` was **not**
  touched by this session's commit (`0e12cf4`), even though `ServiceList.tsx` itself was — its 6 existing
  tests exercise the container's other behaviors but none of them set a `'topics'` feed error and assert the
  per-card `TopicList`s render the error tone. I traced the wiring by hand (`ServiceList.tsx:39,66,75`) and
  confirmed it is correct — but a future refactor of `ServiceList.tsx` that broke this specific threading
  (e.g. an accidental typo changing `'topics'` to `'topology'` in the `.find()` predicate, or dropping the
  `feedError={topicsFeedError}` prop during an unrelated edit) would not be caught by any test today; it would
  only be caught by `TopicList`'s own unit tests indirectly failing to matter (they test the component in
  isolation, not through `ServiceList`). A container-level test mirroring `pages.test.tsx`'s new case —
  dispatch a `getTopics` failure, render `<ServiceList />`, assert `screen.getAllByText(/could not be
  read/).length > 0` and that `'Consumes nothing.'`/`'Produces nothing.'` are absent — would close this gap.
  Not filed as a bug (current behavior is correct), only as a coverage note per this round's brief.

---

## Summary

| # | Finding | Severity | Status |
|---|---------|----------|--------|
| 1 | Reserved-concurrency fix (1→10) measurably weakens `MeshDispatchGuardOptions`' per-target in-memory rate limit (~30/min → up to ~120/min in practice), an interaction its own long comment never discusses | Medium-high | New, traced by hand + AWS-documented-behavior reasoning |
| 2 | `mesh_lambda_reserved_concurrency` has no `validation` block (unlike its sibling vars); `0` silently disables the entire mesh Lambda | Low-medium | New |
| 3 | `mesh_allowed_emails`'s documented "not sticky" reset has no runtime confirmation in the workflow's own output — a routine redeploy can silently revoke previously-granted access | Low-medium | New (documented behavior, weak safety net) |
| 4 | `mesh-example-aws-logs.yml`'s new CloudWatch/API-Gateway metrics + injection surface | — | Swept, clean (one narrow, currently-unreachable inconsistency noted, not filed) |
| 5 | `Mesh/Startup.cs`/`MeshAggregateHandler.cs` parsing + DI wiring | — | Swept, clean |
| 6 | `benzene-ui` feed-error threading (`ServicePage`/`ServiceList`/`TopicList`/`EdgeList`) | — | Swept, clean; full test suite run and passing; one minor test-coverage gap noted |

**Overall assessment: safe as deployed, with one real gap worth a maintainer's attention before the next AWS
mesh demo redeploy.** Findings 2 and 3 are cheap, low-risk fixes (a `validation` block; an echoed
pre-apply confirmation) that a maintainer could apply directly without a design review. Finding 1 is the one
genuinely worth flagging up before relying on `mesh_dispatch_max_per_target_per_minute` as a precise
per-target guarantee at the new concurrency ceiling — it does not make the estate unsafe (the API Gateway edge
throttle still bounds total dispatch throughput to a moderate ceiling), but the two layers are no longer sized
consistently with each other, and nothing in the code says so. The `benzene-ui` half of this session's changes
is unambiguously solid: traced by hand, type-checked, and the full test suite (563 tests) was actually run and
passes with the new behavior included.
