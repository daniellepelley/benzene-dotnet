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
   served contract document's contractHash             {Service}ServiceClient.HashCode (baked at codegen)
                 └──────── the same value for a default (no include-list) client ─────────┘
```
- **`SchemaHealthCheck`** reads the contract document the service **serves** at `benzene:spec`
  (from the process's `SpecCache` when `UseSpec` registered one, else built the same way
  `SpecBuilder` builds it) and publishes its `contractHash` - a health result of `Type = "schema"`
  with the hash under `Data["hashCode"]`. It never computes a private hash of its own
  (contract-document.md §6.1; spec 3a removed every provider-side hash).
- The served `contractHash` covers the document's `requests` and `events` (dereferenced, normalized -
  `Benzene.Schema.OpenApi`'s `ContractHash`), so a consumer's generated client generated without an
  include-list embeds exactly this value: equal means no drift, different means the contract changed.
  An include-list or per-topic client embeds the hash of its own projection (§6.4), which a drift
  check must compare against the same projection of the provider's document.
- The wire contract (the `Type` and `Data` key strings) lives in
  `Benzene.HealthChecks.Core.SchemaHealthCheckConstants`, referenced by both this package and the
  consumer-side processor so they can't drift on a literal.

## Key types
- `SchemaHealthCheck : IHealthCheck` - the provider health check.
- `SchemaHealthCheckExtensions.AddSchemaHealthCheck(this IHealthCheckBuilder)` - registration;
  reads the served document through the check's resolver when it runs. `new SchemaHealthCheck(() => json)`
  takes an already-served document.

## When to use
- On any Benzene service whose consumers use CodeGen-generated typed clients, so a consumer's
  health check turns to a mismatch verdict the moment the provider's contract drifts.

## Dependencies on other Benzene packages
- **Benzene.Abstractions** - `IMessageHandlerDefinitionLookUp`, DI `IServiceResolver`.
- **Benzene.Schema.OpenApi** - `SpecBuilder`/`SpecCache` (the served document) - which also carries
  the contract hash now, so a deployed service no longer ships the code generator.
- **Benzene.HealthChecks.Core** - `IHealthCheck`, `HealthCheckResult`, `SchemaHealthCheckConstants`.

## Conventions / notes
- The hash is published as a **plain string** under `Data["hashCode"]` (not a nested object) so it
  survives any JSON round-trip; the consumer reads it via `ToString()` rather than `dynamic`, so it's
  robust whether the value arrives as a string, a System.Text.Json `JsonElement`, or a Newtonsoft
  `JToken`. (The old consumer processor used `dynamic Data["data"].hashCode`, which only worked under
  Newtonsoft - hardened as part of this change.)
- Test coverage: `test/Benzene.Core.Test/HealthChecks/SchemaHealthCheckTest.cs` (served-hash +
  end-to-end match/drift) and `test/Benzene.Core.Test/Clients/ClientHealthCheckProcessorTest.cs`
  (processor robustness incl. the JsonElement wire-round-trip case).
- **Not yet built (A.2b):** a CI-time breaking-vs-additive gate on top of the existing
  `Benzene.Schema.OpenApi.Compatibility.SchemaCompatibility` comparer - this package is the runtime
  half only. See `work/enterprise-adoption-gap-analysis.md` A.2.
