# Customization robustness review

Status: all six obvious fixes DONE, and all seven follow-up decisions ALSO DONE (see "Questions for
the user, resolved" below). Probes/regression tests live in `test/Benzene.Core.Test/Customization/`.
Findings verified by code-reading plus runtime probes (marked ✅ where probed over a real socket /
real pipeline).

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
HTTP body rendering. **Resolved (see fix 7 below, revisited): this is correct, deliberate behavior**
— `ISerializer` is format-agnostic (`Benzene.Xml.XmlSerializer` also implements it) and is not meant
to be "the" HTTP serializer; JSON media rendering has its own, separately-registered customization
point, the concrete `JsonSerializer` class.]

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

## Questions for the user, resolved

The user's answers and what was implemented for each, in order:

1. **Custom statuses should round-trip verbatim.** `BenzeneResultHttpMapper.NormalizeStatus` now
   passes an application-defined status through instead of collapsing it to `null`/`unexpected-error`.
   Round-tripping `IsSuccessful` correctly (not just the status text) needed a wire change - see #2.
2. **Custom `Set` needs a mandatory isSuccessful, and IsSuccessful must be honored over every
   transport.** This was the big one:
   - **Wire contract**: added `isSuccessful` (required bool) to the `BenzeneMessage` response
     envelope (wire-contracts.md §1.2, spec repo commit 98a9b29) - the authoritative signal a
     receiver now prefers over deriving classification from `statusCode` text. `IBenzeneMessageResponse`/
     `BenzeneMessageResponse`/`BenzeneMessageClientResponse` carry it; `IBenzeneResponseAdapter<TContext>`
     gained a default-interface `SetSuccessful` (no-op for numeric-status-code transports, wired for
     the `BenzeneMessage` envelope); `DefaultResponseStatusHandler` sets it; `AsBenzeneResult` reads
     it with a safe fallback (`IsSuccessStatus`) when the sender is an older service that doesn't
     write it yet.
   - **HTTP/gRPC mapper widening**: `IHttpStatusCodeMapper`/`IGrpcStatusCodeMapper` gained a
     default-interface `Map(status, isSuccessful)` overload (old `Map(status)` overrides keep
     compiling unchanged); the default mappers now map an unknown-but-successful status to 200/OK
     instead of the generic-error row. wire-contracts.md §4.1/§4.2 updated to match; conformance
     fixtures split their single `<unknown>` row into one per `isSuccessful` value.
   - **`BenzeneResult.Set<T>(status, payload)` now throws `ArgumentException`** for a status outside
     `BenzeneResultStatus.IsKnown` - forcing every custom-status call through the explicit
     `Set<T>(status, payload, isSuccessful)` overload instead of silently guessing. Internal call
     sites updated to pass the explicit flag.
3. **`IBenzeneWireNames` should work.** It's now a real DI seam for the standalone consumer
   packages: `AddSqsConsumer`, `AddServiceBusConsumer`, `AddEventHubConsumer`, `AddRabbitMq` each
   resolve a registered `IBenzeneWireNames` (TryAdd-registered by `AddBenzene`) to seed their topic
   getter's key, when the caller left that transport's own parameter at its default; an explicit
   value always still wins. Proven end-to-end in `BenzeneWireNamesOverrideTest.cs`. **Not yet wired**:
   outbound clients (they build converters eagerly, not through a lazy DI resolve, so honoring
   `IBenzeneWireNames` there needs a deeper restructuring than this pass covers), the Lambda-triggered
   consumer packages, Azure Functions-triggered consumers, GoogleCloud Pub/Sub, and Kafka (which has
   no equivalent knob - topic is a routing concept there, not a message attribute). Also found and
   fixed in passing: RabbitMq's `AddRabbitMq` was plain `Add`, not `TryAdd` - missed by fix 1's pass.
4. **All expected headers should be able to change.** The correlation header key was two independent
   hardcoded literals (`CorrelationIdMiddleware` defaulted outbound stamping to `"correlationId"`;
   `ActivityMiddlewareDecorator` hardcoded its inbound trace-tag read to `"x-correlation-id"`) that
   never matched by default. Added `Benzene.Abstractions.CorrelationHeaderDefaults`/`CorrelationHeaderOptions`
   as the one shared, DI-overridable definition; both directions now default to `x-correlation-id`
   (matching wire-contracts.md's own request example) and read the same optional DI registration.
   Fixed `Benzene.Azure.Function.AspNet`'s header getter, which used to rename incoming
   `x-correlation-id` to `correlationId` - a third, uncoordinated literal that made this specific
   transport's diagnostics tag miss the header even before this fix.
5. **Header lookup should be case-insensitive.** Swept ~45 files: every Benzene-owned headers
   dictionary (request/response envelopes, every transport's `IMessageHeadersGetter`, client request
   headers, test builders) now constructs with `StringComparer.OrdinalIgnoreCase`, matching the
   precedent already set by `Benzene.Aws.Lambda.ApiGateway`. Also fixed the existing `.GetHeader(...)`
   convenience extension, which was already case-insensitive by default but used
   `StringComparer.CurrentCultureIgnoreCase` - locale-dependent and wrong for protocol-level keys;
   changed to `OrdinalIgnoreCase`.
6. **Fix the `Set(status, string)` overload trap.** Obsoleted the generic
   `Set<T>(string status, params string[] errors)` overload (`[Obsolete]`, compiler warning) and
   added `SetFailed<T>(status, errors)` as its unambiguous replacement - a distinct method name means
   a single string argument can no longer be silently captured by the errors overload when it was
   meant for `Set<T>(status, payload)`. All internal callers migrated.
7. **Revisited: the finding was correct behavior, not a bug.** The first pass changed
   `JsonMediaFormat<TContext>` to depend on `ISerializer` instead of the concrete `JsonSerializer`,
   reasoning that a replaced `ISerializer` should reach the HTTP body. The user caught the flaw:
   `ISerializer` is a **format-agnostic** abstraction - `Benzene.Xml.XmlSerializer` also implements
   it - so a service that replaces `ISerializer` for an unrelated reason (its outbound client should
   send XML, say) would silently make `JsonMediaFormat`, which always advertises `Content-Type:
   application/json`, render that XML instead. `Benzene.Xml`'s own `XmlMediaFormat` already
   establishes the correct pattern: it wraps the **concrete** `XmlSerializer`, registered
   independently of `ISerializer` (`AddXml` does `TryAddSingleton<XmlSerializer>()`, not
   `TryAddSingleton<ISerializer, XmlSerializer>()`). Reverted `JsonMediaFormat` to the same shape -
   it wraps the concrete `JsonSerializer` (unsealed, virtual members, already independently
   TryAdd-registered by `AddBenzene`). **The correct way to customize JSON media rendering is to
   register your own `JsonSerializer`** (custom `JsonSerializerOptions`, or a subclass overriding
   its virtual methods) in `ConfigureServices` - not to replace `ISerializer`, which is for changing
   the format used elsewhere (message/envelope serialization) without touching HTTP/JSON rendering.
   `CustomJsonSerializer_InConfigureServices_Probe` proves the corrected path works; the original
   `ISerializer` probe now asserts it correctly does *not* reach the HTTP body.

All fixes verified: Benzene.Core.Test 2352/2354 (2 skipped, unrelated), Benzene.Grpc.Test 105/105,
Benzene.Conformance.Test 136/136 (was 134 - the 2 new `<unknown>`+isSuccessful mapping rows).
Benzene.Aws.Tests (10 fail) and Benzene.Mesh.Test (2 fail) unchanged from the pre-existing,
environment-dependent baseline (real AWS/mesh endpoints, fails identically on clean main).
