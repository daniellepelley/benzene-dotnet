# Benzene.Mesh.Fleet.Tempo

## What this package does
The **non-AWS reference** realisation of the trace-backed fleet reader scoped in
`work/otel-fleet-adapter-scope.md` (**Increment 4**): it answers the mesh's `mesh:query:trace`,
`mesh:query:correlation`, and the fleet view's recent-flows from **Grafana Tempo's trace API**, so the
fleet UI + waterfall work over Tempo the same way `Benzene.Mesh.Fleet.Aws.XRay` makes them work over
X-Ray — reusing the **same** `CompositeMeshFleetReadModel`, `MeshCollectorHandlers.Queries`, and the mesh
UI's live Fleet plane (`UseMeshUi(..., envelopeUrl)`), differing only in the backend. That reuse is the whole point of the `IMeshTraceSource`
seam: increments 0-3 didn't need to change to add a second backend.

## Not to be confused with `Benzene.Mesh.Tracing.Tempo`
That sibling package queries Tempo's **metrics-generator** (service-graph metrics) over **PromQL** and
publishes `topology.json`. **This** package reads Tempo's **trace** API (TraceQL search + trace-by-id)
and serves the live fleet read-models. Different Tempo surface, different job; a deployment can use
either, both, or neither.

## Key types
- `TempoTraceSource : Benzene.Mesh.Collector.IMeshTraceSource` —
  - `GetTraceAsync` → `GET /api/traces/{id}` (OTLP/JSON), mapped by `TempoTraceMapper`; null (→ handler
    answers `NotFound`) when Tempo has no such trace or it carried no Benzene topic-bearing span.
  - `GetCorrelationAsync` → TraceQL search `{ span."benzene.correlation-id" = "…" }` over the correlation
    window (`limit` = `TempoTraceSourceOptions.CorrelationSearchLimit`), then fetch each matching trace by
    id and group into a `CorrelationView` (traces earliest-first — the same ordering the in-memory
    collector and the X-Ray adapter use, so every plane renders identically). **Fetch isolation + bounded
    fan-out (#188, round 12-13).** The per-trace fetches run concurrently via `BoundedFanOut.WhenAllAsync`,
    capped by `TempoTraceSourceOptions.SearchConcurrency` (default 8, matching
    `JaegerTraceSourceOptions.SearchConcurrency`'s default/rationale) — not the old sequential `foreach`.
    Each fetch is wrapped in its own try/catch: a trace whose fetch throws (a genuine connection failure)
    logs a warning and is dropped, the rest are kept — before this fix the sequential loop had no
    try/catch, so one bad fetch propagated out of `GetCorrelationAsync` entirely and the composite's own
    fetch-isolation degraded the *whole* correlation to `null`, discarding every already-fetched trace too.
    See `work/bug-fix-rulings-round12-13-2026-08.md` §WP-2. Covered by `TempoTraceSourceTest`
    (`GetCorrelationAsync_IsolatesAPerTraceFetchFailure_AndKeepsTheRest` — N-1, not zero;
    `...FetchesMatchedTracesConcurrently_NotSequentially`).
  - **At-limit warning (#190, round 12-13).** `TempoTraceSourceOptions.CorrelationSearchLimit` (default
    100, preserving the prior hardcoded behavior) bounds the search; Tempo's `/api/search` has no further
    paging, so a search that returns exactly the limit may have missed matches beyond it — logged as a
    warning (`ILogger?`, optional constructor param) rather than silently truncated, mirroring X-Ray's #77
    at-limit warning. Covered by `TempoTraceSourceTest`
    (`GetCorrelationAsync_LogsAWarning_WhenTheSearchReturnsExactlyTheConfiguredLimit`,
    `...UsesTheConfiguredCorrelationSearchLimit`, `...DoesNotWarn_WhenBelowTheConfiguredLimit`).
  - `GetRecentFlowsAsync` → one TraceQL search `{ span."benzene.topic" != "" }` (mesh flows only) over the
    recent-flows window → `TraceSummary` rows (traceID; `durationMs`; start from `startTimeUnixNano`;
    `rootServiceName` → `Services`), newest first, capped. `Failed=false` and `Events=0`: Tempo's search
    summary carries no aggregate error flag or span count — the failure colouring and accurate event count
    come from the drill-in trace (`GetTraceAsync`). No per-trace fetch on a fleet load.
  - Reachable-but-unsuccessful (HTTP error / unexpected body) → null/empty, not a throw (the topology
    adapter's "one bad query shouldn't fault the build" rule); a genuine connection failure still throws
    and the composite's fetch-isolation degrades that slice.
- `TempoTraceMapper.Map(meshTraceId, otlpJsonBody)` — walks the OTLP `batches[].scopeSpans[].spans[]`
  (and legacy `instrumentationLibrarySpans`), emitting one `MeshTraceEvent` per span carrying
  `benzene.topic`. Reads `benzene.topic`/`benzene.version`/`benzene.status`/`benzene.correlation-id` by
  their **dotted OTLP names verbatim** — Tempo preserves attribute keys, so unlike X-Ray there's no
  annotation/metadata sanitising to reconcile. Service = the span's **`benzene.service`** attribute when
  present (2026-07-24), falling back to the batch `resource`'s `service.name` (already correct in the common
  case since `AddService("orders-api")` sets it — preferring `benzene.service` keeps the mesh's own namespace
  authoritative and uniform with the X-Ray/Jaeger mappers); times from `start`/`endTimeUnixNano`; events
  returned in start order; an unparseable body → empty list. **Recent-flows caveat:** Tempo's search summary
  carries `rootServiceName` (a backend name, not `benzene.service`) and no span attributes, so recent-flows
  rows show the backend name. **X-Ray now enriches its recent-flows rows** (a bounded, batched
  `BatchGetTraces` reads `benzene.service`), so Tempo is the remaining adapter with this caveat; Tempo
  parity is a **deferred follow-up** because its enrichment would be 20 **unbatched** `GET /api/traces/{id}`
  calls (no 5-per-call batch like X-Ray) — a heavier per-load fan-out that needs its own opt-in/limit first.
  The drill-in trace shows the real names today. Covered by
  `TempoTraceSourceTest` (`...PrefersBenzeneServiceTag_OverResourceServiceName`).
- `TempoTraceSourceOptions(tempoUrl)` — `CorrelationLookback` (24h) and `RecentFlowsLookback` (1h) bound
  the two searches (Tempo's `/api/search` needs a time range); a trace lookup is by id (no window).
  `CorrelationSearchLimit` (default 100, round 12-13 #190) is the `/api/search` `limit` for a correlation
  search — logged as a warning when hit, since there's no further paging beyond it.
  `SearchConcurrency` (default 8, round 12-13 #188) caps how many per-trace fetches
  `GetCorrelationAsync` runs concurrently while hydrating the search's matched trace ids.
- `Extensions.AddTempoFleetReadModel(options)` — registers the options, an `HttpClient` (unless one is
  already registered — the same shape as `AddTempoTopology`), `TempoTraceSource` as `IMeshTraceSource`, and
  `CompositeMeshFleetReadModel` as `IMeshFleetReadModel` (composed with whatever `IMeshUsageSource`s are
  registered for topic stats). Wire the read side with `UseMessageHandlers(MeshCollectorHandlers.Queries)`
  and point the mesh UI's live Fleet plane at it with `UseMeshUi(..., envelopeUrl: "/benzene/invoke")`.

## What it deliberately does NOT do
Per `IMeshTraceSource`, it carries **no** per-topic/service counts and **no** service health: traces are
sampled and Tempo has no heartbeat feed. Topic stats compose from an `IMeshUsageSource` (a
Prometheus/Tempo-metrics usage adapter would be the Tempo-native fit, not yet built); with none wired the
composite serves traces/correlation/recent-flows/services and reports honest-empty topic stats.
Per-service and single-topic pages stay omitted on this plane (no descriptor feed).

## Prerequisites it relies on
The pipeline must stamp `benzene.topic`/`benzene.version`/`benzene.status`/`benzene.correlation-id` on the
topic-bearing span (`Benzene.Diagnostics.ActivityMiddlewareDecorator`). Tempo must ingest those spans and
have TraceQL search enabled. Unlike X-Ray, no annotation-vs-metadata indexing choice applies — TraceQL
filters on span attributes directly by their dotted names.

## Verification caveat
`TempoTraceMapper`, the TraceQL construction, and the search/trace parsing are unit-tested against Tempo's
**documented** API shapes (`test/Benzene.Mesh.Test/TempoTraceSourceTest.cs`, mocked `HttpClient`), covering
trace mapping + non-Benzene-span filtering, correlation search/fetch/group, recent-flows ordering + the
single-search (no per-row fetch) guarantee, the null cases, and (round 12-13) per-trace fetch isolation +
concurrent hydration + the `CorrelationSearchLimit` at-limit warning. It has **not** been run against a **live**
Tempo instance — the same egress limitation that blocked live-verifying `Benzene.Mesh.Tracing.Tempo`. Treat
the API paths (`/api/traces/{id}`, `/api/search`), the OTLP/JSON trace shape, and the TraceQL attribute
syntax as "per Tempo's public documentation, not independently confirmed" until verified against a real instance.

## Dependencies
- **Benzene.Mesh.Collector** — `IMeshTraceSource`/`IMeshFleetReadModel`/`CompositeMeshFleetReadModel`/
  `TraceView`/`TraceSummary`/`CorrelationView`/`MeshTraceEvent` (via `Benzene.Mesh.Wire`) and
  `MeshCollectorHandlers.Queries`.
- **Benzene.Core.Middleware** — `BoundedFanOut` (the per-trace correlation fetch fan-out, round 12-13 #188).
- **Benzene.Abstractions** — `IBenzeneServiceContainer` for the DI extension; `Microsoft.Extensions.Logging.Abstractions`
  (transitive via `Benzene.Abstractions`) for the source's optional `ILogger` constructor overload, wired by
  `AddTempoFleetReadModel` via `resolver.TryGetService<ILogger<TempoTraceSource>>()` (the same pattern
  `AddXRayFleetReadModel` uses). Uses `System.Net.Http`'s `HttpClient` and `System.Text.Json` directly,
  matching the `Benzene.Mesh.*` family.
