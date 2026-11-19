> ARCHIVED 2026-11-19: actioned in `605225f`; the design shipped as written, except the response
> handler doesn't log skipped headers (one constructor, nothing extra to resolve). Not done:
> surfacing received headers on a client result (§2.4, optional), and a dedicated test that a
> versioned response keeps its headers (the cast forwards them; covered by code, not a test).

# FR-3 — Response headers from handler results (plan)

**Status: done, 2026-11-19** (`605225f`). The request is FR-3 of the adopter feature requests,
[`Benzene/work/adopter-feature-requests.md`](https://github.com/daniellepelley/Benzene/blob/main/work/adopter-feature-requests.md).
No spec change: the envelope already has response `headers`. Code references are to `main` at `be33941`.

**Goal.** A handler returns `BenzeneResult.Created(task).WithHeader("Location", $"/tasks/{task.Id}")`,
and the HTTP response is 201 with that `Location`, on API Gateway v1 and v2, ASP.NET Core
(including Google Cloud Functions) and Azure Functions. Over the envelope, the same header appears in
the response envelope's `headers`.

## 1. What happens today (verified)

**Results can't carry headers.**
- `IBenzeneResult` (`src/Benzene.Abstractions/Results/IBenzeneResult.cs:7-35`) has `Status`,
  `IsSuccessful`, `PayloadAsObject` and `Errors`.
- The one concrete result, private `ServiceBenzeneResultInternal<T>` (`src/Benzene.Results/BenzeneResult.cs:393`),
  is immutable.
- There are no `With*` builders.
- The pattern for optional result data already exists: `IHasProblemDetails`
  (`src/Benzene.Results/IHasProblemDetails.cs:26-34`). Its doc says it is deliberately not a member of
  `IBenzeneResult`, "that interface is frozen". It is read through the `GetProblem()` extension.

**Two places already lose optional result data.** Both would lose headers too:
- `CastBenzeneResult` (`src/Benzene.Core.Versioning/Response/CastMessageHandlerResult.cs:27`) forwards
  only `Status`, `IsSuccessful` and `Errors`. It already drops `IHasProblemDetails`.
- The `As<...>` re-wraps (`src/Benzene.Results/BenzeneResultExtensions.cs:146-176`) rebuild through
  `BenzeneResult.Set` and drop the problem.

**How a result reaches the response.**
- The result is wrapped in `MessageHandlerResult` (`MessageRouter.cs:187`). Each HTTP host's result
  setter calls `ResponseHandlerContainer.HandleAsync` (`ResponseHandlerContainer.cs:30-37`).
- That runs every registered `IResponseHandler<TContext>` in registration order, then
  `IBenzeneResponseAdapter.FinalizeAsync`.
- Every HTTP host registers `HttpStatusCodeResponseHandler`, then `RendererResponseHandler`, which
  sets the body and `content-type`:
  - ASP.NET Core: `src/Benzene.AspNet.Core/DependencyInjectionExtensions.cs:57,59`
  - API Gateway v1: `src/Benzene.Aws.Lambda.ApiGateway/DependencyInjectionExtensions.cs:60,62`
  - API Gateway v2: same file, `:106,108`
  - Azure Functions: `src/Benzene.Azure.Function.AspNet/DependencyInjectionExtensions.cs:100-101`
  - The envelope: `src/Benzene.Core.MessageHandlers/DI/Extensions.cs:71-72`, renderer first

**Writing a header** is `IBenzeneResponseAdapter<TContext>.SetResponseHeader`. The hosts disagree on a
second write to the same key:

| Host | Adapter | Second write to the same key |
|---|---|---|
| API Gateway v1 | `ApiGatewayResponseAdapter.cs:20-24` | overwrites (case-insensitive dictionary; no multi-value headers) |
| API Gateway v2 | `ApiGatewayV2ResponseAdapter.cs:24-38` | overwrites; `set-cookie` appends to `Cookies` |
| ASP.NET Core | `src/Benzene.AspNet.Core/AspNetResponseAdapter.cs:27-33` | appends a second value |
| Azure Functions | `src/Benzene.Azure.Function.AspNet/AspNetResponseAdapter.cs:17-21` | **throws** (`Headers.Add`) |
| Envelope | `BenzeneMessageResponseAdapter.cs:15-19` | overwrites |

Non-HTTP transports have no response adapter. gRPC has its own `GrpcContext.ResponseHeaders`.

**Middleware already writes headers.**
- CORS (`src/Benzene.Http/Cors/CorsMiddleware.cs:63-79`) writes **before** `next()`, on purpose,
  because ASP.NET Core headers turn read-only once the response flushes.
- Basic auth's `WWW-Authenticate`, rate limiting's `Retry-After` and OIDC's `Location` only write on
  short-circuit paths, where no handler result exists.

So **CORS is the only middleware header that meets a handler result**. Any step that writes result
headers after the handler will:
- overwrite CORS headers on v1, v2 and the envelope;
- duplicate them on ASP.NET Core;
- throw on Azure Functions.

That rules out a plain "write every result header".

**Clients.** `BenzeneMessageClientResponse.Headers` is filled from the envelope, but
`AsBenzeneResult<T>` (`src/Benzene.Clients/Common/ClientResultExtensions.cs:37-55`) ignores it.

## 2. Design

### 2.1 The result carries headers

- **`IHasResponseHeaders`,** in `Benzene.Results`, beside `IHasProblemDetails`:
  `IReadOnlyDictionary<string, string> ResponseHeaders { get; }`. Keys are case-insensitive.
  - String to string, like the envelope (spec wire-contracts §1.2 and §2: "Duplicate keys: last value
    wins").
  - `IBenzeneResult` stays frozen.
- `ServiceBenzeneResultInternal<T>` implements it, with an internal copy constructor that keeps the
  problem.
- **The fluent API** goes in `BenzeneResultExtensions`:
  ```csharp
  public static IBenzeneResult<T> WithHeader<T>(this IBenzeneResult<T> result, string name, string value);
  public static IBenzeneResult WithHeader(this IBenzeneResult result, string name, string value);
  public static IBenzeneResult<T> WithHeaders<T>(this IBenzeneResult<T> result, IEnumerable<KeyValuePair<string, string>> headers);
  public static IReadOnlyDictionary<string, string> GetResponseHeaders(this IBenzeneResult result); // empty when none
  ```
  - Each call returns a new result, and a second call for a name replaces the first.
  - On a result that isn't `ServiceBenzeneResultInternal` (a user's own `IBenzeneResult`), it
    rebuilds through `BenzeneResult.Set`, keeping the problem from `GetProblem()`.
- **Validation in `WithHeader`.** A name must be an HTTP token. A name or value containing CR, LF or
  NUL throws `ArgumentException`, which closes off header injection whatever the host does.
- **Preserve through wrapping.**
  - `CastBenzeneResult` forwards `IHasResponseHeaders`, and also `IHasProblemDetails` (today's gap,
    fixed in the same change).
  - The `As<...>` re-wraps keep both.

### 2.2 A response handler writes them, and never replaces the pipeline's headers

**Add a read to the adapter.**
- `bool HasResponseHeader(TContext context, string name)` becomes a default interface member on
  `IBenzeneResponseAdapter<TContext>`, returning `false`, so existing custom adapters keep compiling.
  `SetSuccessful` already follows this pattern.
- The five adapters above implement it from their own header store.

**A new `ResultHeadersResponseHandler<TContext>`,** in `Benzene.Core.MessageHandlers/Response`. For each
result header:
- **Skip** it when `HasResponseHeader` is true, logging at debug. The pipeline wrote it first: CORS
  before the handler, or the renderer's `content-type`.
- Otherwise write it with `SetResponseHeader`.

**Register it after `RendererResponseHandler`** on every HTTP host and on the envelope. The renderer's
`content-type` is then already present and wins. A handler sets the body type through the existing
media-type machinery, not a header.

The precedence rule, stated once in the docs: **a header the pipeline sets wins over the same header
from the handler; the handler adds what the pipeline didn't set.** CORS and the content type can't be
overridden by accident, and nothing throws on Azure.

This differs from the adopter request's wording, "the handler wins over defaults". No default header
exists to override except `content-type`, which has its own mechanism.

**`set-cookie` on v2** goes through the adapter's existing cookie path. Several cookies would need
several values, which this string-to-string model doesn't hold, so one `set-cookie` per result. Say so
in the docs.

**Non-HTTP transports** (SQS, SNS, Kafka and the rest) have no adapter, so result headers are
ignored, silently and by design. gRPC is out of scope here. Mapping onto `GrpcContext.ResponseHeaders`
is a follow-up if anyone asks.

### 2.3 The envelope

`BenzeneMessageResponse.Headers` already exists, and the spec defines it as the response headers
(wire-contracts §1.2). Registering the handler on the envelope pipeline puts result headers there.
That is an observable change, but within the contract: a header the envelope may already carry. No
fixture changes.

### 2.4 Clients (optional, last)

For symmetry, `AsBenzeneResult<T>` could attach the received envelope or HTTP headers to the client
result through `IHasResponseHeaders`, the same way `AttachReceivedProblem` attaches a problem. This
isn't needed for the adopter; do it only if it stays small, otherwise leave it as a follow-up.

## 3. Steps

1. **Result.**
   - `IHasResponseHeaders`, `ServiceBenzeneResultInternal` support, `WithHeader`/`WithHeaders`/
     `GetResponseHeaders`, and name/value validation.
   - Preserve headers and problem in `CastBenzeneResult` and the `As<...>` re-wraps.
   - Tests in `test/Benzene.Core.Test/Core/Core/Results/BenzeneResultTest.cs` and
     `Core/Results/BenzeneResultExtensionsTest.cs`:
     - immutability: the original result is unchanged;
     - last-write-wins per name, ignoring case;
     - CR/LF rejected;
     - the problem is kept across `WithHeader`;
     - headers survive `As<...>` and a versioning cast.
2. **Adapter read.** `HasResponseHeader` default member, and the implementation on the five adapters,
   with a test per adapter beside the existing adapter tests (`Asp/AspNetResponseAdapterTest.cs`,
   `Aws/ApiGateway/ApiGatewayResponseHeaderCasingTest.cs`, …).
3. **Response handler.** `ResultHeadersResponseHandler<TContext>`, registered after the renderer in
   the five DI extension methods listed in §1. Unit test in
   `Core/Core/Response/ResponseHandlerContainerTest.cs` style: writes absent headers, skips present
   ones, no headers is a no-op.
4. **End to end, per host,** through the test helpers:
   - `Created` with `Location` gives 201 and the header, on v1, v2, ASP.NET Core and Azure Functions.
   - A failure result (`Conflict` with a header) carries it too.
   - With `UseCors`, a handler setting `Access-Control-Allow-Origin` doesn't replace the CORS value,
     and Azure doesn't throw.
   - The handler can't replace `content-type`.
   - Over the envelope, the header appears in the response envelope's `headers`.
5. **Docs.**
   - `docs/message-result.md`: a "Response headers" section with the precedence rule, the
     non-HTTP behaviour and the `set-cookie` limit.
   - `docs/reference/results.md`: the new extension methods.
   - `docs/getting-started-aws.md`: remove "response headers set by a handler" from the API Gateway
     section's "Not built yet" list.
6. **Records.** Capability-matrix row, and mark FR-3 done in the adopter feature requests. Step 2.4
   (clients) only if done.

## 4. Risks and open points

- **The default-false `HasResponseHeader`** on a custom adapter means result headers always write
  there. That is acceptable: such an adapter defines its own semantics.
- **Middleware that runs after the handler** and sets a header on the way out (none in-tree today)
  would overwrite on v1, v2 and the envelope, and append on ASP.NET Core. Document that response
  handlers run before the adapter finalizes, and that later writes follow the host's own semantics.
- **Absolute `Location`.** A relative `Location` is legal (RFC 9110 §10.2.2). Making it absolute
  needs the request URL, which is FR-2. Until then, handlers pass relative paths.
