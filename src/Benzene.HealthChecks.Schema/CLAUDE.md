# Benzene.HealthChecks.Schema

## What this package does
The **provider side** of Benzene's contract-drift check: `SchemaHealthCheck` hashes a service's
current message contract (every registered handler's topic + request/response schema) and publishes
the hash as a `"schema"`-typed health check, so consumers can detect when the contract has drifted
from what their generated client was built against. This revives the (long-commented-out) legacy
`MessageSchemaHealthCheck` and completes the loop the current repo only had the consumer half of.

## The contract-drift loop
```
provider: SchemaHealthCheck  ──"schema" health check {hashCode}──▶  consumer: ClientHealthCheckProcessor
            (this package)                                            (Benzene.Clients.HealthChecks)
                 ▲                                                              ▲
   ContractHash.Compute(live handlers)                 {Service}ServiceClient.HashCode (baked at codegen)
                 └──────────────── same hash function ─────────────────────────┘
```
- **`SchemaHealthCheck`** resolves `IMessageHandlerDefinitionLookUp`, calls
  `ContractHash.Compute(GetAllHandlers())` (`Benzene.CodeGen.Core`), and returns a health
  result of `Type = "schema"` with the hash under `Data["hashCode"]`.
- **Crucially**, it uses the *same* `ContractHash` (the spec-pinned `contractHash`,
  `contract-document.md` §6 in the cross-language Benzene repo) that `Benzene.CodeGen.Client` bakes
  into a generated `{Service}ServiceClient.HashCode`. It hashes **every** registered handler
  (reserved `benzene:*` topics included) rather than pre-filtering them, because `ContractHash`
  itself applies §5.1's domain-projection rule (reserved entries excluded from a whole-service hash)
  - the same rule `MessageClientSdkBuilder`'s `TopicScope` applies before a default client is even
  generated. Pre-filtering here would double-apply the rule and risk drifting from it; passing every
  handler through and letting `ContractHash` project is what keeps the two sides honest. So the live
  provider hash and the consumer's baked-in hash are directly comparable - equal means no drift,
  different means the contract changed. (Before this alignment, the provider hashed every handler
  under the *old*, non-projecting algorithm while a default client's hash was already domain-scoped
  before hashing - the two could never match; see `work/archive/cross-language-clients-plan-2026-08.md` Phase 2.)
- The wire contract (the `Type` and `Data` key strings) lives in
  `Benzene.HealthChecks.Core.SchemaHealthCheckConstants`, referenced by both this package and the
  consumer-side processor so they can't drift on a literal.

## Key types
- `SchemaHealthCheck : IHealthCheck` - the provider health check.
- `SchemaHealthCheckExtensions.AddSchemaHealthCheck(this IHealthCheckBuilder)` - registration;
  resolves the handler lookup from DI when the check runs.

## When to use
- On any Benzene service whose consumers use CodeGen-generated typed clients, so a consumer's
  health check turns to a mismatch verdict the moment the provider's contract drifts.

## Dependencies on other Benzene packages
- **Benzene.Abstractions** - `IMessageHandlerDefinitionLookUp`, DI `IServiceResolver`.
- **Benzene.CodeGen.Core** - `ContractHash.Compute` (the canonical, spec-pinned contract hash;
  lightweight, only pulls in `Benzene.Schema.OpenApi` + `System.Text.Json`, no Roslyn). Not
  `CodeGenHelpers.GenerateHash` - that older HMAC-SHA256-over-Microsoft.OpenApi-JSON algorithm is
  unchanged but no longer used here; it lives on only because `Benzene.Mesh.Contracts.MeshHashing`
  deliberately mirrors it for a different, .NET-internal hash (mesh.md §9).
- **Benzene.HealthChecks.Core** - `IHealthCheck`, `HealthCheckResult`, `SchemaHealthCheckConstants`.

## Conventions / notes
- The hash is published as a **plain string** under `Data["hashCode"]` (not a nested object) so it
  survives any JSON round-trip; the consumer reads it via `ToString()` rather than `dynamic`, so it's
  robust whether the value arrives as a string, a System.Text.Json `JsonElement`, or a Newtonsoft
  `JToken`. (The old consumer processor used `dynamic Data["data"].hashCode`, which only worked under
  Newtonsoft - hardened as part of this change.)
- Runtime hashing goes through the same `EventServiceDocument` normalization `ContractHash` uses
  (strips generated examples, `messageEndpoint`, `transports`, and - for this whole-service call -
  every reserved entry entirely), so a service upgrade doesn't trip a false mismatch - see
  `ContractHash`'s doc comment (`Benzene.CodeGen.Core`).
- Test coverage: `test/Benzene.Core.Test/HealthChecks/SchemaHealthCheckTest.cs` (canonical-hash +
  end-to-end match/drift) and `test/Benzene.Core.Test/Clients/ClientHealthCheckProcessorTest.cs`
  (processor robustness incl. the JsonElement wire-round-trip case).
- **Not yet built (A.2b):** a CI-time breaking-vs-additive gate on top of the existing
  `Benzene.Schema.OpenApi.Compatibility.SchemaCompatibility` comparer - this package is the runtime
  half only. See `work/enterprise-adoption-gap-analysis.md` A.2.
