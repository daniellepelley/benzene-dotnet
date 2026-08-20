> ARCHIVED 2026-08-20: actioned; Phases 1–5 shipped (RFC 9457 across transports: `src/Benzene.Results`, `src/Benzene.Http`; gated by `ProblemDetailsConformanceTest`).

# Problem Details (RFC 9457) Error Payload — Plan

**Status:** Plan for implementation — settles the pending task *"Spec: investigate a standardised
error payload (RFC 9457)"* (the investigation verdict is §1; the rest is the adoption plan).
**Date:** 2026-08-13
**Owner direction:** adopt a standardised error payload based on RFC 9457 (Problem Details) —
"plan out the problem details error payload because I think that's quite important."
**Sources to read first:** `Benzene/work/error-payload-proposal.md` (the July investigation this
supersedes in part), `work/benzene-result-errors-ruling.md` (the approved-but-unimplemented
structured-error model this plan depends on), `Benzene/work/spec-review-2026-07-25.md` §2.
**Audience:** implementation agents. Each phase is a self-contained task; do them in order unless
"Depends on" says otherwise.

**Decisions already made (do not re-litigate):**
1. The error payload **is an observable wire contract** (both repos' AGENTS.md): the normative
   definition lands in the **spec repo** (`Benzene/docs/specification/**` + conformance fixtures)
   first; benzene-dotnet is the reference implementation; other ports implement from the spec.
2. `IBenzeneResult.Errors` becomes `IReadOnlyList<BenzeneError>` (`{Message, Field?, Code?}`,
   `BenzeneError` in `Benzene.Abstractions`) — **ruled and approved 2026-07-25**
   (`work/benzene-result-errors-ruling.md`), not yet implemented (verified 2026-08-13:
   `src/Benzene.Abstractions/Results/IBenzeneResult.cs` still has `string[] Errors`). Phase 2
   implements that ruling as written; its amendments are binding here.
3. **No further changes to `IBenzeneResult`** — the ruling's R7 froze the result model ("the last
   change to the result model before the tag"). Everything client-facing in this plan is additive
   (extensions, a capability interface on concrete types), never a new interface member.
4. Pre-1.0 compatibility posture is the **topic-prefix precedent** (`work/archive/topic-prefix-migration-2026-07.md`):
   clean break, no dual-accept shim — `version.txt` is `0.0.2`, everything published is alpha.
5. The `errors` wire member is an **ordered array of objects**, not ASP.NET's
   `{field: [messages]}` dictionary — argued and settled in `error-payload-proposal.md` §4
   (no-field errors, per-message codes, ordering, trivial in Go); not reopened.
6. `code` (per-error, app-owned, open) and the mesh issue `classification` (per-invocation,
   framework-owned, closed) **stay two concepts** (ruling §5.4). Neither `code` nor the new
   problem `type` may enter the mesh issue fingerprint.

---

## 1. Investigation verdict (settles the pending task)

**RFC 9457 is the right standard, adopted as a transport-neutral profile — not verbatim.**

The July investigation (`error-payload-proposal.md`) rejected "full RFC 9457" (its option B) for
one honest reason: RFC 9457's `status` member is *the integer HTTP status code*, and fabricating
an HTTP number inside an SQS message's error body is a transport lie this project refuses
elsewhere. That objection was about **one member**, not the standard. Everything else in RFC 9457
fits Benzene exactly:

- Its members are **all optional** (even `type`, defaulting to `about:blank`), so a compliant
  problem document can omit `status` where no HTTP response exists. Omission is compliance, not
  divergence.
- `type` as the primary, URI-named discriminator with **registered extension members** is
  precisely the extensibility model Benzene's status vocabulary already uses ("applications MAY
  use additional status strings") — the RFC gives it an interoperable spelling.
- The RFC's `application/problem+json` media type gives HTTP bindings a signalling story we
  currently lack (today failures go out as plain `application/json`).
- The .NET type is already half-built: `src/Benzene.Results/ProblemDetails.cs` has
  `Type/Status/Title/Detail/Instance` (all strings), and spec §1.3 already calls the payload
  "problem-details-shaped". What ships today is only `{status, detail}` with the RFC members
  always null — the July finding that this is *mislabeled* (a string Benzene status in a member
  the RFC defines as an integer) stands, and this plan fixes it by renaming, not by removing the
  RFC alignment.

**The profile ("problem details over Benzene envelopes"), in one paragraph:** on failure, the
response body is a **valid RFC 9457 problem document** on every transport. `type`/`title` come
from a Benzene-owned registry keyed by the status vocabulary (no new taxonomy). The
transport-neutral discriminator is the extension member **`benzeneStatus`** (the §3 status string,
mirroring the envelope) — required everywhere. The RFC-numeric `status` appears **only where an
actual HTTP response exists**, and there MUST equal the real HTTP response code (which the §4.1
table already determines) — so it is never fabricated on a queue, and never wrong on HTTP.
Structured validation errors travel in the **`errors`** extension member (array of
`{message, field?, code?}`), fed by the approved `BenzeneError` model. `detail` stays what it is
today (the joined messages) so every existing reader keeps working.

This supersedes the July proposal's option C ("problem-details-*inspired*, explicitly not
RFC 9457") in exactly one respect: the payload now **is** RFC 9457, because the one collision
(`status` string vs. integer) is resolved by moving the Benzene status to `benzeneStatus`. It
upholds option C's transport honesty and adopts its `errors` shape unchanged.

---

## 2. The contract (what the spec phase pins)

### 2.1 The problem document

```json
{
  "type": "https://benzene.app/problems/validation-error",
  "title": "Validation failed",
  "status": 422,
  "detail": "Name must not be empty, Age must be greater than 0",
  "benzeneStatus": "validation-error",
  "errors": [
    { "message": "Name must not be empty",     "field": "Name", "code": "NotEmptyValidator" },
    { "message": "Age must be greater than 0", "field": "Age",  "code": "GreaterThanValidator" }
  ]
}
```

| Member | Type | Rules |
|---|---|---|
| `type` | string (URI ref) | Framework-produced failures MUST use the registry URI for the status (§2.2). Application-authored problems SHOULD use their own absolute URI; absent/`about:blank` is tolerated on read. Readers treat it as an **opaque identifier** — comparison is string equality, never dereference. |
| `title` | string | Short human summary of the *type*, fixed per type (registry value for framework types). Never asserted by fixtures (wording rule). |
| `status` | integer | **HTTP bindings only**: MUST equal the actual HTTP response status code (which §4.1 derives from the Benzene status). MUST be **omitted** where no HTTP response exists (envelope over non-HTTP, queue replies). Benzene clients MUST NOT classify from it. |
| `detail` | string | Human-readable occurrence detail — the result's error messages joined with `", "`, unchanged from today. The compatibility member: it is what every existing reader uses. |
| `instance` | string (URI ref) | Optional, application-owned. The framework never fabricates it. Dereferenceability optional (out of scope, §8). |
| `benzeneStatus` | string | **Required.** The §3 status string, mirroring the envelope's `statusCode`. The transport-neutral discriminator. Carries the `benzene` marker per the naming principle (this member namespace is shared with the RFC and applications). |
| `errors` | array | Optional; when present, **authoritative and ordered**. Each item: `message` (required), `field` (optional — the producer's property path; JSON Pointer for `Benzene.JsonSchema`, .NET property path for the validation middlewares, documented as such), `code` (optional — machine-readable rule code, emitted verbatim, e.g. FluentValidation's `NotEmptyValidator`). |
| *(extensions)* | any | Applications MAY add members (RFC 9457 §3.2). Readers MUST ignore unknown members. Neither `code` nor `type` participates in the mesh issue fingerprint (normative one-liner, ruling §5.4). |

**Replace, not wrap:** when the result is unsuccessful, the response body **is** the problem
document — exactly today's behavior (`DefaultResponsePayloadMapper` already discards the payload
on failure). The existing health-check nuance is untouched: a `service-unavailable` result marked
successful (the `Set<T>(status, payload, isSuccessful)` escape hatch) renders its payload, not a
problem — the branch is on `IsSuccessful`, not on status class.

