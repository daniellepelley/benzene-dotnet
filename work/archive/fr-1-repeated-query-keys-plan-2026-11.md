> ARCHIVED 2026-11-19: actioned in `131898d`; the design shipped as written. The `MapOnto` overload
> for `object` values already existed, so step 1 needed none. Not done: the benchmark run (step 5) and
> the optional spec sentence (step 7), both still open in Benzene's `work/adopter-feature-requests.md`.

# FR-1 — Repeated query keys bind to collections (plan)

**Status: done, 2026-11-19** (`131898d`). The request is FR-1 of the adopter feature requests,
[`Benzene/work/adopter-feature-requests.md`](https://github.com/daniellepelley/Benzene/blob/main/work/adopter-feature-requests.md).
No spec change. Code references are to `main` at `be33941`.

**Goal.** `GET /tasks?status=open&status=blocked` binds both values into a `string[]`/`List<T>`
property, on every HTTP host. A scalar property keeps binding the first value, as it does today.

## 1. What happens today (verified)

**One binder for every host.**
- `MessageHandler.HandleAsync` → `MultiSerializerOptionsRequestMapper.GetBody` →
  `EnrichingRequestMapper.GetBody` (`src/Benzene.Core.MessageHandlers/Request/EnrichingRequestMapper.cs:43-56`).
- It deserializes the body, folds every `IRequestEnricher<TContext>` into one dictionary, then calls
  `DictionaryUtils.Enrich(request, dictionary)`.
- There is no other binder: the source generator only emits registrations.

**The enricher contract already allows non-strings.**
`IDictionary<string, object> Enrich<TRequest>(TRequest request, TContext context)`
(`src/Benzene.Abstractions.MessageHandlers/Request/IRequestEnricher.cs:21`). Emitting a `string[]`
needs no signature change.

**The four enrichers, and what each does with `?id=a&id=b`:**

| Host | Enricher | Today |
|---|---|---|
| API Gateway v1 | `ApiGatewayRequestEnricher.cs:51` → `QueryStringFirstWinsMapper.ForV1` | `"a"`, from `MultiValueQueryStringParameters` (falls back to the single-value map) |
| API Gateway v2 | `ApiGatewayV2RequestEnricher.cs:51` → `QueryStringFirstWinsMapper.ForV2` | `"a"`. AWS comma-joins repeated keys; the mapper keeps the first segment |
| ASP.NET Core (also Google Cloud Functions, which reuses it) | `AspNetRequestEnricher.cs:50` | `"a"` (`x.Value.First()`) |
| Azure Functions HTTP | `AspNetContextRequestEnricher.cs:46` | **`"a,b"`** (`StringValues.ToString()` comma-joins). This is the odd one out |

Out of scope: the HttpBridge / Lambda AspNet bridges (they use the app's own ASP.NET binder), gRPC
(no query string) and `IOidcQueryStringReader` (not binding).

**Merge order.**
- Within one enricher, `MapOnto` lower-cases keys and the first write wins
  (`src/Benzene.Core/Helper/DictionaryUtils.cs:56-68`): query, then path params, then headers, then
  route params.
- Across enrichers, the one registered first wins.
- Enriched values overwrite values deserialized from the body.

None of this changes.

**Binding** (`DictionaryUtils.GetValue`, `:224-256`).
- It handles: an exact type match, `Nullable<T>`, `Guid` and enums, then falls back to
  `Convert.ChangeType`.
- **A collection property whose name matches a query key fails today, even with one value.**
  `Convert.ChangeType("a", typeof(string[]))` throws `InvalidCastException`. `MessageHandler.cs:79-85`
  turns that into `bad-request` "Message is not valid".
- A collection with no matching key is left alone, so an array in a JSON body survives.

**Tests that pin the current behaviour.**
- `test/Benzene.Core.Test/Aws/ApiGateway/ApiGatewayHeaderCasingAndQueryStringTest.cs`, class
  `ApiGatewayQueryStringFirstWinsTest` (`:133`). Its four tests cover v1 with and without the
  multi-value map, v2, and an "agree" test that never exercises ASP.NET.
- There are no query tests for ASP.NET Core or Azure.

**Test helpers can't set a query string.** `IHttpBuilder<T>` (`src/Benzene.Abstractions/IHttpBuilder.cs`)
has only headers, method, path and message. `AsApiGatewayRequest` and `AsAspNetCoreHttpRequest` set no
query.

## 2. Design

### 2.1 Enrichers emit every value

A query key with one value is emitted as a `string`, as today. A key with more than one value is
emitted as a `string[]`, in request order:

| Host | Source of the values |
|---|---|
| v1 | `MultiValueQueryStringParameters[key]`, or the single-value map when the multi-value map is absent |
| v2 | `QueryStringParameters[key].Split(',')`. A value that contains a literal comma can't be told apart from two values; that is already true today, where the mapper cuts it |
| ASP.NET Core | `Request.Query[key]` (`StringValues`): one value → `string`, several → `string[]` |
| Azure Functions | the same as ASP.NET Core |

Keeping single values as `string` means:
- anything reading enricher output sees no change for ordinary requests;
- the common path allocates nothing new.

`QueryStringFirstWinsMapper` (internal) becomes `QueryStringValuesMapper`, returning
`IDictionary<string, object>`. `DictionaryUtils` gets a `MapOnto` overload for `object` values; today
only `string` overlays are accepted (`:16-29`).

### 2.2 The binder converts to the property's shape

In `DictionaryUtils.GetValue`, before the existing scalar rules:

- **Collection target, string value:** a one-element collection.
- **Collection target, `string[]` value:** convert each element with the existing scalar rules
  (`Guid`, enum, `Nullable<T>`, `ChangeType`), then build the target.
- **Scalar target, `string[]` value:** the first element, then the scalar rules. This keeps
  first-value-wins for scalars on v1, v2 and ASP.NET.

Supported collection targets: `T[]`, `List<T>`, `IList<T>`, `ICollection<T>`, `IEnumerable<T>`,
`IReadOnlyList<T>` and `IReadOnlyCollection<T>`. Other collection types (`HashSet<T>`, custom
collections) fall through to today's behaviour and throw, as now. Cache the element type and
collection factory per property type next to `PropertySetterCache<T>`, so the reflection runs once
per type. The same element-type test already exists in
`Benzene.Core.Versioning/CasterBuilder/CasterFuncBuilder.cs:259-269`; reuse the shape, not the code.

**A string is not a collection.** `string` implements `IEnumerable<char>` and must stay scalar.

**A conversion failure** (`?n=1&n=x` into `List<int>`) throws inside the binder, as a scalar does,
and becomes the same `bad-request` "Message is not valid". This plan keeps that. Turning binding
failures into a `validation-error` naming the key is worth doing, but for scalars and collections
together, and not here.

**An absent key** leaves the property untouched: the body's value, or the type's default.

### 2.3 Azure Functions changes behaviour

Today a scalar on Azure Functions receives `"a,b"` for `?id=a&id=b`; every other host gives `"a"`.
After this change Azure gives `"a"` as well, and a collection gets `["a","b"]`.
- **Decision (recommended): align it.** The comma-joined string was never a documented contract;
  it came from `StringValues.ToString()`.
- The change goes in the release notes under behaviour changes, with the one-line fix for anyone
  who relied on it: make the property a `string[]`.

### 2.4 Test helpers

Adding a member to the public `IHttpBuilder<T>` would break anyone implementing it. Instead:
- Add an optional `IHttpQueryBuilder` interface in `Benzene.Abstractions`:
  `IReadOnlyList<KeyValuePair<string, string>> Query { get; }`.
- `HttpBuilder<T>` implements it, with `WithQuery(string key, string value)` (repeatable; each call
  adds a value) and `WithQuery(string key, params string[] values)`.
- `AsApiGatewayRequest` fills both `MultiValueQueryStringParameters` and `QueryStringParameters`.
  AWS puts the last value in the single-value map; match it.
- A v2 helper, if one is added, comma-joins the values.
- `AsAspNetCoreHttpRequest` fills `Query`.
- Each helper reads the query through `source as IHttpQueryBuilder`, so other builders keep
  working.

## 3. Steps

1. **Binder.** Add collection conversion and the scalar-from-array rule to `DictionaryUtils.GetValue`,
   plus the `object` `MapOnto` overload. Tests in
   `test/Benzene.Core.Test/Core/Core/Helper/DictionaryUtilsTest.cs`:
   - each supported collection type from one value and from several;
   - element conversion for `int`, `Guid`, enum and `Nullable<int>`;
   - a scalar from an array takes the first element;
   - `string` stays scalar;
   - an unsupported collection type throws as before;
   - a failing element throws.
2. **Enrichers.**
   - Replace `QueryStringFirstWinsMapper` with `QueryStringValuesMapper` (v1, v2), and change the
     ASP.NET Core and Azure enrichers.
   - Update `ApiGatewayQueryStringFirstWinsTest`: the scalar tests stay (they still pass, now
     through the array rule). Add collection cases for v1 with and without the multi-value map, and
     for v2.
3. **Test helpers.** `IHttpQueryBuilder`, `HttpBuilder<T>.WithQuery`, and query support in
   `AsApiGatewayRequest` and `AsAspNetCoreHttpRequest`.
4. **End-to-end tests per host,** through the test helpers and a real handler with
   `string[] Ids`, `List<int> Numbers`, `IReadOnlyList<Status> Statuses` and scalar `string Id`:
   - v1 and v2 in `ApiGatewayMessagePipelineTest` / `ApiGatewayV2MessagePipelineTest`;
   - ASP.NET Core in `test/Benzene.Core.Test/Asp/`;
   - Azure Functions in `test/Benzene.Core.Test/Azure/AspNetPipelineTest.cs`.

   The cases:
   - `?ids=a&ids=b` binds `["a","b"]`;
   - `?numbers=1&numbers=2` binds `[1,2]`;
   - enum values bind ignoring case;
   - `?id=a&id=b` into a scalar binds `"a"`;
   - a missing key leaves the property as the body set it;
   - `?numbers=1&numbers=x` is `bad-request`.

   The Azure scalar case is the behaviour change in §2.3.
5. **Benchmark.** Run `benchmarks/Benzene.Benchmarks/RequestMappingBenchmarks.cs` before and after.
   A request with no repeated keys should not change.
6. **Docs.**
   - `docs/getting-started-aws.md`, the API Gateway section's "Repeated query keys" bullet.
   - The package `CLAUDE.md` of `Benzene.Aws.Lambda.ApiGateway` (`:34`, `:125-130`), and the
     ASP.NET Core and Azure ones.
   - `docs/asp-net-core.md:92` (query binding).
   - `docs/message-handlers.md` (the enricher section): one paragraph on collections.
7. **Records.**
   - Capability matrix: the query-binding row.
   - Release notes: the Azure behaviour change.
   - Mark FR-1 done in the adopter feature requests.
   - Optionally add one sentence to the spec's transport-bindings, "a repeated query key binds to a
     collection", so the other ports agree. That is a separate, small Benzene change.

## 4. Risks

- **v2 commas.** A single value containing a comma is split, so a collection gets two elements
  where the caller meant one. Today that value is already cut to its first segment, so scalars lose
  nothing new. Document it as an API Gateway HTTP API limit.
- **Custom enrichers.** An enricher registered before a transport enricher still wins, and may emit
  a `string` where a collection is expected. That now binds as a one-element collection instead of
  throwing, which is an improvement, not a break.
- **Body arrays.** A collection set from the body and also present in the query is overwritten by
  the query, the same precedence as scalars today. Call it out in the docs.
