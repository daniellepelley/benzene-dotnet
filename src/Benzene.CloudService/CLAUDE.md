# Benzene.CloudService

## What this package does
The batteries-included setup for a **Benzene Cloud Service** — the code-level counterpart of
`docs/specification/cloud-service-profile.md`. One call, `UseBenzeneCloudService("name", ...)`,
on a hosted HTTP pipeline wires every operational surface the profile requires (R1–R5, R7) at the
default service standard paths - the `/benzene/invoke` envelope endpoint, `benzene:spec` +
`/benzene/spec`, `/benzene/health` + reserved `healthcheck` topic - and, given a collector, the
meshed-service claim's feeds (mesh.md §7.1: M1 the trace feed, collector registration +
heartbeats; M2 the inbound trace join) - the registration's body being the contract document `benzene:spec` serves (mesh.md §2,
spec-0.4.0; the `benzene:mesh` descriptor topic is gone). It is deliberately **syntactic sugar** over the
same pipeline builders Benzene Core setup uses — no new capability lives here, only the profile's
steers pre-wired in the right order. Manual composition of the underlying `Use*` calls (Benzene
Core setup) remains the full-control path and this package must never be required for it.

## Key types
- `Extensions.UseBenzeneCloudService<TContext>(serviceName, configure?)` — the entry point;
  `TContext : IHttpContext`, so it works on any HTTP transport (ASP.NET, API Gateway, self-host).
  Terminal: it ends with the message router; app HTTP middleware goes *before* the call.
- `ICloudServiceBuilder` / `CloudServiceBuilder` — the configuration surface. Every setting has a
  profile-conformant default; overrides are always honored and honestly reflected in the report
  (`WithoutMesh()`, relocated paths → flagged, never refused).