### 2.2 The problem-type registry (no third taxonomy)

One registry row per **failure** status — the registry is *keyed by the existing §3 status
vocabulary*, so no new taxonomy is introduced. Success statuses have no problem type (problems
exist only on failure). URIs live under `https://benzene.app/problems/` (the spec repo owns
benzene.app; the URIs are identifiers first — serving them as pages is parked, §8).

| `benzeneStatus` | `type` URI (`https://benzene.app/problems/` +) | `title` | HTTP `status` |
|---|---|---|---|
| `bad-request` | `bad-request` | Bad request | 400 |
| `unauthorized` | `unauthorized` | Unauthorized | 401 |
| `forbidden` | `forbidden` | Forbidden | 403 |
| `not-found` | `not-found` | Not found | 404 |
| `conflict` | `conflict` | Conflict | 409 |
| `validation-error` | `validation-error` | Validation failed | 422 |
| `too-many-requests` | `too-many-requests` | Too many requests | 429 |
| `unexpected-error` | `unexpected-error` | Unexpected error | 500 |
| `not-implemented` | `not-implemented` | Not implemented | 501 |
| `service-unavailable` | `service-unavailable` | Service unavailable | 503 |
| `timeout` | `timeout` | Timeout | 504 |

Application-defined failure statuses: `type` is the application's own URI or omitted;
`benzeneStatus` carries the string; HTTP `status` falls to the §4.1 unknown row (500) as today.

