# Result & Status Reference

Every message handler returns its response wrapped in `IBenzeneResult<T>` (or `IBenzeneResult`
for handlers with no payload). The result carries a **status** — success, not found, validation
error, and so on — alongside the payload or error. Transports map that status onto their native
response (HTTP status codes, etc.), so your handler expresses intent once and it's translated
everywhere.

You build results with the static `BenzeneResult` factory (in `Benzene.Results`). For the
conceptual introduction, see [Message Results](../message-result.md).

```csharp
public Task<IBenzeneResult<OrderDto>> HandleAsync(GetOrderRequest request)
{
    var order = _repository.Find(request.Id);
    return order is null
        ? Task.FromResult(BenzeneResult.NotFound<OrderDto>())
        : Task.FromResult(BenzeneResult.Ok(order));
}
```

## Statuses

The status strings are defined as constants on `BenzeneResultStatus` (`Benzene.Results`). This
table lists every status, the factory method that produces it, whether it counts as success, and
the HTTP status code HTTP transports map it to (via `DefaultHttpStatusCodeMapper`).

### Success statuses

| Factory | Status | HTTP | Notes |
|---|---|---|---|
| `BenzeneResult.Ok(payload)` | `ok` | `200` | Standard success with a payload. |
| `BenzeneResult.Created(payload)` | `created` | `201` | Resource created. |
| `BenzeneResult.Accepted()` | `accepted` | `202` | Acknowledged for async processing. The **default** result for fire-and-forget event handlers (those with no response). |
| `BenzeneResult.Updated(payload)` | `updated` | `204` | Resource updated; no content returned. |
| `BenzeneResult.Deleted<T>()` | `deleted` | `204` | Resource deleted; no content returned. |
| `BenzeneResult.Ignored<T>()` | `ignored` | `200` | Message deliberately not acted upon but acknowledged as handled. |

### Failure statuses

