# Contract Testing (catching breaking changes before they reach consumers)

Benzene services talk to each other by topic + payload, and consumers often use a CodeGen-generated
strongly-typed client. That makes it possible to catch a provider changing its contract in a way
that breaks its consumers — two complementary mechanisms, one at **runtime** and one at **build/CI
time**.

## Problem statement

A service evolves its message contract (a handler's request/response shape, or the set of topics it
answers). Some changes are safe (adding an optional response field); some break consumers (removing
a response field, adding a required request field). You want to know **before** the change ships,
not from a production incident.

## Mechanism 1 — runtime contract-drift check

Every generated client bakes in a hash of the contract it was built against
(`{Service}ServiceClient.HashCode`), which can be compared against the provider's *current* contract
hash by calling the provider's health check. The provider publishes that hash from a schema health
check.

**Provider** — register the schema health check (`Benzene.HealthChecks.Schema`):

```csharp
using Benzene.HealthChecks.Schema;

app.UseHealthCheck("get", "healthcheck", health => health
    .AddSchemaHealthCheck()          // publishes the live contract hash as a "schema" health check
    .AddHealthCheck("live", _ => true));
```

**Consumer** — register the library's downstream check and hand it the generated client's `HashCode`
(`Benzene.Clients.HealthChecks`):

```csharp
using Benzene.Clients.HealthChecks;
using Benzene.HealthChecks;

app.UseContractsCheck(x => x
    .AddServiceCheck("Payments", new PaymentsServiceClient(sender).HashCode));
```

`AddServiceCheck` builds a `ServiceHealthCheckClient`: it sends `benzene:healthcheck`
(`BenzeneTopic.HealthCheck`) to the provider over `IBenzeneMessageSender` and runs the answer through
`ClientHealthCheckProcessor` against the hash you supplied. A mismatch means the provider's contract
has drifted from what your client was generated against. Both ends hash with the same
`CodeGenHelpers.GenerateHash`, so the hashes are directly comparable. Reachable + matching is `Ok`,
reachable + drifted is `Warning` (degraded, not fatal), unreachable is `Failed`.

Two things to know:

- **The hash is optional.** `AddServiceCheck("Payments")` with no hash is a pure reachability check —
  it reports no drift verdict at all rather than a false one.
- **You must register an outbound route for `benzene:healthcheck`**, pointed at the provider you want
  to probe, over a transport that can actually answer (a fire-and-forget queue cannot). That opt-in is
  the point: a generated client covers domain topics only and never demands this route — see
  [Client SDKs](../client-sdks.md#generated-clients-cover-domain-topics-only).

Nothing needs generating or hand-writing for this: the health-check payload is standard and known up
front, unlike the domain payloads that generated clients exist for. Hand-writing an `IHasHealthCheck`
and registering it with `AddContractCheck<TClient>(...)` is still supported for the unusual case where
the standard call isn't what you want — `examples/Mesh/Benzene.Examples.Mesh.OrdersService/Clients/PaymentsContractClient.cs`
does it to fake the call with canned data in a demo.

This is reactive — it tells you drift has already happened. For a pre-merge stop, use mechanism 2.

> **Wire the contract check to monitoring, not to a Kubernetes probe.** It calls a *downstream*
> service and reports contract drift, so it belongs on the dedicated, probe-less **`contracts`**
> diagnostic topic the mesh / alerting consume — **not** in a liveness or readiness probe. Coupling
> it to a probe lets a struggling dependency (or a compatible-but-changed contract) restart or
> de-route pods that are themselves healthy. That's what `UseContractsCheck` above is for. See
> [Kubernetes Health Checks — client/contract-drift checks belong in neither probe](../kubernetes-health-checks.md#client--contract-drift-checks-belong-in-neither-probe).

## Mechanism 2 — CI compatibility gate

`SchemaCompatibility.EnsureBackwardCompatible(...)` compares the current contract against a committed
baseline and **throws on breaking changes** (while allowing additive ones), so a plain test fails
the build.

### 1. Commit a baseline spec

Generate the service's contract and commit it as `spec.baseline.json` (the OpenAPI/event-service
document — the same one CodeGen and the schema health check hash). Regenerate it deliberately
whenever you *intend* a breaking change.

### 2. Add a gate test

```csharp
using Benzene.Schema.OpenApi.Compatibility;
using Benzene.Schema.OpenApi.EventService;

[Fact]
public void Contract_IsBackwardCompatibleWithBaseline()
{
    var baselineJson = File.ReadAllText("spec.baseline.json");

    // The current contract, built from this service's handler definitions. Get them however your
    // app exposes them - e.g. resolve IMessageHandlerDefinitionLookUp and call GetAllHandlers(),
    // the same source the provider schema health check uses.
    var current = lookUp.GetAllHandlers().ToEventServiceDocument();

    // Throws SchemaCompatibilityException (failing the test) on any breaking change.
    SchemaCompatibility.EnsureBackwardCompatible(baselineJson, current);
}
```

`EnsureBackwardCompatible` returns the report (additive changes + warnings) when compatible, and
throws with a message listing every breaking change when not. Overloads accept two
`EventServiceDocument`s, `(baselineJson, current)`, or `(baselineJson, currentJson)`.

### 3. What counts as breaking

The default rules are direction-aware (see `SchemaCompatibilityRules.DefaultFor`):

| Change | Request | Response |
|---|---|---|
| Topic added | compatible | — |
| Topic removed | **breaking** | **breaking** |
| Optional property added | compatible | compatible |
| Required property added | **breaking** | compatible |
| Property removed | warning | **breaking** |
| Type changed | **breaking** | **breaking** |

Pass `SchemaCompatibilityRules.Strict()` to treat every non-compatible change as breaking, or
`.Set(kind, direction, compatibility)` to override individual rules.

## Further reading

- [Client SDKs](../client-sdks.md) — the generated typed clients that bake in the contract hash.
- [Health Checks](../health-checks.md) — registering the provider health check.