**Mesh alignment (informative cross-reference, no new mechanism):** the operator-side roll-up of
the same failure is the mesh issue `classification`
(`exception`/`validation`/`config-wiring`/`dependency`/`contract-drift`/`unclassified`), derived
from status + captured exception type by mesh.md §4.1's precedence — already implemented
(`MeshIssueClassification.Classify`, `benzene.exception.type` end-to-end tagging). The spec
section states the relationship explicitly: problem `type` is the **caller-facing** identity
(open, per-response), `classification` is the **operator-facing** identity (closed,
per-invocation, fingerprint-stable); both derive from the one status vocabulary, and the registry
deliberately introduces no third vocabulary between them.

### 2.3 Signalling per binding

- **HTTP** (ASP.NET Core self-host, API Gateway v1/v2, Lambda AspNet/HttpBridge, Azure Functions
  AspNet, Google Cloud Functions HTTP): on failure, when the negotiated response format is JSON,
  the response `content-type` MUST be **`application/problem+json`** (with charset as today);
  `status` (numeric) MUST be present and equal the HTTP response code. When another format was
  negotiated, the document is serialized in that format (`application/problem+xml` for XML per
  RFC 9457 §11.2; other formats keep their own content type — informative). Clients MUST accept
  both `application/json` and `application/problem+json` failure bodies.
- **BenzeneMessage envelope** (direct invocation, `/benzene/invoke`, queue request/response): the
  failure signal **is the envelope** — failure-class `statusCode` / `isSuccessful: false` (§1.2;
  note: `isSuccessful` was added to §1.2 on origin/main — commit `98a9b29`; pull the spec repo
  checkout before editing, the local copy can lag). The envelope's inner `headers.content-type`
  SHOULD be `application/problem+json`; readers MUST NOT require it. The **outer** transport
  content-type is unchanged (e.g. `BenzeneMessageHttpMiddleware` keeps `application/json` — its
  HTTP body is the envelope, not the problem document).
