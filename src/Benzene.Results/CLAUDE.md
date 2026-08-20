# Benzene.Results

## What this package does
Concrete result types and helpers for Benzene message handlers. Handlers return an
`IBenzeneResult`/`IBenzeneResult<T>` (the interfaces live in `Benzene.Abstractions.Results`) built
through this package's `BenzeneResult` factory, so success/failure is modelled explicitly with a
status string rather than thrown exceptions. Transport adapters map the status to their own codes
(HTTP status, etc.).

## Key types/interfaces
- `BenzeneResult` - static factory for building results. Named helpers (`Ok`, `Created`, `Accepted`,
  `Updated`, `Deleted`, `Ignored`, `NotFound`, `BadRequest`, `ValidationError`, `Conflict`,
  `Forbidden`, `Unauthorized`, `ServiceUnavailable`, `NotImplemented`, `UnexpectedError`,
  `TooManyRequests`, `Timeout`), plus low-level `Set(...)` overloads including
  `Set<T>(status, payload, isSuccessful)` for the case where the success class must not be derived
  from the status (e.g. a health check reporting `ServiceUnavailable` while staying successful so
  the body renders the report). Every failure-carrying factory keeps its original
  `params string[] errors` overload (projected to message-only `BenzeneError`s); `Set`/`Set<T>`,
  `ValidationError`/`ValidationError<T>` and `BadRequest`/`BadRequest<T>` additionally have a
  non-`params` `IReadOnlyList<BenzeneError>` overload for callers that already have structured
  errors (field/code, not just text) - see `work/benzene-result-errors-ruling.md`.
- `BenzeneResultStatus` - the framework-defined status-string vocabulary (`const string` values)
  plus classifiers `IsSuccess`, `IsFailure`, `IsKnown`, `IsTransient`. Success is derived from the
  status class: a known failure status yields an unsuccessful result even with a payload; unknown
  application-defined statuses default to successful.
- `BenzeneResultExtensions` - `IsOk`/`IsNotFound`/`IsTransient`/... status predicates, `As<...>`
  mapping/projection helpers (sync and `Task`-returning), `AsTask`, `HttpStatusCode.Convert(...)`
  to/from Benzene statuses, and `ErrorMessages()` - projects a result's structured
  `IBenzeneResult.Errors` (`IReadOnlyList<BenzeneError>`) down to the pre-`BenzeneError` `string[]`
  shape, for callers that only want the message text.
- `ProblemDetails` - the RFC 9457 ("Problem Details for HTTP APIs") problem document Benzene emits
  in place of the payload whenever a result is unsuccessful, on every transport (see
  `docs/specification/wire-contracts.md` §1.3/§3.1 in the cross-language Benzene repo): `Type`,
  `Title`, `Status` (`int?`, HTTP bindings only - never fabricated off-HTTP), `Detail` (the result's
  error messages joined with `", "`), `Instance`, `BenzeneStatus` (the transport-neutral
  discriminator, mirrors the envelope's `statusCode`), `Errors` (`IReadOnlyList<BenzeneError>?`,
  present only when the result carries structured errors). Every member is
  `[JsonIgnore(Condition = WhenWritingNull)]` so an absent member is omitted from the wire, not
  emitted as `null` - load-bearing for `Status` off an HTTP transport. Build one from a result with
  `ProblemTypes.From(result)` rather than constructing it by hand; an application needing its own
  extension members should subclass it (the retired `ErrorPayload`'s pattern - see below).
- `ProblemTypes` - the problem-type registry, keyed by `BenzeneResultStatus`'s failure vocabulary
  (no new taxonomy): `TypeFor`/`TitleFor`/`HttpStatusFor(status)` (unknown/application-defined
  status → `null`/`null`/`500`) plus the factory `From(IBenzeneResult)` that builds the `§2.1`
  document (`Type`/`Title` from the registry, `Detail` joined exactly as the retired `ErrorPayload`
  did, `Errors` from the result's structured errors when non-empty, `Status` deliberately never set
  - that's an HTTP-binding concern for a later phase). `DefaultResponsePayloadMapper`
  (`Benzene.Core.MessageHandlers`) calls this on every failed result.
  - **`ErrorPayload` (retired):** the pre-RFC-9457 `{ status, detail }` shape this type used to be is
    gone (Phase 3 of `work/archive/problem-details-plan-2026-08.md`, clean break, no shim) - `ProblemDetails` /
    `ProblemTypes.From` took over both of its jobs (join-`detail` construction; client-side
    deserialization target).

### Structured errors - which validation integration populates what
`BenzeneError` (`Benzene.Abstractions.Results`) is `{ Message, Field?, Code? }`. Each validation
integration populates `Field`/`Code` differently - see each package's own `CLAUDE.md` for detail:

| Integration | `Field` | `Code` |
|---|---|---|
| `Benzene.FluentValidation` | `ValidationFailure.PropertyName` (.NET property path) | `ValidationFailure.ErrorCode`, verbatim (e.g. `NotEmptyValidator` - not stripped/normalized) |
| `Benzene.DataAnnotations` | `ValidationResult.MemberNames` (one error per member; `null` when the result names no member) | always `null` - `ValidationResult` doesn't expose the attribute that produced it |
| `Benzene.JsonSchema` | the failing value's JSON Pointer (e.g. `/name`; `null` at the root) | the failed schema keyword (e.g. `maxLength`, `required`) |

Note: `IBenzeneResult` and `IBenzeneResult<T>` themselves (and `Void`) are declared in
`Benzene.Abstractions.Results`, not here. This package supplies the concrete builders and helpers.

## When to use this package
- Anywhere a handler needs to return a result — use `BenzeneResult.Ok(...)` / `.NotFound(...)` etc.
  instead of throwing for expected outcomes.
- When mapping between HTTP status codes and Benzene statuses.
- When inspecting a result's status class (success/failure/transient) in middleware.

## Dependencies on other Benzene packages
- **Benzene.Abstractions** - for `IBenzeneResult`, `IBenzeneResult<T>`, and `Void`.

## Important conventions
- Return `IBenzeneResult`/`IBenzeneResult<T>` from handlers instead of throwing for expected outcomes.
- Status strings are the case-sensitive wire vocabulary — use the `BenzeneResultStatus` constants.
- `IsSuccessful` is derived from the status class unless an explicit `isSuccessful` overload is used.
- `IsTransient` marks retry-*eligible* statuses only; it is not a retry-*safety* guarantee (a
  `Timeout` leaves the operation's application state unknown — see `BenzeneResultStatus.IsTransient`).

## Tests
Covered by `test/Benzene.Core.Test` (result construction, status classification, and
`HttpStatusCode` conversion).