| Factory | Status | HTTP | Notes |
|---|---|---|---|
| `BenzeneResult.BadRequest(message)` | `bad-request` | `400` | Malformed or invalid request. |
| `BenzeneResult.Unauthorized()` | `unauthorized` | `401` | Authentication required or failed. |
| `BenzeneResult.Forbidden()` | `forbidden` | `403` | Authenticated but not permitted. |
| `BenzeneResult.NotFound<T>()` | `not-found` | `404` | Resource does not exist. |
| `BenzeneResult.Conflict()` | `conflict` | `409` | Conflicts with current state. |
| `BenzeneResult.ValidationError(message)` | `validation-error` | `422` | Request failed validation. Returned automatically by [validation middleware](middleware.md#message-router-middleware). |
| `BenzeneResult.TooManyRequests()` | `too-many-requests` | `429` | Throttled / rate limited; transient — back off and retry. |
| `BenzeneResult.UnexpectedError(message)` | `unexpected-error` | `500` | Unhandled/unexpected failure. |
| `BenzeneResult.NotImplemented()` | `not-implemented` | `501` | Operation not implemented. |
| `BenzeneResult.ServiceUnavailable()` | `service-unavailable` | `503` | A dependency is unavailable; safe to retry. |
| `BenzeneResult.Timeout()` | `timeout` | `504` | A downstream deadline elapsed; transient, but the operation may or may not have been applied — retry only if idempotent. |

> A `null` status falls back to **HTTP 500**. A status not in the map (an application-defined
> status) falls back to **HTTP 200** when the result's `IsSuccessful` is `true`, else **500** — see
> [Using an application-defined status](#using-an-application-defined-status).

## Payload vs. no-payload overloads

Each factory has two forms:

- `IBenzeneResult<T>` — carries a typed payload. On success you pass the value
  (`BenzeneResult.Ok(order)`); on failure you specify the type parameter
  (`BenzeneResult.NotFound<OrderDto>()`) since there's no payload.
- `IBenzeneResult` — no payload, for handlers declared as `IMessageHandler<TMessage>`.

```csharp
BenzeneResult.Ok(new OrderDto { /* … */ });   // IBenzeneResult<OrderDto>
BenzeneResult.NotFound<OrderDto>();            // IBenzeneResult<OrderDto>, no payload
BenzeneResult.Accepted();                      // IBenzeneResult, no payload
```

Failure factories that describe an error (`BadRequest`, `ValidationError`, `UnexpectedError`, …)
accept an optional message:

```csharp
BenzeneResult.ValidationError<OrderDto>("Name is required");
BenzeneResult.BadRequest("Invalid request");
```

## Helpers and lower-level building

| Member | Purpose |
|---|---|
| `BenzeneResult.Set<T>(status, isSuccess)` | Build a result with an explicit status string and success flag — the escape hatch for custom statuses. (The non-generic `Set(status)` derives success from the status class, so it's only valid for one of the framework's known statuses — see below.) |
| `BenzeneResult.Set(status, payload, isSuccess)` | Explicit status *and* payload *and* success flag — for results whose success class shouldn't be derived from the status (e.g. an unhealthy health check: `service-unavailable` for the HTTP 503, successful so the report payload renders as the body). This is also the *only* way to give a custom status a payload — `Set<T>(status, payload)` throws for an application-defined status. |
| `BenzeneResult.SetFailed<T>(status, errors)` | Build a *failed* result under a custom status with error messages, no payload. Replaces the obsolete `Set<T>(status, params string[] errors)`, which a single string argument could silently bind to instead of the payload overload. |
| `result.IsSuccess()` | Extension method — true when the result's status is a success status. |
| `result.IsAccepted()` | Extension method — true when the result is `accepted`. |
| `*Internal` factories (`OkInternal`, `NotFoundInternal`, …) | Variants used for internal/inter-service results — e.g. results returned across a Benzene [message client](packages.md#outbound-messaging-clients) rather than mapped straight to an HTTP response. |

## Classifying statuses

`BenzeneResultStatus` is the single owner of what each status *means* — the transport mappers,
clients, and `IsSuccessful` all derive from it:

| Member | Purpose |
|---|---|
| `BenzeneResultStatus.IsSuccess(status)` | True for the six success statuses. |
| `BenzeneResultStatus.IsFailure(status)` | True for the known failure statuses. Application-defined statuses are neither. |
| `BenzeneResultStatus.IsKnown(status)` | True for any framework-defined status. |
| `BenzeneResultStatus.IsTransient(status)` | True for `service-unavailable`, `too-many-requests`, and `timeout` — a later retry may succeed. |
| `result.IsTransient()` | Extension form of the above. |

`IsSuccessful` on a result built with `BenzeneResult.Set(status)` / `Set<T>(status, payload)` is
derived from the status class: known failure statuses produce `IsSuccessful == false`; success
statuses produce `IsSuccessful == true`. An application-defined status isn't in either class, so
these overloads **throw `ArgumentException`** for one rather than silently guessing — use
`Set<T>(status, isSuccessful)` / `Set<T>(status, payload, isSuccessful)` to state it explicitly.

**Retrying:** `RetryBenzeneMessageClient` (`Benzene.Clients`) retries `service-unavailable` and
`too-many-requests` by default. `timeout` is transient but *not* retried by default — a timed-out
operation may have been applied, so blind retries are only safe for idempotent calls; opt in via
its `shouldRetry` constructor parameter (e.g. `r => BenzeneResultStatus.IsTransient(r.Status)`).

## Using an application-defined status

Applications may use their own status strings (`BenzeneResult.Set("quarantined", payload, true)`),
with caveats:

- An application-defined status has no known success class, so `Set<T>(status, payload)` **throws**
  — use `Set<T>(status, payload, isSuccessful)` to state it explicitly. There is no silent default.
- Transports honor `IsSuccessful` for an unmapped status by default: HTTP maps to `200`, gRPC to
  `OK`, when `IsSuccessful` is `true` — `500`/`Internal` (a thrown `RpcException` for gRPC)
  otherwise. To map your status to something more specific, replace `IHttpStatusCodeMapper`
  (`Benzene.Http`) / `IGrpcStatusCodeMapper` (`Benzene.Grpc`). Both are registered with `TryAdd`, so
  a registration in `ConfigureServices` wins.
- Custom **validation** statuses: FluentValidation's per-rule `.WithStatus(...)`, the handler-level
  `[ValidationStatus]` attribute, and the replaceable `IValidationStatusMapper` set the status on
  validation failures; `IDefaultStatuses` replaces the framework's own defaults.
- **Benzene clients round-trip custom statuses**, `IsSuccessful` included: the response envelope
  carries an explicit `isSuccessful` field (wire-contracts.md §1.2) that a receiving client reads
  directly. A sender that predates this field is still classified from the status text, so a
  custom status from one arrives as `unexpected-error`.
- **The overload trap is now `[Obsolete]`**: `Set<T>(status, params string[] errors)` — the
  overload a single string argument could silently bind to instead of the payload overload — is
  obsoleted in favor of `SetFailed<T>(status, errors)` (errors case) and
  `Set<T>(status, payload, isSuccessful: true)` (string payload case).

## Problem-type registry

Every failed result is served on the wire as an [RFC 9457 problem
document](../message-result.md#problem-documents-rfc-9457) (`Benzene.Results.ProblemDetails`). The
document's `type`/`title` come from `Benzene.Results.ProblemTypes`, a static registry keyed by the
same status vocabulary as the tables above — one row per failure status, plus a fallback for an
application-defined one:

| `benzeneStatus` | `type` (`https://benzene.app/problems/` + ) | `title` | HTTP `status` |
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
| *(application-defined status)* | `null` — or your own URI via `BenzeneResult.Problem(...)` | `null` | 500 (the unknown-status fallback, same row as the status table above) |

`ProblemTypes.TypeFor(status)` / `.TitleFor(status)` / `.HttpStatusFor(status)` expose this table in
code; `ProblemTypes.From(result)` is the factory that builds a full `ProblemDetails` document from an
ordinary result — `DefaultResponsePayloadMapper` calls it for every failed result automatically, so
application code rarely calls it directly. Treat `type` as an opaque identifier: compare it by string
equality, never dereference it as a URL. See [Message Results — Problem
documents](../message-result.md#problem-documents-rfc-9457) for the full document shape, an example,
`BenzeneResult.Problem(...)` (a handler returning its own rich problem), and `GetProblem()` (reading
one back).

## Mapping in both directions

- **Outbound (handler → transport):** `DefaultHttpStatusCodeMapper` (`Benzene.Http`) converts a
  result status to an HTTP status code using the table above. Non-HTTP transports apply their
  own conventions.
- **Inbound (transport → result):** when a Benzene client calls another service over HTTP,
  `BenzeneResultHttpMapper` (`Benzene.Clients`) converts the received HTTP status code back into
  a `BenzeneResult` status, so calling code sees the same result model regardless of transport.

## See also

- [Message Results](../message-result.md) — the conceptual introduction, including problem documents,
  `BenzeneResult.Problem(...)`, and `GetProblem()`.
- [Message Handlers](../message-handlers.md) — where results are returned.
- [Fluent Validation](../fluent-validation.md) / [Data Annotations](../data-annotations.md) — produce `validation-error` results automatically.