- **gRPC**: no JSON problem document. The problem's information maps onto gRPC's native error
  model, which the binding already implements: non-`OK` code per §4.2, `benzene-status` trailer
  (≡ `benzeneStatus`), detail string, and `google.rpc.BadRequest` in `grpc-status-details-bin`
  with one `FieldViolation` per error — Phase 5 fills `FieldViolation.Field` from
  `BenzeneError.Field` (the ruling's §5.3 "free correctness win").
- **Fire-and-forget / one-way** (SQS/SNS/EventBridge/Kafka/RabbitMQ consumption, one-way sends):
  a problem document is a **response-path artifact**; one-way bindings MUST NOT invent a reply.
  Failures surface where they already do: the mesh issue feed (classification + exception type +
  resolution hint), traces/logs, and the transport's retry/DLQ machinery (out of scope, §8).
  Where the application wired response events, the failure response carries the problem document
  like any response body (verify against `Benzene.ResponseEvents` at implementation time).

### 2.4 Compatibility stance

**Clean break on the pinned shape, additive for every real reader.** The body member `status`
(string) is **removed**, replaced by `benzeneStatus`; `detail` is kept unchanged; `type`/`title`/
`errors` are new. No dual-accept shim (decision 4). Why this is safe, measured:

- The .NET client (`ClientResultExtensions.AsBenzeneResult`) classifies from the **envelope**
  (`statusCode`/`isSuccessful`) and reads only `Detail` from the body — verified 2026-08-13. It
  never reads the body's `status` member, so old .NET services and new clients (and vice versa)
  interop through the transition with zero shim: against an old producer, `benzeneStatus`/`errors`
  are simply absent and `detail` still round-trips.
- The conformance fixtures pin the body by **subset** comparison, so old-shape emitters fail the
  *updated* fixtures (that is the point of the change), but nothing in the fixture format changes.
- Ports (TS/Go/Python) re-vendor and re-verify on their own schedule (Phase 8); until then their
  `{status, detail}` payloads degrade gracefully against the .NET client exactly as above.
- The packed alpha `Benzene.Results.ProblemDetails` changes shape (`Status` string→`int?`, new
  members) — a compile break for external users of the type; CHANGELOG + `docs/migration-alpha-to-1.0.md`
  entries required (that migration doc still does not exist; ruling R1 — create it in Phase 7).

---

## 3. Ground rules for every phase

- **Repos.** Phase 1 is in the cross-language **Benzene** repo (spec + fixtures + website
  link-check). Phases 2–7 are in **benzene-dotnet**. Phase 8 is per-port notes only.
- **Verify before you edit.** Paths/APIs cited here were verified on 2026-08-13; follow the
  intent if a symbol moved. In the Benzene repo, `git pull` first (the checkout used for this
  plan lagged origin/main on wire-contracts §1.2).
- **benzene-dotnet conventions** are as in `work/archive/spec-mesh-tooling-implementation-plan-2026-08.md` §0
  (csproj shape, single test project xunit+Moq no FluentAssertions, `IBenzeneServiceContainer`
  registrations, per-package `CLAUDE.md`, docs reachable from `docs/index.md`). Verification per
  phase: `dotnet build Benzene.sln -v q` (0 new warnings) + scoped `dotnet test`, plus
  `test/Benzene.Conformance.Test` whenever fixtures or emission change. Commit per phase,
  conventional messages.
- **Fixture discipline:** in this plan Phase 1 (spec repo) is the ONLY place conformance fixtures
  are authored; benzene-dotnet only **re-vendors** them verbatim into
  `test/conformance-fixtures/` (Phase 6). Never edit a fixture to make the implementation pass.

---

## Phase 1 — Spec repo: the normative definition + fixtures

**Repo:** Benzene (cross-language). **Depends on:** nothing. **Effort:** M.

Steps:
1. **Rewrite `docs/specification/wire-contracts.md` §1.3** ("Error payload" → "Problem details
   payload") to the §2.1 contract above: the member table, the replace-not-wrap rule, the
   `benzeneStatus` naming rationale (one line: the member namespace is shared with RFC 9457 and
   applications, so Benzene's name carries the marker; `errors` is borrowed convention and stays
   unmarked), the unknown-member-tolerance rule, and the withdrawal of the two defects the July
   investigation found: the string-`status` collision (fixed by rename) and the unimplementable
   "clients recover `errors` from `detail`" rule (replaced by: `errors` when present is
   authoritative; `detail` is presentation only; a reader without `errors` treats `detail` as a
   single opaque message).
2. **Add the problem-type registry** as §3.1, immediately after the status vocabulary it is keyed
   by — the §2.2 table plus the app-defined-status row and the mesh-classification
   cross-reference paragraph (including the normative "neither `code` nor `type` enters the mesh
   issue fingerprint" sentence, which also discharges the ruling §5.4 one-liner owed to mesh.md —
   add the mirror sentence in `mesh.md` §4.1).
3. **Signalling rules per binding** (§2.3): the `application/problem+json` rule + numeric-`status`
   rule in §4.1 (HTTP); the envelope rule in §1.3 itself; a sentence in §4.2 mapping problem
   members onto the existing gRPC error model (trailer + `google.rpc.BadRequest`); the one-way
   rule as a short paragraph in `transport-bindings.md` §1 (binding contract).
4. **Conformance fixtures** (`docs/specification/conformance/`):
   - `envelope-cases.json`: update every failure case body from `{"status": "...", "detail": ...}`
     to `{"type": "https://benzene.app/problems/<status>", "benzeneStatus": "<status>",
     "detail": ...}` and add `"errors": [{"message": "first error"}, ...]` where the canonical
     `conformance:status` handler supplies errors. Add `"bodyExclude": ["status"]` to failure
     cases — a **new negative-assertion key** mirroring the existing `headersExclude` precedent:
     listed members must NOT appear in the parsed body (pins "no numeric `status` off-HTTP").
     Document `bodyExclude` in `conformance/README.md`'s envelope-format section.
   - New **`problem-details-cases.json`**: (a) `registry` — directly assertable rows
     (status → type URI → HTTP status), the cheapest check, catching a URI typo without building a
     message (same rationale as `defaultMetadataKeys`); (b) `envelopeCases` — a canonical
     `conformance:problem` handler (add it to the canonical-handlers table: request
     `{ "message", "field"?, "code"?, "appType"? }`, returns `validation-error` carrying one
     structured error, or an application-authored problem verbatim when `appType` is given) pinning
     structured `errors` round-trip and app-defined `type` passthrough, positive + `bodyExclude`
     negatives (e.g. framework problems carry no `instance`); (c) `httpRules` — table rows for
     ports with an HTTP binding: failure content-type is `application/problem+json`, `status`
     member equals the §4.1 mapped code, success responses unaffected. Title wording is never
     asserted (existing wording rule).
   - Update `conformance/README.md`: fixture-file table row, canonical handler, which conformance
     claims require the new file (Benzene Core requires groups a+b; group c required for each
     HTTP binding the port ships — mirror the `transport-metadata-cases.json` phrasing).
5. **Sweep the other spec pages:** `core-concepts.md` (result model section — `errors` now
   structured per the ruling; keep it brief, the wire shape lives in wire-contracts) and
   `porting-guide.md` (one paragraph: implement the problem payload from §1.3 + fixtures; the
   registry is data, not code to invent).
6. **Website build clean:** `dotnet run --project website/generator -- --out website/dist` — 0
   broken-link warnings (the registry URIs are external identifiers, not internal links — do not
   add benzene.app/problems pages in this phase; parked, §8).

**Acceptance:** spec pages updated and self-consistent; fixtures parse; `bodyExclude` documented;
website build clean; no fixture asserts `title`/`detail` wording; the .NET conformance runner is
expected to FAIL against the new fixtures until Phases 3–6 land (state this in the commit message).

---

## Phase 2 — .NET: implement the approved `BenzeneError` result model

**Repo:** benzene-dotnet. **Depends on:** nothing (can run parallel with Phase 1). **Effort:** M.

This phase implements `work/benzene-result-errors-ruling.md` **as written** — that document is the
authority; re-read it in full before starting. Summary of what it binds you to:

1. `BenzeneError` record (`Message`, `Field?`, `Code?`; parameterless ctor + init-only; `ToString()
   => Message`) in `Benzene.Abstractions` namespace `Benzene.Abstractions.Results`;
   `IBenzeneResult.Errors` → `IReadOnlyList<BenzeneError>`; empty-shared-instance on success.
2. `BenzeneResult` factories: all `params string[]` overloads retained (project to message-only
   errors); structured non-`params` overloads on `Set`/`Set<T>`, `ValidationError`, `BadRequest`
   only. `ErrorMessages()` string-projection extension in `Benzene.Results`.
3. Fix the measured read sites (8 in `src/`, 23 test assertions — re-grep, the counts are from
   2026-07-25) in the same commit.
4. Validation integrations emit structure per ruling §5.1: FluentValidation (`PropertyName`→field,
   `ErrorCode`→code, **verbatim** — `NotEmptyValidator`, no suffix-stripping), DataAnnotations
   (member names→field, code null), JsonSchema (JSON Pointer→field, keyword→code, and **stop**
   prefixing the pointer into the message). Serializer round-trip tests for STJ + Newtonsoft (R2);
   `[JsonIgnore(WhenWritingNull)]` on `Field`/`Code` (R3).
5. CHANGELOG entry (breaking, alpha); XML docs (Abstractions stays 100% documented); update the
   two package `CLAUDE.md`s and the per-integration field/code capability table the ruling owes.

**Acceptance:** sln builds; full suite green; a FluentValidation failure carries field+code on the
result; `string.Join` call sites compile unchanged; CHANGELOG updated.

---

## Phase 3 — .NET: `ProblemDetails` reconciliation + server emission

**Repo:** benzene-dotnet. **Depends on:** Phase 1 (shape pinned), Phase 2 (`errors`). **Effort:** M.

1. **Evolve `src/Benzene.Results/ProblemDetails.cs` in place — no duplicate type.** Members after:
   `Type` (string), `Title` (string), `Status` (**`int?`** — was string; the one breaking member
   change, CHANGELOG'd), `Detail` (string), `Instance` (string), `BenzeneStatus` (string),
   `Errors` (`IReadOnlyList<BenzeneError>?`). `[JsonIgnore(WhenWritingNull)]` on every optional
   member so envelope-transport documents omit `status` rather than emitting null (writers MAY
   omit nulls per §6, but `bodyExclude` fixtures make omission of `status` load-bearing — emit-
   as-null would fail them; STJ default serializer honours the attribute, verify Newtonsoft/other
   serializers via the round-trip tests). Parameterless ctor + settable members (five-serializer
   rule). No `[JsonExtensionData]` bag — applications needing extension members subclass
   `ProblemDetails` (the established `ErrorPayload` pattern), which round-trips through all
   serializers; a typed extensions dictionary is parked (§8).
2. **`ProblemTypes`** static class in `Benzene.Results`: the registry as constants +
   `TypeFor(status)` / `TitleFor(status)` / `HttpStatusFor(status)` (unknown status → null type /
   null title / 500), and a factory `ProblemDetails From(IBenzeneResult result)` producing the
   §2.1 document (joined `detail` exactly as `ErrorPayload` does today, `errors` from the result's
   structured errors when non-empty, no `Status`). Keep it pure/static — it is the same
   spec-pinned-table pattern as `BenzeneResultStatus`.
3. **Retire `ErrorPayload`** (`src/Benzene.Results/ErrorPayload.cs`): its two jobs (join-detail
   construction; client-side deserialization target) move to `ProblemTypes.From` and
   `ProblemDetails` itself. Clean break per decision 4 — delete the type, fix the sites (emission:
   `DefaultResponsePayloadMapper`; read: `ClientResultExtensions` — Phase 5 rewrites that path
   anyway; tests; two docs mentions). If sequencing with Phase 5 is awkward, keep `ErrorPayload :
   ProblemDetails` as an empty `[Obsolete]` shim for one commit, deleted in Phase 5 — do not ship
   it.
4. **Emission:** `DefaultResponsePayloadMapper` serializes `ProblemTypes.From(result)` on failure.
   Add a capability probe for handler-authored problems: when the failed result implements
   `IHasProblemDetails` (Phase 5 defines it beside `ProblemDetails`), emit that document verbatim
   after coherence fill-in (missing `benzeneStatus` ← result status; `type`/`title` ← registry
   when absent and the status is known). Unhandled-exception and router failures need no change —
   their statuses (`service-unavailable`, `not-found`, `validation-error`, `unexpected-error`)
   flow through the registry like any other. **Known behavior, kept deliberately:** the exception
   path puts `ex.Message` into the errors (→ `detail`); RFC 9457 §4 warns about exposing
   internals, but changing that is a behavior decision independent of the payload shape — parked
   (§8) with a pointer to the mesh plane's stricter exception-*type*-only rule.
5. **Content-type at the renderer:** `SerializerResponseRenderer` sets the problem media type on
   failure — negotiated JSON → `application/problem+json`, XML → `application/problem+xml`, any
   other format unchanged. This one site serves every transport whose adapter honours
   `SetContentType` (HTTP responses directly; envelope transports via the envelope's inner
   `headers.content-type`, which is exactly the SHOULD in §2.3). Confirm
   `BenzeneMessageHttpMiddleware`'s outer `application/json` is a different code path (it is —
   line ~104) and stays unchanged.
6. **Tests:** payload-mapper failure emission (registry type/title, no numeric status, errors
   array, handler-authored passthrough + fill-in), renderer content-type per format, health-check
   successful-failure-status nuance unchanged, serializer round-trips (STJ + Newtonsoft at
   minimum) including `status` omission-when-null.

**Acceptance:** sln builds; a failed handler's wire body is the §2.1 document with no `status`
member; `ErrorPayload` gone (or one-commit shim); scoped tests green (conformance suite still red
until Phase 6 re-vendors — acceptable mid-plan, note in commit).

---

## Phase 4 — .NET: HTTP bindings — `application/problem+json` + numeric `status`

**Repo:** benzene-dotnet. **Depends on:** Phase 3. **Effort:** S–M.

1. **Numeric `status` on HTTP only.** The seam is already per-context:
   `IResponsePayloadMapper<TContext>` is registered per transport. Add an HTTP-aware problem
   payload mapper (decorating or subclassing `DefaultResponsePayloadMapper<TContext>`) that fills
   `ProblemDetails.Status` from the same status mapping the adapter uses for the response line
   (`IHttpStatusCodeMapper` / `DefaultHttpStatusCodeMapper` in `Benzene.Http`), and register it
   for the HTTP-facing contexts: `Benzene.AspNet.Core`, `Benzene.Aws.Lambda.ApiGateway` (v1+v2),
   `Benzene.Aws.Lambda.AspNet`/`HttpBridge`, `Benzene.Azure.Function.AspNet`,
   `Benzene.GoogleCloud.Functions.Http`. Follow each package's existing registration pattern
   (`TryAdd*` defaults; last-wins overrides); the two values MUST come from the one mapper so the
   body can never disagree with the response line.
2. **Verify content-type end-to-end** per binding: the Phase 3 renderer change must actually
   reach the native response (`AspNetResponseAdapter` writes `content-type` header — verify the
   ApiGateway/Functions adapters do too; fix any adapter that hard-codes `application/json` on
   this path).
3. **Clients accept both media types:** sweep HTTP client paths (`Benzene.Clients.Http`,
   `Benzene.Client.Http`, ApiGateway test helpers) for any `application/json` equality check that
   would reject `application/problem+json`; fix to prefix/media-type-aware comparison.
4. **Tests:** per-binding pipeline tests (the existing `AspNetPipelineTest` / ApiGateway test
   style): failure → mapped HTTP code, `application/problem+json`, body `status` == response
   code; success unchanged; envelope endpoint (`BenzeneMessageHttpMiddleware`) outer content-type
   unchanged with inner problem body.

**Acceptance:** an AspNet-hosted validation failure returns 422 + `application/problem+json` +
body `"status": 422`; a direct-invoke (envelope) failure body has **no** `status` member; tests
green.

---

## Phase 5 — .NET: client read-side, typed accessor, app-authored problems, gRPC

**Repo:** benzene-dotnet. **Depends on:** Phase 3 (Phase 4 not required). **Effort:** S–M.

1. **`IHasProblemDetails`** (`src/Benzene.Results/`, beside `ProblemDetails`):
   `ProblemDetails? Problem { get; }`. A capability interface on concrete result types — NOT a
   change to `IBenzeneResult` (decision 3). Implemented by a small internal problem-carrying
   result created via a new factory **`BenzeneResult.Problem<T>(ProblemDetails problem)`** (and
   non-generic) — status = `problem.BenzeneStatus` (required; throw a clear argument error when
   missing), errors projected from `problem.Errors`, unsuccessful. This is the API for a handler
   returning a rich, deliberate problem (custom `type`, `instance`, extension members via
   subclass).
2. **Typed accessor, kept small:** extension `GetProblem(this IBenzeneResult result)` in
   `Benzene.Results` — returns the attached document when the result implements
   `IHasProblemDetails`, else **synthesizes** one via `ProblemTypes.From(result)` (total function:
   consumers always get a document; document the synthesized-vs-received distinction in XML docs).
   No `TryGet` twin — null never escapes.
3. **Client round-trip** (`src/Benzene.Clients/Common/ClientResultExtensions.cs`): on failure,
   deserialize the body as `ProblemDetails`; when `errors` is present, populate the result's
   structured errors from it (closing the ruling §5.2 defect — today a two-error failure
   round-trips as one joined string); else fall back to a single message-only error from `detail`
   (unchanged behavior for old producers). Attach the received document so `GetProblem()` returns
   it (route the construction through the Phase 5.1 problem-carrying result). Status
   classification stays envelope-first exactly as today. Delete the `ErrorPayload` shim if Phase 3
   left one. Mirror the same read in any other failure-body reader
   (grep `Deserialize<ErrorPayload>` / `Deserialize<ProblemDetails>` — the Lambda client test
   fixtures at `test/.../LambdaResultExtensionTest.cs` show the shapes in play).
4. **Generated clients:** verify `Benzene.CodeGen.Client` output consumes failures only through
   `IBenzeneMessageSender`/`ClientResultExtensions` (expected — then it inherits all of this with
   zero template change); if any template mentions `ErrorPayload`, update it.
5. **gRPC** (`src/Benzene.Grpc/GrpcMethodHandler.cs`): `AddRichErrorDetails` fills
   `FieldViolation.Field` from `BenzeneError.Field` (empty→unset), description = message. Carrying
   `code` in `google.rpc.ErrorInfo` is parked (§8). Client side
   (`DefaultGrpcStatusReverseMapper` path): populate structured errors from `BadRequest` details
   when present.
6. **Tests:** round-trip (server emission → client result: fields/codes/order survive),
   `GetProblem` on received vs synthesized vs handler-authored results, `BenzeneResult.Problem`
   coherence rules, gRPC field-violation fill + reverse read.

**Acceptance:** a validation failure produced by a .NET service and consumed by the .NET client
yields `result.Errors` with field+code intact and `GetProblem().Type` ==
`https://benzene.app/problems/validation-error`; handler-authored problems pass through verbatim;
tests green.

---

## Phase 6 — .NET: conformance re-vendor + runner

**Repo:** benzene-dotnet. **Depends on:** Phases 1, 3 (and 5 for the canonical problem handler).
**Effort:** S.

1. Re-vendor `Benzene/docs/specification/conformance/*.json` → `test/conformance-fixtures/`
   **verbatim** (the CI snapshot check must stay byte-identical).
2. `EnvelopeConformanceTest`: implement `bodyExclude` (assert listed members absent from the
   parsed body) alongside the existing subset comparison.
3. New `ProblemDetailsConformanceTest` (`test/Benzene.Conformance.Test/`), fixture-driven like
   `StatusMappingConformanceTest`: registry rows against `ProblemTypes`; envelope cases through
   the pipeline with the new canonical `conformance:problem` handler (add it to
   `Handlers/`); the `httpRules` group against the AspNet-hosted pipeline (the repo's reference
   HTTP binding).
4. Full conformance suite green — this is the gate that Phases 3–5 actually implemented Phase 1.

**Acceptance:** vendored fixtures byte-identical to the spec repo; full
`Benzene.Conformance.Test` suite green; `bodyExclude` proven by a deliberately-broken local run
(emit `status` off-HTTP → red).

---

## Phase 7 — .NET docs

**Repo:** benzene-dotnet. **Depends on:** Phases 2–5 landed. **Effort:** S.

1. `docs/message-result.md` + `docs/message-handlers.md`: the problem document as the failure
   body; `BenzeneResult.Problem` for deliberate rich problems; `GetProblem` on the consumer side.
2. `docs/diagnosing-failures.md` + `docs/cookbooks/global-error-handling.md`: update the shapes
   shown; the type-registry table; the caller-facing (`type`/`code`) vs operator-facing
   (mesh classification) split with a pointer to the mesh docs.
3. Validation docs (`docs/cookbooks/fluentvalidation-*.md`, DataAnnotations/JsonSchema pages):
   the `errors` member, which integration populates `field`/`code` (the ruling's capability
   table).
4. **Create `docs/migration-alpha-to-1.0.md`** (owed since the ruling; still absent): entries for
   the `Errors` model change (Phase 2) and the wire/`ProblemDetails` changes (Phases 3–4).
   CHANGELOG entries per phase should already exist — verify.
5. Every touched page reachable from `docs/index.md`; run the Benzene repo website generator with
   `--dotnet-docs` pointing at this tree — 0 broken-link warnings.

**Acceptance:** docs match shipped behavior; migration doc exists; website build clean.

---

## Phase 8 — Other ports (re-vendor notes, not scheduled here)

TypeScript, Python, and Go implement from **the spec + fixtures alone** (Phase 1 is deliberately
sufficient: the registry is data, `bodyExclude` is specified, and the canonical
`conformance:problem` handler pins the behavior), on their own schedules:

- Each port: re-vendor `conformance/*.json`, implement §1.3/§3.1/§4.1 emission + read-side, run
  the fixtures. The shape was chosen to be trivial off-.NET (flat members, one array, no URI
  dereferencing, no registry code — a table).
- Until a port updates, its `{status, detail}` payloads interop with updated consumers via
  `detail` + envelope classification (§2.4) — no coordination window required.
- Client-side consumption patterns for the ports (typed accessors, generated clients) belong to
  the cross-language client work — see `work/archive/cross-language-clients-plan-2026-08.md` (parallel plan;
  referenced by path, keep the two consistent at merge time).

---

## Out of scope (recorded so they aren't invented mid-implementation)

- **Retry semantics and dead-letter policy** — the problem document *describes* a failure; what a
  transport does about it stays where it is (`RetryBenzeneMessageClient`, transport DLQ config,
  `work/archive/results-taxonomy-plan-2026-08.md` D4).
- **Dereferenceable `instance` URLs** — optional per RFC 9457; the framework never fabricates
  `instance`. Applications may set it via `BenzeneResult.Problem`.
- **Serving `https://benzene.app/problems/*` as live registry pages** — the URIs are opaque
  identifiers first (RFC 9457 §3.1.1 explicitly permits non-dereferenceable types). A website
  registry page is a cheap later follow-up in the Benzene repo; parked, not required for
  conformance.
- **Mesh UI rendering of problems** — parked note: the issue drill-in could later show the
  problem `type` of exemplar failures; nothing in this plan blocks it (the mesh wire shapes are
  untouched).
- **`ex.Message` in unhandled-exception `detail`** — kept as-is (Phase 3.4); hardening to
  type-only on the wire is a separate behavior decision if ever taken.
- **`google.rpc.ErrorInfo` carrying `code` over gRPC**; **typed extension-member bag on
  `ProblemDetails`** (subclassing covers it); **structured factory overloads beyond
  `Set`/`ValidationError`/`BadRequest`** (additive later per the ruling).

## ⚠️ FLAGS — approved by approving this plan

1. Wire break (pre-1.0, decision 4): failure body member `status` (string) →
   `benzeneStatus`; conformance fixtures updated accordingly; ports must re-verify.
2. `Benzene.Results.ProblemDetails.Status` changes type string→`int?`; `ErrorPayload` is deleted.
   Both packed-alpha compile breaks, CHANGELOG'd + migration-doc'd.
3. Phase 2 lands the already-approved `IBenzeneResult.Errors` break (ruling flags apply).
4. Between Phase 1 and Phase 6 the .NET conformance suite is red against the new fixtures — land
   the phases promptly in order; do not "fix" fixtures to green it early.

## Suggested agent task slicing

| Task | Phase | Repo | Parallel-safe with |
|---|---|---|---|
| T1 | Phase 1 (spec + fixtures) | Benzene | T2 |
| T2 | Phase 2 (BenzeneError model) | benzene-dotnet | T1 |
| T3 | Phase 3 (ProblemDetails + emission) | benzene-dotnet | — (after T1+T2) |
| T4 | Phase 4 (HTTP bindings) | benzene-dotnet | T5 (different files) |
| T5 | Phase 5 (clients/accessor/gRPC) | benzene-dotnet | T4 |
| T6 | Phase 6 (conformance) | benzene-dotnet | after T3–T5 |
| T7 | Phase 7 (docs) | benzene-dotnet | after T5; refresh at the end |
| T8 | Phase 8 (ports) | per-port repos | own schedule; needs only T1 |

Each task: read this plan's §1–§3 + its phase + the cited source documents; verify cited files;
build + test; commit per phase; report what was verified vs assumed.