- `CloudServiceProfileReport` / `CloudServiceRequirement` — the wiring-time self-assessment
  against the profile's R1–R5 and R7 (spec-0.5.0; R6 and R8 left the profile and their ids are not
  reused) - `Requirements`/`Missing`/`IsConformant` - and, apart, the meshed-service claim's M1 (feeds:
  registration, heartbeats, traces - formerly R6) and M2 (trace join and propagation - formerly R8,
  keeping #167's resolved-trace-exporter rule) - `MeshRequirements`/`MeshMissing`/`IsMeshed`, where
  `IsMeshed` requires `IsConformant` (a meshed service is a Cloud Service first). Registered in DI, and
  stamped on the contract document as contract-document.md §1's `profile` field (`ToProfile()` →
  `ContractDocumentProfile`, outside both hashes; `missing` from R1–R5/R7 only, M1/M2 **never**)
  through the `ContractDocumentDeployment` the builder registers (`runtime: dotnet`, `placement` -
  default `self-hosted` - and `profile`). R5 is "answers `benzene:spec` wherever the transport carries
  a reply, and `/benzene/spec`". M2 reflects both halves: the inbound join (`UseMeshTrace`, wired
  with a collector or trace exporter) **and** outbound forwarding of the mesh span - satisfied only when
  the container's outbound routing forwards `MeshSpan.Current` on every route
  (`routing.UseMeshTraceContext()`, which registers `MeshTraceContextForwarding`) or has no outbound
  routes at all; outbound routes without it (e.g. only `UseW3CTraceContext()`, which stamps OpenTelemetry's
  `Activity.Current`) leave M2 unsatisfied with a note saying what to wire. Read from the registrations
  made before `UseBenzeneCloudService` (the hosts' services-then-pipeline order). It reports *provisioning*, not runtime state — runtime degradation is not a
  conformance failure (profile §4).
- `CloudServiceDocumentSource` — the registration body. `Get(resolver)` derives the complete
  contract document through `SpecBuilder` and the same `SpecCache` entry `benzene:spec` serves from, so
  the registered and the served document are byte-identical. With `WithHandlers` a **wire-up** document
  also exists (`TryGetWireUp`): `requests` from those types, `events`/`sends` named in `degraded` (their
  declarations live in the container wire-up can't see) - so registration can start before any traffic.
- `MeshAnnouncer` — the outbound mesh feeds (mesh.md §2, §4–§5): register-with-retry then heartbeats
  carrying `serviceVersion` and the registered document's `registrationHash` (health via
  `HealthCheckProcessor`). Every invocation offers the complete document until one is in hand; a
  document differing from the registered one is re-registered at once (a semaphore wakes the loop),
  so the wire-up document is replaced by the complete one on the first invocation. `IAsyncDisposable` **and**
  `IDisposable` (bounded sync bridge, like `HttpMeshTraceExporter`) so a synchronously-disposed
  container can stop the announce loop without MS DI throwing on an async-only-disposable.
- **Mesh singleton disposal.** `MeshAnnouncer` and the (framework-created or `WithTraceExporter`-
  supplied) `IMeshTraceExporter` are registered as singletons **and resolved once** by a
  `RealizeMeshDisposables` middleware on the first invocation of either pipeline — a factory-
  registered singleton that nothing resolves is never tracked for disposal by MS DI, so without the
  resolve their `DisposeAsync` (announce-loop stop / trace tail-flush) never runs on shutdown. This
  disposes them on any host that disposes its provider (ASP.NET Core, the generic host). A host whose
  provider is never disposed — e.g. a short-lived Lambda container, where `MicrosoftServiceResolverFactory`
  owns the provider but its `Dispose()` is a no-op — still can't dispose them; that provider-ownership
  gap is tracked separately in `work/audit-remaining-suggestions.md` item 1. Covered by
  `test/Benzene.Core.Test/CloudService/MeshDisposalTest.cs`.
- `CloudServicePaths` — the `/benzene/*` default-standard path constants.
- **Contract-document start-up check, on by default (spec-0.5.0).** `UseBenzeneCloudService` registers
  `AddContractDocumentStartUpCheck()`, so a component-name collision (contract-document.md §4.1 rule 4)
  or an event/send conflict fails the host before the first message. `WithoutContractDocumentStartUpCheck()`
  opts out where the cold-start cost of deriving every schema matters (AWS Lambda); build-time
  derivation (`benzene-descriptor`) catches the same errors. Covered by
  `test/Benzene.Core.Test/CloudService/CloudServiceContractDocumentStartUpCheckTest.cs`.

## Important conventions
- **The logical service name is the application name (2026-07-24).** `UseBenzeneCloudService` calls
  `SetApplicationInfo(serviceName, version, "", versionScheme)`, so `IApplicationInfo.Name` = the same
  string that feeds the contract document (and so the registration), the mesh feeds (`info.Service`), the log scope, and the `benzene.service` span attribute
  (`Benzene.Diagnostics`, so a trace-store mesh reader labels a flow with the real service name instead of
  the backend's segment name — the `orders-api → ApiGatewayLambdaHandler` fix). One source of truth, three
  surfaces; before this, `IApplicationInfo.Name` was `""` on a cloud service.
- **Spec §6 degradation is law here too**: the announcer and trace exporter swallow every
  failure; no mesh feed may ever fail, slow, or block an invocation.
- **Two pipelines, one registry.** The envelope pipeline (`/benzene/invoke`) and the outer HTTP
  pipeline both route through the same handler definitions; health checks are wired on both, the
  mesh trace only on the envelope pipeline (service-to-service mesh traffic speaks
  the envelope — see the Mesh examples).
- Pipeline order inside the envelope surface: announce-start → trace (outermost, sees everything)
  → health interception → app middleware (`WithMiddleware`) → router (which answers `benzene:spec`).
- Adding a profile requirement means updating `CloudServiceProfileReport.Evaluate`, the profile
  spec doc, and this wiring together — the report must never claim what the builder didn't wire.

## Dependencies on other Benzene packages
Http (envelope endpoint, routes, `IHttpContext`), HealthChecks + HealthChecks.Core,
Schema.OpenApi (spec handler and the contract document, which is also the register body), Mesh.Wire (trace/heartbeat wire layer, send declarations),
Core.MessageHandlers / Core.Messages / Core.Middleware.
