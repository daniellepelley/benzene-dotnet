# Message Results

Every [message handler](message-handlers.md) returns its outcome wrapped in an `IBenzeneResult<T>` (or
`IBenzeneResult` for handlers with no payload) instead of throwing for expected failure cases. The
result carries a status, a success flag, the payload (on success), and error messages (on failure).
Build one with the static `BenzeneResult` factory (`Benzene.Results`) — you should not need to
implement `IBenzeneResult<T>` yourself.

## `IBenzeneResult` / `IBenzeneResult<T>`

Defined in `Benzene.Abstractions.Results`:

```csharp
public interface IBenzeneResult
{
    string Status { get; }
    bool IsSuccessful { get; }
    object PayloadAsObject { get; }
    IReadOnlyList<BenzeneError> Errors { get; }
}

public interface IBenzeneResult<T> : IBenzeneResult
{
    T Payload { get; }
}
```

`Status` is a plain string (see [`BenzeneResultStatus`](#benzeneresultstatus) below) — not a .NET
`enum` — which is what lets transport-specific status mappers (HTTP status codes, SQS
acknowledgement, ...) key off it without a hard dependency on `Benzene.Results` itself.

`Errors` (`Benzene.Abstractions.Results`) is a list of `BenzeneError { Message, Field?, Code? }`, not
bare strings — `Message` is the human-readable text, `Field`/`Code` are populated by whichever
producer built the error (a validation integration, or your own handler) when it has something
structured to say; both are `null` for a plain `BenzeneResult.NotFound("Order 123 not found")`-style
message. A success result's `Errors` is always an empty list, never `null`. `BenzeneError.ToString()`
returns `Message`, so a pre-existing `string.Join(", ", result.Errors)` still compiles and produces
the same text; `result.ErrorMessages()` (`Benzene.Results`) does the same projection as an explicit
`string[]`. See [Problem documents](#problem-documents-rfc-9457) below for where `Field`/`Code` end up
on the wire, and [Fluent Validation](fluent-validation.md#structured-errors-on-the-wire-field-and-code) /
[Data Annotations](data-annotations.md#structured-errors-on-the-wire-field-and-code) for which
integration populates what.

## `BenzeneResult` factory

Static factory methods on `Benzene.Results.BenzeneResult`, each with a generic `<T>` overload (for
handlers with a payload) and a non-generic overload (for `IMessageHandler<TRequest>`/`Void`
payloads):

```csharp
BenzeneResult.Ok(new OrderDto());          // BenzeneResult.Ok<T>()  also available (default payload)
BenzeneResult.Created(new OrderDto());     // BenzeneResult.Created<T>()
BenzeneResult.Accepted(new OrderDto());    // BenzeneResult.Accepted<T>() / BenzeneResult.Accepted()
BenzeneResult.Updated(new OrderDto());     // BenzeneResult.Updated<T>()
BenzeneResult.Deleted(new OrderDto());     // BenzeneResult.Deleted<T>()
BenzeneResult.Ignored<OrderDto>();         // BenzeneResult.Ignored()

BenzeneResult.NotFound<OrderDto>("Order 123 not found");
BenzeneResult.BadRequest<OrderDto>("Invalid request");
BenzeneResult.ValidationError<OrderDto>("Name is required");
BenzeneResult.Forbidden<OrderDto>();
BenzeneResult.Unauthorized<OrderDto>();
BenzeneResult.Conflict<OrderDto>();
BenzeneResult.ServiceUnavailable<OrderDto>();
BenzeneResult.NotImplemented<OrderDto>();
BenzeneResult.UnexpectedError<OrderDto>("Something went wrong");
```

All the error-style factories (`NotFound`, `BadRequest`, `ValidationError`, `Forbidden`,
`Unauthorized`, `Conflict`, `ServiceUnavailable`, `NotImplemented`, `UnexpectedError`) accept
`params string[] errors` and produce `IsSuccessful == false`. There's also a lower-level escape
hatch, `BenzeneResult.Set(status, ...)`, for a custom status string that isn't one of the built-ins
— used internally (e.g. `MessageRouter<TContext>` sets `validation-error`/`not-found` results this way
when a topic is missing or unmatched). `BenzeneResult.SetFailed<T>(status, errors)` is the
equivalent for a custom *failure* status (no payload) — see
[Using an application-defined status](#using-an-application-defined-status) below before reaching
for either.

### `BenzeneResultExtensions`

`Benzene.Results.BenzeneResultExtensions` adds `Is*()` checks (`IsOk`, `IsCreated`, `IsNotFound`,
`IsValidationError`, etc.) mirroring every status, plus `.As<TOutput>(...)` helpers for remapping a
result's payload type while preserving its status/success/errors — handy when adapting one
handler's result to another shape. It also has `HttpStatusCode.Convert()` / `Convert<T>()`
extensions that go the other direction: turning a raw `HttpStatusCode` (e.g. from an outbound HTTP
call inside a handler) into an `IBenzeneResult`/`IBenzeneResult<T>`.

## Problem documents (RFC 9457)

Whenever a result is unsuccessful, the payload it would have carried is replaced on the wire by a
**problem document** — an [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) "Problem Details"
object, the same shape on every transport. `Benzene.Results.ProblemDetails` is the wire type:

```csharp
public class ProblemDetails
{
    public string? Type { get; set; }
    public string? Title { get; set; }
    public int? Status { get; set; }
    public string? Detail { get; set; }
    public string? Instance { get; set; }
    public string? BenzeneStatus { get; set; }
    public BenzeneError[]? Errors { get; set; }
}
```

You never build this by hand for the ordinary case — `BenzeneResult.NotFound(...)`,
`.ValidationError(...)`, and every other failure factory get one for free. Every optional member is
**omitted from the wire, not emitted as `null`**, when it doesn't apply — most visibly `Status`,
which only ever appears on an HTTP-bound response (an envelope over a queue or a direct invocation
never carries a fabricated HTTP status number). A `validation-error` produced by a FluentValidation
failure and served over HTTP looks like this on the wire:

```json
{
  "type": "https://benzene.app/problems/validation-error",
  "title": "Validation failed",
  "status": 422,
  "detail": "Name must not be empty, Age must be greater than 0",
  "benzeneStatus": "validation-error",
  "errors": [
    { "message": "Name must not be empty", "field": "Name", "code": "NotEmptyValidator" },
    { "message": "Age must be greater than 0", "field": "Age", "code": "GreaterThanValidator" }
  ]
}
```

- `type`/`title` come from the [problem-type registry](reference/results.md#problem-type-registry),
  keyed by the result's `Status`.
- `detail` is the result's `Errors` joined with `", "` — the one member every pre-RFC-9457 reader
  already used, unchanged.
- `benzeneStatus` mirrors the result's `Status` string — the transport-neutral discriminator every
  reader should classify on; `status` (numeric) only exists for HTTP.
- `errors`, when present, is the result's structured `BenzeneError`s. See
  [Fluent Validation](fluent-validation.md#structured-errors-on-the-wire-field-and-code) /
  [Data Annotations](data-annotations.md#structured-errors-on-the-wire-field-and-code) for which
  integration populates `field`/`code` and how.
- `instance` is never set by the framework — it's an application-owned member (see below).

`DefaultResponsePayloadMapper` builds this document via `ProblemTypes.From(result)` for every failed
result automatically; HTTP-facing transports additionally fill in `status` from the same mapper that
decides the actual HTTP response code, so the two can never disagree (see
[Transport mapping](#transport-mapping) below). `SerializerResponseRenderer` also rewrites the
response `content-type` to `application/problem+json` (or `application/problem+xml`) on failure.

### Returning a rich problem with BenzeneResult.Problem

The registry covers the ordinary case. For a handler that wants to return something the registry
can't express — a custom `type` outside it, an `instance` URI identifying this specific occurrence,
or extension members via a `ProblemDetails` subclass — build the document yourself and hand it to
`BenzeneResult.Problem(...)`:

```csharp
public class OutOfStockProblem : ProblemDetails
{
    public int AvailableQuantity { get; set; }
}

public async Task<IBenzeneResult<OrderDto>> HandleAsync(CreateOrderMessage request)
{
    var available = await _stock.AvailableAsync(request.Sku);
    if (available < request.Quantity)
    {
        return BenzeneResult.Problem<OrderDto>(new OutOfStockProblem
        {
            Type = "https://example.com/problems/out-of-stock",
            Title = "Not enough stock",
            Detail = $"Only {available} of '{request.Sku}' left.",
            Instance = $"urn:order-sku:{request.Sku}",
            BenzeneStatus = BenzeneResultStatus.Conflict,
            AvailableQuantity = available,
        });
    }

    return await _orderService.SaveAsync(request);
}
```

`problem.BenzeneStatus` is required — it becomes the result's `Status` (and therefore drives every
transport mapping below), so `BenzeneResult.Problem(...)` throws `ArgumentException` naming the fix
if it's missing. The result this returns is always unsuccessful, and the document you passed in is
attached to it verbatim (not re-derived from the registry) — retrieve it with `GetProblem()` below.
There's also a non-generic `BenzeneResult.Problem(problem)` for `IMessageHandler<TRequest>` handlers.

### Reading a problem document back — `GetProblem()`

`GetProblem(this IBenzeneResult result)` (`Benzene.Results`) is the one API you need on the reading
side — whether the result came from your own handler's `BenzeneResult.Problem(...)`, from a Benzene
client's failure response, or from an ordinary `BenzeneResult.NotFound(...)` you never attached a
document to:

```csharp
ProblemDetails problem = result.GetProblem();
```

It never returns `null` — there's no `TryGetProblem` twin. If the result carries a
deliberately-attached document (built via `BenzeneResult.Problem(...)`, or received over the wire by
a Benzene client, which attaches exactly what it deserialized) you get that document back verbatim;
otherwise one is synthesized on the spot via `ProblemTypes.From(result)`. Callers don't need to know,
or care, which case they're in.

## `BenzeneResultStatus`

A static class of string constants (`Benzene.Results`), **not** a .NET `enum`:

```csharp
public static class BenzeneResultStatus
{
    public const string Accepted = "accepted";
    public const string Ok = "ok";
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Deleted = "deleted";
    public const string Ignored = "ignored";
    public const string NotFound = "not-found";
    public const string BadRequest = "bad-request";
    public const string ValidationError = "validation-error";
    public const string ServiceUnavailable = "service-unavailable";
    public const string NotImplemented = "not-implemented";
    public const string UnexpectedError = "unexpected-error";
    public const string Conflict = "conflict";
    public const string Forbidden = "forbidden";
    public const string Unauthorized = "unauthorized";
}
```

## Using an application-defined status

`Status` being a plain string means an application can use statuses beyond the built-ins —
`BenzeneResult.Set("quarantined", payload)` builds one. Know what you're opting into:

- **Success classification is mandatory, not derived.** `Set<T>(status, payload)` only derives
  `IsSuccessful` for one of the framework's own known statuses (`BenzeneResultStatus`) — for an
  application-defined status it **throws `ArgumentException`**, naming the fix. Use
  `Set<T>(status, isSuccessful)` / `Set<T>(status, payload, isSuccessful)` to state it explicitly;
  there is no silent default.
- **Transport mapping honors `IsSuccessful` by default.** `DefaultHttpStatusCodeMapper` /
  `DefaultGrpcStatusCodeMapper` map an unknown status to their generic-**success** row (`200`/`OK`)
  when the result's `IsSuccessful` is `true`, and to the generic-error row (`500`/`Internal`)
  otherwise — no split-brain between transport code and payload. To map your status to something
  more specific than the generic row, replace `IHttpStatusCodeMapper` (`Benzene.Http`) /
  `IGrpcStatusCodeMapper` (`Benzene.Grpc`); both are registered with `TryAdd`, so register yours in
  `ConfigureServices` and it wins.
- **Validation.** To make validation failures carry your status instead of `validation-error`,
  FluentValidation supports per-rule `.WithStatus(...)`, the handler-level
  `[ValidationStatus("...")]` attribute, and a replaceable `IValidationStatusMapper`; the
  framework's own routing/validation defaults come from a replaceable `IDefaultStatuses`.
- **Benzene clients round-trip custom statuses**, including `IsSuccessful`: the `BenzeneMessage`
  response envelope (wire-contracts.md §1.2) carries an explicit `isSuccessful` field alongside
  `statusCode`, and a receiving Benzene client (this version or later) reads it directly rather than
  guessing from the status text. A receiver talking to an older sender that predates this field
  falls back to classifying by the known-status vocabulary, so a custom status from such a sender
  still arrives as `unexpected-error`.
- **The string-payload overload trap is now a compile-time signal.** `Set<T>(status, params
  string[] errors)` — the overload a single string argument could silently bind to instead of the
  payload overload — is `[Obsolete]`; use `SetFailed<T>(status, errors)` for the errors case (same
  behavior, unambiguous name) and `Set<T>(status, payload, isSuccessful: true)` for a genuine string
  payload.

## Transport mapping

### HTTP

`Benzene.Http`'s `DefaultHttpStatusCodeMapper` (`IHttpStatusCodeMapper`) maps every
`BenzeneResultStatus` value onto an HTTP status code; an unrecognized status defaults to `200` when
the result's `IsSuccessful` is `true`, else `500` (a null status is always `500`):

| Status | HTTP code |
|---|---|
| `ok`, `ignored` | 200 |
| `created` | 201 |
| `accepted` | 202 |
| `updated`, `deleted` | 204 |
| `bad-request` | 400 |
| `unauthorized` | 401 |
| `forbidden` | 403 |
| `not-found` | 404 |
| `conflict` | 409 |
| `validation-error` | 422 |
| `unexpected-error`, or unmapped + `IsSuccessful == false` | 500 |
| unmapped + `IsSuccessful == true` | 200 |
| `not-implemented` | 501 |
| `service-unavailable` | 503 |

`HttpStatusCodeResponseHandler<TContext>` applies this mapping to the HTTP response via
`IBenzeneResponseAdapter<TContext>`. On success, `SerializerResponseRenderer<TContext>` (see
[Message Handlers](message-handlers.md#response-handling)) serializes `Payload`; on failure, it
serializes the [problem document](#problem-documents-rfc-9457) described above — so a
`BenzeneResult.NotFound<OrderDto>("Order 123 not found")` becomes an HTTP `404` with an
`application/problem+json` body describing the error, not the (empty) `OrderDto` payload. On every
HTTP-facing context (`Benzene.AspNet.Core`, API Gateway v1/v2, the Lambda/Azure/Google Cloud HTTP
hosts), the problem document's `status` member is filled in from this same mapping, so the body's
`status` and the response line's HTTP code can never disagree.

### Async/event transports — settlement (ack/nack/checkpoint)

For queues, streams, and event triggers there is no synchronous HTTP status to return to a caller;
instead the result's `IsSuccessful` flag decides whether the message is **settled** (acked/completed/
checkpointed) or **redelivered** (nacked/abandoned/left for retry). Each transport's result-setter
records the outcome on the context's `IHasMessageResult.MessageResult`, and the transport's
application/worker reads that back to settle the message.

**As of the 1.0 settlement contract, every queue-shaped transport is safe by default**: a returned
failure result (`IsSuccessful == false`, e.g. `validation-error`/`not-found`/`service-unavailable`) —
**or** an unset/null result, e.g. an unroutable message no handler matched — is redelivered
(at-least-once), exactly like an unhandled exception, rather than being silently settled. The two
self-hosted **stream** workers (`Benzene.Kafka.Core`, `Benzene.Azure.EventHub`) are the deliberate
exception and default to at-most-once. The full per-transport table — the default on a returned
failure result and the exact opt-in/opt-out knob for each (`SqsOptions.BatchFailureMode`,
`SnsOptions.RaiseOnFailureStatus`, `ServiceBusOptions.AckMode`, `CommitOnlyOnSuccess`, …) — is the
single source of truth in the **[Capability Matrix](capability-matrix.md#retry-on-handler-failure-result--the-per-transport-breakdown)**.
Because a redelivered message re-runs the handler, **any handler on an at-least-once transport must be
idempotent** — see [Idempotency](cookbooks/idempotency.md).

Two representative examples:

- **AWS SQS** (`Benzene.Aws.Lambda.Sqs`) — batch-based: `SqsApplication` reports every record whose
  `MessageResult` is unsuccessful **or unset**, or that threw, back to Lambda as an
  `SQSBatchResponse.BatchItemFailure`, so SQS retries (or dead-letters, per your redrive policy) only
  those records — successfully-handled records in the same batch are not reprocessed. Configurable via
  `SqsOptions.BatchFailureMode` (default `PartialBatchFailure`; `FailWholeBatch` retries the whole
  batch on any failure) — see [Handling SQS Message Failures](cookbooks/handling-sqs-failures.md).
- **AWS SNS** (`Benzene.Aws.Lambda.Sns`) — one notification per invocation, no per-record ack API, so
  settlement rides on whether the invocation throws. `SnsMessageHandlerResultSetter` records the
  result; `SnsOptions.RaiseOnFailureStatus` (**default `true`**) escalates a non-exception failure
  result into a thrown `SnsMessageProcessingException` so SNS's subscription retry/redrive applies —
  the same at-least-once treatment a thrown exception already gets. Set `RaiseOnFailureStatus = false`
  for at-most-once (a failure result is accepted, no retry). `CatchExceptions` (default `false`)
  conversely controls whether a thrown exception is caught/logged instead of cascading — see
  [SNS Fan-Out Pattern](cookbooks/sns-fan-out.md#configuring-exception-and-retry-behavior-with-snsoptions).

## See also

- [Message Handlers](message-handlers.md) — how handlers produce `IBenzeneResult<T>` and how the
  router/response-handling pipeline consumes it.
- [Middleware](middleware.md) — the pipeline mechanism handlers run inside.
- [Result & Status Reference](reference/results.md#problem-type-registry) — the full problem-type
  registry table.
- [Diagnosing Failures](diagnosing-failures.md) — reading a problem document back out when a message
  failed in production, and how it relates to the mesh's operator-facing issue classification.
- [Global Error Handling](cookbooks/global-error-handling.md) — building a problem document by hand
  for a caught exception, where the automatic mapper doesn't run.
