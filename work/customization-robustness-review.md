# Customization robustness review

Status: all six obvious fixes DONE (see fix plan); the "Questions for the user" section below is
awaiting decisions. Probes live in `test/Benzene.Core.Test/Customization/`. Findings verified by
code-reading plus runtime probes (marked ✅ where probed over a real socket / real pipeline).

## The systemic finding: Add vs TryAdd decides whether "override X" works at all

`ConfigureServices` runs BEFORE `Configure`; transport `Use*` extensions register their services
immediately during `Configure`. MS DI is last-registration-wins for single resolves. Therefore:
- Seams the framework registers with **TryAdd**: user's earlier `ConfigureServices` registration
  wins. These overrides work as advertised.
- Seams the framework registers with **plain Add** during Configure: the framework's later
  registration silently shadows the user's. No warning, no startup check. The only working override
  is registering AFTER the `Use*` call (inside the pipeline action or later in Configure) — which
  nothing documents.

### Truth table (verified by reading every registration)

Works from ConfigureServices (TryAdd): `IHttpStatusCodeMapper` ✅(probed), `IGrpcStatusCodeMapper`,
`IDefaultStatuses`, `IValidationStatusMapper` (FluentValidation), `IVersionSelector`,
`MessageVersionHeaderNames` (resolved lazily — order-independent by design), `IRouteFinder`,
`IHttpHeaderMappings`, ALL BenzeneMessage-envelope getters, `IRequestMapper` on SQS/SNS/BenzeneMessage.

Silently shadowed from ConfigureServices (plain Add during Configure): topic/body/header/version
getters + result setters on **AspNet, SQS (both flavors), SNS, Kafka, ServiceBus, EventHub**;
`IRequestMapper<AspNetContext>`; `ISerializer` ✅(probed — user serializer ignored on AspNet).
[All converted to TryAdd — see fix 1. Note discovered while flipping the probe: even with the
override honored at the seam, the AspNet request/response BODY never flows through `ISerializer` —
the media-format path's default `JsonMediaFormat` wraps the concrete `JsonSerializer` directly, so a
custom `ISerializer` affects only consumers that resolve the seam (clients, envelope handlers), not
HTTP body rendering. Separate honesty question: is `ISerializer` over-advertised as "the" serializer
seam?]

Consequences beyond the getters themselves:
- `AddPayloadVersioning(...).ForContext<AspNetContext>()` in ConfigureServices is **silently
  disabled** by `UseHttp` (its `AddScoped<IRequestMapper<AspNetContext>>` wins) — old-version HTTP
  payloads deserialize into the newest type with defaulted fields, no error. The message-versioning
  cookbook claims order-independence ("no way to half-wire it") — false for AspNet. Its own doc
  comment claims "the transports register their default request mapper with TryAdd" — false for AspNet.
- `Benzene.Testing`'s `WithServices` runs before Configure too, so even TEST fakes of transport
  getters are silently shadowed.

