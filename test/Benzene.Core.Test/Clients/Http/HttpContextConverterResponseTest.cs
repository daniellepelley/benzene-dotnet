using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Benzene.Abstractions.Messages.BenzeneClient;
using Benzene.Abstractions.Results;
using Benzene.Clients.Http;
using Benzene.Clients;
using Benzene.Results;
using Xunit;

namespace Benzene.Test.Clients.Http;

public class HttpContextConverterResponseTest
{
    private static BenzeneClientContext<string, string> CreateClientContext() =>
        new(new BenzeneClientRequest<string>("topic", "message", new Dictionary<string, string>()));

    [Fact]
    public async Task MapResponseAsync_ErrorStatusWithNonJsonBody_DoesNotThrow_AndSurfacesTheStatus()
    {
        // A 500 carrying an HTML error page must not be deserialized as TResponse (that would throw a
        // serialization exception and mask the real status).
        var converter = new HttpContextConverter<string, string>("GET", "https://example.test/x");
        var context = new HttpSendMessageContext(new HttpRequestMessage())
        {
            Response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("<html>error</html>", Encoding.UTF8, "text/html")
            }
        };

        var clientContext = CreateClientContext();
        await converter.MapResponseAsync(clientContext, context);

        Assert.NotEqual(BenzeneResultStatus.Ok, clientContext.Response.Status);
    }

    [Fact]
    public async Task MapResponseAsync_SuccessStatusWithJsonBody_DeserializesTheResponse()
    {
        var converter = new HttpContextConverter<string, string>("GET", "https://example.test/x");
        var context = new HttpSendMessageContext(new HttpRequestMessage())
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("\"hello\"", Encoding.UTF8, "application/json")
            }
        };

        var clientContext = CreateClientContext();
        await converter.MapResponseAsync(clientContext, context);

        Assert.Equal(BenzeneResultStatus.Ok, clientContext.Response.Status);
        Assert.Equal("hello", clientContext.Response.Payload);
    }

    private const string ValidationProblem = """
        {
          "type": "https://example.com/problems/custom-rule",
          "title": "Validation failed",
          "status": 422,
          "detail": "Name must not be empty, Age must be greater than 0",
          "benzeneStatus": "validation-error",
          "errors": [
            { "message": "Name must not be empty", "field": "/name", "code": "NotEmptyValidator" },
            { "message": "Age must be greater than 0", "field": "/age", "code": "GreaterThanValidator" }
          ],
          "tenant": "acme"
        }
        """;

    private static async Task<IBenzeneResult<string>> MapAsync(HttpStatusCode status, string body, string mediaType)
    {
        var converter = new HttpContextConverter<string, string>("GET", "https://example.test/x");
        var context = new HttpSendMessageContext(new HttpRequestMessage())
        {
            Response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) }
        };

        var clientContext = CreateClientContext();
        await converter.MapResponseAsync(clientContext, context);
        return clientContext.Response;
    }

    [Fact]
    public async Task MapResponseAsync_ProblemJsonFailure_RebuildsErrorsWithFieldAndCode_InOrder()
    {
        // core-concepts §12, error fidelity: a client that receives an application/problem+json response
        // MUST rebuild the result's errors from it with field and code intact (wire-contracts §1.3).
        var result = await MapAsync(HttpStatusCode.UnprocessableEntity, ValidationProblem, "application/problem+json");

        Assert.Equal(BenzeneResultStatus.ValidationError, result.Status);
        Assert.False(result.IsSuccessful);
        Assert.Equal(new[]
        {
            new BenzeneError("Name must not be empty", "/name", "NotEmptyValidator"),
            new BenzeneError("Age must be greater than 0", "/age", "GreaterThanValidator"),
        }, result.Errors);
    }

    [Fact]
    public async Task MapResponseAsync_ProblemJsonFailure_AttachesTheReceivedDocument()
    {
        // The received document is attached verbatim, so an application's own problem type survives the hop.
        var result = await MapAsync(HttpStatusCode.UnprocessableEntity, ValidationProblem, "Application/Problem+JSON");

        var problem = result.GetProblem();
        Assert.Equal("https://example.com/problems/custom-rule", problem.Type);
        Assert.Equal(422, problem.Status);
        Assert.Equal("validation-error", problem.BenzeneStatus);
        Assert.Equal(2, problem.Errors!.Length);
    }

    [Fact]
    public async Task MapResponseAsync_AProblemDocumentsBenzeneStatus_WinsOverTheReverseHttpTable()
    {
        // wire-contracts §4.1 (spec 1.1): a client that reads a problem document takes the result's status
        // from its benzeneStatus, not the reverse table (which would give conflict for a 409).
        var result = await MapAsync(HttpStatusCode.Conflict, ValidationProblem, "application/problem+json");

        Assert.Equal(BenzeneResultStatus.ValidationError, result.Status);
        Assert.False(result.IsSuccessful);
        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public async Task MapResponseAsync_AnApplicationDefinedBenzeneStatus_SurvivesTheHop()
    {
        // The point of the rule: an application-defined status has no HTTP code of its own (it goes out as a
        // 500 or 400), so only the document can carry it back.
        var result = await MapAsync(HttpStatusCode.BadRequest,
            """{"benzeneStatus":"insufficient-funds","detail":"Balance too low","errors":[{"message":"Balance too low","code":"funds"}]}""",
            "application/problem+json");

        Assert.Equal("insufficient-funds", result.Status);
        Assert.False(result.IsSuccessful);
        Assert.Equal(new[] { new BenzeneError("Balance too low", code: "funds") }, result.Errors);
    }

    [Fact]
    public async Task MapResponseAsync_AProblemDocumentWithoutBenzeneStatus_KeepsTheReverseHttpTablesStatus()
    {
        // The reverse table applies to every other failure; the document's HTTP status member is never read.
        var result = await MapAsync(HttpStatusCode.Conflict,
            """{"status":422,"detail":"Already exists"}""", "application/problem+json");

        Assert.Equal(BenzeneResultStatus.Conflict, result.Status);
        Assert.Equal("Already exists", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task MapResponseAsync_ProblemJsonWithoutErrors_IsOneMessageOnlyErrorFromDetail()
    {
        // wire-contracts §1.3, "Without errors": detail is one opaque message, never split on commas.
        var result = await MapAsync(HttpStatusCode.NotFound,
            """{"type":"https://benzene.app/problems/not-found","status":404,"detail":"Order 7, line 2 not found","benzeneStatus":"not-found"}""",
            "application/problem+json");

        Assert.Equal(BenzeneResultStatus.NotFound, result.Status);
        Assert.Equal(new[] { new BenzeneError("Order 7, line 2 not found") }, result.Errors);
    }

    [Fact]
    public async Task MapResponseAsync_AJsonErrorBodyThatIsNotProblemJson_IsNotReadAsAProblemDocument()
    {
        // Only an application/problem+json body declares itself a problem document; an arbitrary
        // endpoint's application/json error body is not one, so nothing is read from it.
        var result = await MapAsync(HttpStatusCode.UnprocessableEntity, ValidationProblem, "application/json");

        Assert.Equal(BenzeneResultStatus.ValidationError, result.Status);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task MapResponseAsync_ProblemJsonThatIsNotJson_KeepsTheStatus()
    {
        var result = await MapAsync(HttpStatusCode.ServiceUnavailable, "<html>down</html>", "application/problem+json");

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
        Assert.Empty(result.Errors);
    }
}
