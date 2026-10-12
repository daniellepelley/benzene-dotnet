# Benzene.Mesh.Wire

## What this package does
The spec-conformant mesh **wire layer** of `docs/specification/mesh.md` - what makes a .NET
Benzene service a citizen of a cross-language mesh fleet: the trace middleware emitting semantic
TraceEvents (§3), W3C traceparent join and `MeshSpan.Current` propagation, the batching HTTP trace
exporter with the §4 sender rules, the issue feed (§4.1), the heartbeat shape (§5), the collector's
view of a registration (§2), and send declarations (`MeshOutboundRegistry`). Verified against the
language-neutral fixtures by `test/Benzene.Conformance.Test`'s `MeshTraceConformanceTest` (and,
through `Benzene.Mesh.Collector`, the collector fixtures).

**spec-0.4.0 (Phase 3b): there is no mesh self-description.** A service describes itself once, with
its contract document at `benzene:spec` (`Benzene.Schema.OpenApi`), and registers that same document
with a collector (`Benzene.CloudService`'s announcer). `MeshServiceDescriptor`, `MeshTopicDescriptor`,
`MeshDescriptorFactory`, `MeshDescriptorHashing` (`descriptorHash`), `MeshSchemaGenerator`,
`UseMeshDescriptor`, `MeshTopics.Descriptor`/`BenzeneTopic.Mesh` (`benzene:mesh`) and
`IMeshOutboundDefinitionLookUp` were deleted; `MeshServiceInfo` lost `binding`/`placement` (they live
on the document).

Distinct from the pre-existing `Benzene.Mesh.Contracts`/`Aggregator`/`Reporting` visibility
pipeline: this package is the *wire contract*; the aggregator remains the pull-based collector idiom.

## Key types/interfaces
- `MeshRegistration` / `MeshRegisteredEntry` / `MeshPlacement` / `MeshProfile` - the body of
  `benzene:mesh:register` **as a collector reads it** (mesh.md §2): the contract document. Only
  `service` is required; `requests`/`events`/`sends` are null when absent; `IsKnown(member)` is false
  for an absent member or one `degraded` names (unknown: no edges, no drift); `KnownEntries(member)`
  drops `benzene:` entries; unmodelled members round-trip through `[JsonExtensionData] Extra`. A
  service never builds one - it serializes its `EventServiceDocument`.
- `MeshOutboundRegistry` - the service's **send declarations** (contract-document.md §3.2): explicit
  `(topic, version, request, response)` records it may send to topics others define, registered as an
  `IMessageDefinitionFinder<IMessageSenderDefinition>` by `Extensions.AddSendDeclarations` (or the cloud
  service's `WithSends`). `Register<TRequest>` is one-way (no `response`). Generated clients register
  their own finder; `SpecBuilder` reads every one.
- `MeshServiceInfo(service, serviceVersion?, instanceId?)` - the identity stamped on traces, issues and
  heartbeats.
- `Extensions.UseMeshTrace(info, exporter, statusReader)` - wire it
  **outermost**; per-invocation TraceEvent with traceparent join, `MeshSpan.Current` set across
  `next()` (carrying the joined inbound `tracestate` as `MeshSpan.TraceState`), status read back via
  `IMeshStatusReader<TContext>` (BenzeneMessage reader ships here; other transports add their own,
  following the `IMessageGetter<TContext>` mapper idiom).
- **Outbound forwarding (mesh.md §3 / §7.1 M2) - `UseMeshTraceContext()`.** `MeshTraceContextMiddleware`
  stamps `MeshSpan.Current`'s `traceparent` (the span the invocation's TraceEvent records) and
  `tracestate` onto an outbound route's headers and pins them (`Benzene.Clients.TraceContext.OutboundTraceContext`)
  so `UseW3CTraceContext()` - OpenTelemetry's `Activity.Current`, a different span - cannot overwrite
  them in either order; no mesh span → nothing stamped. `routing.UseMeshTraceContext()` on the
  `OutboundRoutingBuilder` puts it on **every** route (`UseOnEveryRoute`, sends and published events
  alike) and registers `MeshTraceContextForwarding`, which the Cloud Service's M2 self-check reads; the
  route-level `pipeline.UseMeshTraceContext()` is for a single route. Keep `UseW3CTraceContext()` for an
  app that runs OpenTelemetry without the mesh. This package references `Benzene.Clients` for it.
  Tests: `test/Benzene.Core.Test/Clients/MeshTraceContextForwardingTest.cs` (downstream TraceEvent
  `parentSpanId` == upstream TraceEvent `spanId`, for a send, a published event, and with OpenTelemetry
  also forwarding).
- `HttpMeshTraceExporter` - bounded channel (DropWrite), batches to a collector's envelope
  endpoint as `mesh:traces`. Lossy by design in every failure mode (§4); `DisposeAsync` flushes
  the tail and is idempotent. Implements **both** `IAsyncDisposable` and `IDisposable`: MS DI's
  synchronous `ServiceProvider.Dispose()` throws on an async-only-disposable, so the sync `Dispose()`
  bridges to `DisposeAsync` with a bounded wait (an unreachable collector must not hang shutdown -
  lossy by design, so an abandoned overlong tail-flush is fine). See
  `test/Benzene.Core.Test/CloudService/MeshDisposalTest.cs`.
- `MeshTopics` / `MeshTraceEvent` / `MeshTraceBatch` / `MeshHeartbeat` - the wire shapes.
  `MeshHeartbeat` is `{service, serviceVersion?, instanceId, registrationHash, sentAt, health}`
  (mesh.md §5; `registrationHash` is the registered document's, `serviceVersion` scopes the
  collector's comparison); `Health` reuses `HealthChecks.Core.HealthCheckResponse` as-is.
  `MeshTraceEvent.ExceptionType` (2026-07-25, spec §3 **optional/additive**, null-omitted): the thrown
  exception's type name when the failure was exception-originated — type only, never message/stack.
  The .NET *span* pipeline stamps it as `benzene.exception.type` (see `Benzene.Diagnostics`) and the
  trace-store mappers (`Benzene.Mesh.Fleet.*`) read it back. The push-plane `UseMeshTrace` populates it
  too (gap closed with the issue feed, same day): it reads the scoped
  `Benzene.Core.MessageHandlers.MessageErrorState` — written by `MessageHandler`'s catch sites when a
  thrown exception is converted into a result — in its post-`next()` finally.
- **The issue feed (2026-07-25, spec §4.1 — drains-up 3.2).** `MeshIssue`/`MeshIssueBatch` (the wire
  shapes; batch-level `service` REQUIRED — an empty batch is the feed's liveness assertion),
  `MeshIssueClassification` (the closed vocabulary + the normative `Classify(status, exceptionType)`
  precedence table — validation statuses first, then exception-type-present, config-wiring,
  dependency, `unclassified` fallback; `contract-drift` is reserved, never emitter-produced),
  `MeshIssueFingerprint.Compute` (the normative recipe: first 16 bytes of SHA-256 over
  `service|topic|version|classification|discriminator`, lowercase hex; transport excluded),
  `IMeshIssueExporter`/`MeshIssueOccurrence` (per-occurrence, dedup lives in the exporter),
  `HttpMeshIssueExporter` (bounded accumulator — 256 fingerprints, drop-new; newest-3 exemplars;
  30s interval flush **including empty liveness batches**; DELTA counts per flush; same lossy/dispose
  rules as the trace exporter), and `UseMeshIssues(info, exporter, statusReader)` — wire it
  immediately INSIDE `UseMeshTrace` (trace outermost) so `MeshSpan.Current` provides the exemplar
  trace id. Null exporter = pass-through; no statusReader → only propagating exceptions report; the
  OCE drain guard prevents phantom deploy-time issues; success path is one memoized status read + a
  set test. Tests: `test/Benzene.Mesh.Test/Wire/MeshIssuesTest.cs` (incl. the end-to-end
  converted-exception → both-feeds proof), `test/Benzene.Conformance.Test/MeshIssueConformanceTest.cs`
  over `conformance/mesh-issue-cases.json`. **Go reference parity: pending** (named deferral).

## Important conventions
- **The spec wins.** These shapes are pinned by `docs/specification/conformance/mesh-*.json`;
  changing them means changing the spec + fixtures first.
- **Degradation is normative (spec §6)**: no mesh feed may ever fail, slow, or block the
  invocation it observes. The trace middleware swallows exporter exceptions; the exporter drops
  on full buffer and failed sends; a missing status reader yields an empty status.
- Wire JSON is camelCase with nulls omitted - always serialize through `MeshJson.Options`.

## Dependencies on other Benzene packages
Abstractions.MessageHandlers (definitions lookup, mappers), Core.MessageHandlers /
Core.Middleware (interception idiom), Core.Messages (BenzeneMessage status reader),
HealthChecks.Core (heartbeat health shape), Results.

## Tests
- `test/Benzene.Conformance.Test/MeshTraceConformanceTest.cs` - the language-neutral fixture-driven
  conformance suite for the trace feed; the collector fixtures exercise `MeshRegistration` /
  `MeshHeartbeat` through `Benzene.Mesh.Collector`.
- `test/Benzene.Mesh.Test/Wire/ExtensionsTest.cs` - end-to-end pipeline tests for
  `UseMeshTrace`, built on a real `BenzeneMessageContext`/
  `BenzeneMessageApplication` pipeline (mirrors `HealthCheckPipelineTest`'s pattern) since
  `Benzene.Mesh.Test` didn't previously reference this package or `Core.MessageHandlers`/
  `Core.Messages`/`Microsoft.Dependencies` (now added as `ProjectReference`s). Covers: `UseMeshTrace`'s null-exporter pass-through, a
  successful export's topic/service/status/duration/ids, W3C traceparent join vs. missing/
  malformed-header fresh-trace fallback, a throwing exporter not affecting the response,
  no-status-reader → empty status, the `x-correlation-id` header capture, and `MeshSpan.Current`
  being set during `next()` and restored afterward. Setting up these tests requires
  `.AddContextItems()` explicitly (not part of `AddBenzeneMessage()` - only `AddMessageHandlers()`
  pulls it in) since none of these tests route through `.UseMessageHandlers()`. (Its
  `UseMeshDescriptor` cases went with the descriptor in spec-0.4.0.)