**Fix (DONE): converted the per-context transport seams to TryAdd** so ConfigureServices
overrides work uniformly, matching the BenzeneMessage envelope's existing registration style and
making the versioning cookbook's order-independence claim true. Per-context generic seams have
exactly one registrar each, so first-wins cannot change framework-vs-framework outcomes; `ISerializer`
on AspNet flips from (scoped JsonSerializer beating AddBenzene's TryAdd singleton JsonSerializer) to
(the singleton winning) — same type, stateless, behaviorally invisible.

## Custom statuses & results

A status is a plain string; `BenzeneResult.Set(...)` is the escape hatch; spec explicitly allows
application-defined statuses (unknown → each mapping's generic-error row, conformance-pinned).

1. **Split-brain success classification** ✅(probed). `Set("quarantined", payload)` →
   `IsSuccessful=true` (constructor derives `!IsFailure(status)`), so queues ACK it (SQS delete,
   ServiceBus complete) and the HTTP body renders the success payload — while the HTTP mapper sends
   **500** and gRPC **throws RpcException(Internal)** (payload lost; raw status only in the
   `benzene-status` trailer). 500-with-success-body confirmed over a real socket. All silent.
2. **Client-side wall (frozen).** `BenzeneResultHttpMapper.NormalizeStatus` (static, not
   DI-replaceable): any non-vocabulary envelope status → null → client rewrites the result to
   `unexpected-error` ("Status code quarantined not mapped"). Custom statuses cannot round-trip
   through ANY Benzene client, even though the server-side envelope carries them verbatim and the
   spec says applications MAY use additional statuses. → USER QUESTION (extensibility vs spec).
3. **Overload trap.** `Set("custom", "some string payload")` binds to the `params string[] errors`
   overload → silently a FAILURE result. Docs don't warn.
4. **`result.IsSuccess()` extension vs `result.IsSuccessful` disagree** on custom statuses
   (vocabulary-set membership vs constructor flag). User assertion code using one, framework using
   the other.
5. **Validation status:** FluentValidation path fully works (per-rule `.WithStatus`, handler-level
   `[ValidationStatus("...")]`, replaceable `IValidationStatusMapper` — all honest). BUT
   DataAnnotations hard-codes `validation-error`; client-side FluentValidation middleware hard-codes
   it too; and nothing documents that a custom validation status ALSO needs an
   `IHttpStatusCodeMapper`/`IGrpcStatusCodeMapper` replacement to avoid 500/Internal.
   → Obvious fix: DataAnnotations middleware should resolve `IDefaultStatuses`.
6. **Unknown-status HTTP mapping is silent** (`DefaultHttpStatusCodeMapper` → "500", no log).
   → Candidate: log-once-per-status warning naming the status and the mapper seam.

## Topic key per transport

- SQS/SNS/ServiceBus/EventHub: config-exposed key (`TopicAttributeKey`/`TopicPropertyKey`) — honest.
- Kafka: topic IS the broker topic; header-routing requires replacing the topic getter, which is
  plain-Add → ConfigureServices override silently ignored (fix above covers it).
- HTTP: route-based; `IRouteFinder` replaceable (TryAdd) — honest.
- BenzeneMessage envelope: field name frozen by the DTO (spec contract) — fine.
- **`IBenzeneWireNames` is a dead seam**: its XML doc says "register a replacement and every binding
  and outbound client follows" — NOTHING ever resolves it from DI; every binding uses the const.
  Registering it is a complete no-op. → Fix doc comment (honest), or wire it up (design change).
  → USER QUESTION.
- **`<missing>` sentinel kills the helpful error**: `Topic(null)` becomes `"<missing>"`, so the
  router's "Topic is missing — set the transport's topic attribute/header on the producer, or
  configure UsePresetTopic(...)" remediation branch is DEAD CODE for every built-in getter. A
  wrong-attribute producer sees only `not-found` / "No handler found for topic '<missing>'" —
  docs describe the branch that never fires. → Obvious fix: router treats the sentinel as missing.
- SQS self-hosted `WholeBatch` mode DELETES unrouted messages (message loss with only a warning
  log); `PerMessage` (default) redelivers forever until DLQ. Stale XML doc on
  `SqsConsumerMessageHandlerResultSetter` still says WholeBatch is the default. → doc fix.

## Versioning

- Change version header name: `AddMessageVersionHeaderNames` — works, genuinely order-independent.
- Replace `IVersionSelector`: works from ConfigureServices (TryAdd). BUT: single-version topics
  bypass the selector entirely (fast path) — a strict "reject unknown versions" selector silently
  never runs for them; and an unknown requested version silently routes to the ordinal-max handler
  (documented only as the "no version" behavior, not the unknown-version behavior). A custom
  selector returning a version not in the available set surfaces as a misleading generic
  `not-found`. → Candidates: log on unknown-version fallback; document the fast path.
- Payload casting: works except the AspNet half-wire (covered by systemic fix).
- `TryGetService` swallows factory exceptions (`catch { return default; }`) — a REGISTERED
  `MessageVersionHeaderNames`/`ISchemaCasters` whose factory throws is treated as unregistered,
  silently. → Candidate fix: narrow the catch or log.

## Inbound→outbound header flow

- No automatic propagation of business headers exists (by design); W3C traceparent is the only
  end-to-end automatic flow. The documented pattern is a scoped holder + outbound middleware.
- **correlation-ids.md claims the outbound header is `x-correlation-id`; the actual default key is
  `correlationId`** — and the inbound diagnostics tag reads only `x-correlation-id`, so following
  the doc yields correlation that never joins up. → Obvious docs fix (+ consider changing the
  middleware default to `x-correlation-id`? behavior change → USER QUESTION).
- `CorrelationIdMiddleware` unconditionally overwrites an explicit per-call header with a
  self-generated GUID when nothing seeded `ICorrelationId`. → Fix: skip if the key is already
  present (explicit per-call wins) — semantic choice, flagged.
- `RabbitMqContextConverter` throws on a null header value (Kafka null-coalesces). Client catch-all
  masks it as `service-unavailable`. → Obvious fix: coalesce like Kafka.
- EventBridge silently drops ALL headers for non-object payloads; Event Grid CloudEvents ingress
  drops extension attributes (egress forwards them); Step Functions client has no headers at all;
  classic Event Grid schema drops headers. Partially documented. → report/docs.
- Headers dictionaries are case-SENSITIVE everywhere; multi-tenancy cookbook does
  `TryGetValue("x-tenant-id")` → an `X-Tenant-Id` sender silently resolves no tenant.
  → Cookbook fix (use the case-insensitive `GetHeader` extension); making getters case-insensitive
  is a behavior change → USER QUESTION.
- multi-tenancy cookbook's outbound section references the client-decorator mechanism DELETED
  2026-07-17 and a dangling `outboundHeaders` snippet. request-correlation.md still claims inbound
  extraction is HTTP-only (fixed 2026-07-18 per Diagnostics CLAUDE.md). → Obvious docs fixes.
- Transport client registrations expose the CONCRETE class (e.g. `AddScoped(x => new
  SqsBenzeneMessageClient(...))`), so a user decorator registered as `IBenzeneMessageClient` never
  intercepts resolutions of the concrete type. → report/design question.

## Fix plan (obvious, in order)
1. ~~TryAdd conversion for per-context transport seams (AspNet, Aws.Sqs, Lambda.Sqs, Lambda.Sns,
   Kafka.Core, Azure.ServiceBus, Azure.EventHub DI extensions) + regression tests (serializer +
   header-getter override now honored; payload-versioning-on-AspNet from ConfigureServices works).~~
   **DONE** (also Grpc and the ApiGateway/V2 http/response adapters; collection-resolved services -
   `IResponseHandler`, `IResponseRenderer`, `IRequestEnricher` - deliberately left plain Add, and
   Azure.Function.* / GoogleCloud / Lambda.Kafka adapters were out of scope this pass).
2. ~~MessageRouter: treat `<missing>` sentinel as missing-topic → the remediation message fires.~~
   **DONE** — the not-found branch emits the actionable detail for the sentinel (status stays
   `not-found` so HTTP keeps its 404); logged as Warning. Note: unrouted AspNet requests
   intentionally write no body (strangler fall-through for embedded mode), so on HTTP the message
   surfaces in logs only; queue/stream transports carry it in the result.
3. ~~RabbitMQ null header value coalesce + test.~~ **DONE** (matches Kafka's coalesce).
4. ~~CorrelationIdMiddleware: don't clobber existing per-call key + test.~~ **DONE** — explicit
   per-call header wins; ambient/self-generated value stamps only when the key is absent.
5. ~~DataAnnotations ValidationMiddleware: use IDefaultStatuses + test.~~ **DONE** — builder passes
   `TryGetService<IDefaultStatuses>()`; parameterless ctor keeps the old hard-coded default.
6. ~~Docs batch.~~ **DONE** — correlation-ids.md key claim (+ clients.md row); multi-tenancy
   outbound section rewritten as an `IMiddleware<OutboundContext>` example + case-insensitive
   `GetHeader` in strategy B; request-correlation stale HTTP-only claim; IBenzeneWireNames XML doc
   now says it is NOT a DI seam and names the real per-transport knobs;
   SqsConsumerMessageHandlerResultSetter stale default; message-handlers.md/diagnosing-failures.md
   (+ service-bus-handling.md) now describe the `<missing>` sentinel and the new not-found detail;
   custom-status recipe added to message-result.md + reference/results.md (Set derives
   IsSuccessful, HTTP 500/gRPC Internal mapping + TryAdd mapper replacement, validation-status
   wiring, client NormalizeStatus wall, `Set(status, string)` overload trap).

All six fixes verified: Benzene.Core.Test 2346/2346, Benzene.Conformance.Test 134/134. New tests in
`test/Benzene.Core.Test/Customization/RobustnessFixesTest.cs`.

## Questions for the user (non-obvious)
1. Client-side `NormalizeStatus` destroys custom statuses (→ `unexpected-error`). Pass unknown
   statuses through verbatim instead (preserving IsSuccessful=false? or deriving from HTTP code)?
   Cross-port + conformance implications.
2. `IBenzeneWireNames`: wire it up for real (all bindings resolve it) or de-advertise it?
3. Correlation default key `correlationId` vs docs' `x-correlation-id` — change the default (wire
   compat break) or fix the docs (done either way)?
4. Case-insensitive header lookup as the framework default?
5. gRPC throwing on custom success statuses: acceptable (mapper replacement documented) or should
   unknown+IsSuccessful=true map to OK?
6. `Set("custom", <string>)` overload trap — accept, or add an analyzer/obsolete overload?
7. `ISerializer` never reaches HTTP body rendering (`JsonMediaFormat` wraps the concrete
   `JsonSerializer`) — should the media-format path resolve `ISerializer` from DI, or should the
   docs stop implying a replaced `ISerializer` changes HTTP bodies?
