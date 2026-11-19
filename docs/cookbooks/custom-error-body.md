# Keeping an existing error body format

Benzene writes every failure as an [RFC 9457 problem document](../message-result.md#problem-documents-rfc-9457).
If you are moving an existing service onto Benzene, its callers may already parse a different error
shape, for example:

```json
{ "error": { "code": "validation-error", "message": "Name must not be empty", "fields": [ { "name": "/name", "reason": "Name must not be empty" } ] } }
```

You can keep that shape on your HTTP routes with a **problem formatter**. It receives the problem
document Benzene built and returns your body instead. Success payloads, the HTTP status code and the
response headers don't change.

## Before you start: what you give up

The Benzene spec says a failure body is a problem document
([wire-contracts §1.3](https://benzene.app/docs/specification/wire-contracts.html)). A route that
writes another format doesn't conform to that rule:

- Benzene clients read the `errors` of a problem document. Against your format, they still see the
  failure status (from the HTTP status code) but not the field errors.
- The mesh reads the same errors to group failures into issues.
- The `content-type` changes from `application/problem+json` to the type you give, so a client that
  checks for a problem document won't treat your body as one.

So register the formatter for the **HTTP-native contexts only**. The envelope endpoint
(`/benzene/invoke`), which Benzene clients and the mesh call, runs on `BenzeneMessageContext`. Don't
register a formatter for it, and it keeps writing problem documents.

## Register a formatter

A formatter is registered for one context type, so it applies only to that transport:

| Host | Context type |
|---|---|
| API Gateway REST API (v1) | `Benzene.Aws.Lambda.ApiGateway.ApiGatewayContext` |
| API Gateway HTTP API / Function URL (v2) | `Benzene.Aws.Lambda.ApiGateway.ApiGatewayV2Context` |
| ASP.NET Core | `Benzene.AspNet.Core.AspNetContext` |
| Azure Functions (HTTP) | `Benzene.Azure.Function.AspNet.AspNetContext` |

```csharp
using Benzene.Core.MessageHandlers;

services.UsingBenzene(benzene => benzene
    .AddProblemFormatter<ApiGatewayContext>((problem, result) => new
    {
        error = new
        {
            code = result.Status,
            message = problem.Detail ?? problem.Title,
            fields = problem.Errors?.Select(e => new { name = e.Field, reason = e.Message }).ToArray(),
        }
    }));
```

The delegate gets:

- `problem`: the `ProblemDetails` Benzene would have written. On HTTP its `Status` is already filled
  in. A handler that returned its own document with `BenzeneResult.Problem(...)` gives you that
  document, subclass and all.
- `result`: the failed `IBenzeneResult`, if you need its `Status` or `Errors` directly.

It returns the body. A `string` is written as it is; anything else is serialized with the request's
negotiated serializer, by its runtime type. The `content-type` is `application/json` unless you pass
another:

```csharp
benzene.AddProblemFormatter<ApiGatewayContext>(
    (problem, result) => $"<error code=\"{result.Status}\">{problem.Detail}</error>",
    contentType: "application/xml");
```

The formatter applies to every failure on that context, including failures that happen before a
handler runs: an unmatched route, an authorization failure, a rate-limit rejection.

## Format only some failures

Pass `applies` to choose which failures use your format. The others keep the problem document:

```csharp
benzene.AddProblemFormatter<ApiGatewayContext>(
    LegacyError,
    applies: result => result.Status == BenzeneResultStatus.ValidationError);
```

## A formatter class

For a formatter that needs services, implement `IProblemFormatter<TContext>` and register it with
`AddProblemFormatter<TContext, TFormatter>()`. It is resolved once per message:

```csharp
using Benzene.Abstractions.MessageHandlers.Response;

public class LegacyErrorFormatter : IProblemFormatter<ApiGatewayContext>
{
    private readonly IErrorCatalog _catalog;

    public LegacyErrorFormatter(IErrorCatalog catalog) => _catalog = catalog;

    public string ContentType => "application/json";

    // Optional: the default formats every failure.
    public bool Applies(ApiGatewayContext context, IBenzeneResult result) => true;

    public object Format(ApiGatewayContext context, ProblemDetails problem, IBenzeneResult result) => new
    {
        error = new { code = _catalog.CodeFor(result.Status), message = problem.Detail }
    };
}

benzene.AddProblemFormatter<ApiGatewayContext, LegacyErrorFormatter>();
```

## Replacing the whole payload mapper

`IResponsePayloadMapper<TContext>` writes both success and failure bodies, and you can still
register your own. Prefer a formatter. A replacement mapper has to serialize success payloads too,
and on HTTP it replaces the mapper that fills in the problem document's `status`.

## Testing it

Send a failing request through your real composition root and check the body. With the Lambda test
host:

```csharp
var host = new InlineAwsLambdaStartUp()
    .ConfigureServices(services => services
        .ConfigureServiceCollection()
        .UsingBenzene(x => x.AddProblemFormatter<ApiGatewayContext>(LegacyError)))
    .Configure(app => app.UseApiGateway(apiGateway => apiGateway.UseMessageHandlers()))
    .BuildHost();

var response = await host.SendApiGatewayAsync(HttpBuilder.Create("POST", "/orders", new CreateOrder()));

Assert.Equal(422, response.StatusCode);
Assert.Equal("application/json", response.Headers["content-type"]);
Assert.NotNull(JObject.Parse(response.Body)["error"]);
```

`test/Benzene.Core.Test/Http/ProblemFormatterTest.cs` runs these cases on API Gateway v1 and v2 and
Azure Functions, and checks that the envelope endpoint still returns a problem document.

## See also

- [Message Results: problem documents](../message-result.md#problem-documents-rfc-9457): the
  default format, and `BenzeneResult.Problem(...)` for a handler that authors its own.
- [Global error handling](global-error-handling.md): a thrown exception is not a failed result, so
  `UseExceptionHandler` builds its own response, and a formatter doesn't apply.
