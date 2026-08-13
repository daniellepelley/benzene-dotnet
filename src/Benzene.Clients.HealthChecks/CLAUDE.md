# Benzene.Clients.HealthChecks

## What this package does
The **consumer side** of downstream health checking: it calls another service's health check and turns
the answer into an `IHealthCheck` result — reachable or not, and (optionally) whether that service's
message contract has drifted from the one this consumer expects. Calling a downstream's health check is
a health-check concern, exactly like pinging a database or an SQS queue, so it lives here rather than in
generated client code: the health-check payload is **standard and known up front** (fixed by the
libraries — `Benzene.Abstractions.Results.Void` in, `HealthCheckResponse` out), unlike domain payloads,
which differ per service and are why domain clients are generated at all. The provider side that
publishes the contract hash is `Benzene.HealthChecks.Schema`.

**CodeGen-generated clients do NOT implement `IHasHealthCheck`** — they cover a service's *domain*
topics only, never Benzene's reserved `benzene:*` endpoints. They do still expose a `HashCode` property,
which is what you hand to `AddServiceCheck(...)` when you want drift reporting.

## Key types/interfaces
- `ServiceHealthCheckClient` - **the built-in downstream health call**, and the reason no client type is
  needed for one. Sends `Benzene.Abstractions.BenzeneTopic.HealthCheck` (`benzene:healthcheck`) with a
  `Void` request over `IBenzeneMessageSender` (`Benzene.Clients`), expecting a `HealthCheckResponse`. Its
  expected contract hash is **optional**: supplied → the answer is drift-annotated through
  `ClientHealthCheckProcessor` (reachability + drift); omitted → the answer is passed straight through
  un-annotated (reachability only), so nothing manufactures a false-drift `ClientHashMatch` against an
  empty hash.
  Because it sends `benzene:healthcheck`, the consumer **must register an outbound route for that
  topic** — an explicit opt-in per dependency, rather than something every consumer of a generated client
  is forced into (which is what it used to be, and why generated clients no longer carry a health check).
- `ClientHealthCheckProcessor.Process(IHealthCheckResponse<HealthCheckResult>, string hashCode)` -
  static. Finds the provider's `"schema"`-typed health check (via
  `SchemaHealthCheckConstants.Type`), reads its published hash out of `Data`, compares it with
  `hashCode` (the hash the consumer expects), and writes a `ClientHashMatch` into the
  schema check's `Data`. If there is no schema check to compare against, it passes the response through
  unchanged. The published hash is normalized with `ToString()`, so it works whether it arrives as a
  plain string, a System.Text.Json `JsonElement`, or a Newtonsoft `JToken`.
- `ClientHashMatch` - the verdict: `ServiceHashCode`, `ClientHashCode`, `IsMatch`.
- `IHasHealthCheck` - `HashCode` + `Task<IBenzeneResult<HealthCheckResponse>> HealthCheckAsync()`, the
  seam `ClientHealthCheck` sits on. `ServiceHealthCheckClient` is the built-in implementation; a
  hand-written one is only needed when the standard call isn't what you want (a bespoke transport, a
  canned response in a demo — see the Mesh example's `PaymentsContractClient`).
- `ClientHealthCheck` - an `IHealthCheck` adapter over one `IHasHealthCheck` + a
  service name. It calls `HealthCheckAsync()` (whose response is already drift-annotated by
  `ClientHealthCheckProcessor`) and folds that aggregated response into one check result: reachable +
  matching contract -> `Ok`, reachable + drift -> `Warning` (degraded-not-fatal, does not flip
  `IsHealthy`), unreachable/throws -> `Failed`; attaches a `HealthCheckDependency("Service", name)`.
  It tracks the *contract* relationship, not the provider's transient internal health (that's the
  provider's own readiness concern).
- `ContractHealthCheckExtensions` -
  `AddServiceCheck(serviceName, expectedContractHash?)` (the ordinary case: resolves
  `IBenzeneMessageSender` from the container and wraps it in a `ServiceHealthCheckClient`),
  `AddContractCheck<TClient>(serviceName)` (resolves a hand-written `IHasHealthCheck` client from DI) and
  `AddContractCheck(serviceName, client)` (explicit instance) all register a `ClientHealthCheck` on a
  health-check builder.

## Wiring: the `contracts` topic, NEVER a probe
Register these checks on the dedicated **`contracts`** diagnostic topic via
`UseContractsCheck(x => x.AddServiceCheck("OrderService", new OrderServiceClient(sender).HashCode))`
(`UseContractsCheck` lives in `Benzene.HealthChecks`; `Constants.DefaultContractsTopic = "contracts"`).
This topic is deliberately kept off the Kubernetes liveness/readiness probes: the check calls a
downstream service and reports drift, so putting it in a probe would let one struggling dependency (or
a compatible-but-changed contract) restart or de-route otherwise-healthy pods. Feed it to monitoring /
the mesh instead. See `docs/kubernetes-health-checks.md` and `work/client-health-checks-design.md`.

## When to use this package
- On a consumer service that wants to report a downstream provider's reachability, and optionally
  whether that provider's contract has drifted from what this consumer was built against.

## Dependencies on other Benzene packages
- **Benzene.Clients** - `IBenzeneMessageSender`, client abstractions
- **Benzene.HealthChecks** - `HealthCheckResult`/`HealthCheckResponse`, `SchemaHealthCheckConstants`
- **Benzene.Results** - `IBenzeneResult`

## Important conventions
- Never type the `benzene:healthcheck` literal - use `Benzene.Abstractions.BenzeneTopic.HealthCheck`.
- `ClientHealthCheckProcessor` is a pure comparison/annotation step - no network I/O, no endpoint URL,
  no timeout in it. Only `ServiceHealthCheckClient` sends anything, and it sends through the consumer's
  own registered outbound route.
- The `"schema"` type + `Data`-key strings are shared with the provider via
  `SchemaHealthCheckConstants` so the two halves can't drift on a literal.
