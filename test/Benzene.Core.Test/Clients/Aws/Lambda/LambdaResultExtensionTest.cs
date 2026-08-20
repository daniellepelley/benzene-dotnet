using System;
using Benzene.Abstractions.Results;
using Benzene.Clients;
using Benzene.Clients.Common;
using Benzene.Results;
using Benzene.Test.Clients.Aws.Samples;
using Newtonsoft.Json;
using Xunit;
using JsonSerializer = Benzene.Clients.JsonSerializer;

namespace Benzene.Test.Clients.Aws.Lambda;

public class LambdaResultExtensionTest
{
    [Theory]
    [InlineData("200", BenzeneResultStatus.Ok)]
    [InlineData("201", BenzeneResultStatus.Created)]
    [InlineData("204", BenzeneResultStatus.Ok)]
    public void MapSuccessTest(string responseStatusCode, string expectedStatus)
    {
        var lambdaResponse = new BenzeneMessageClientResponse(responseStatusCode,
            JsonConvert.SerializeObject(new ExamplePayload { Name = "some-name" }));

        var lambdaBenzeneResult = lambdaResponse.AsBenzeneResult<ExamplePayload>(new JsonSerializer());

        Assert.Equal(expectedStatus, lambdaBenzeneResult.Status);
        Assert.Equal("some-name", lambdaBenzeneResult.Payload.Name);
    }

    [Theory]
    [InlineData("200", BenzeneResultStatus.Ok)]
    [InlineData("201", BenzeneResultStatus.Created)]
    [InlineData("204", BenzeneResultStatus.Ok)]
    public void MapSuccessTest_NullPayload(string responseStatusCode, string expectedStatus)
    {
        var lambdaResponse = new BenzeneMessageClientResponse(responseStatusCode, null);

        var lambdaBenzeneResult = lambdaResponse.AsBenzeneResult<Guid>(new JsonSerializer());

        Assert.Equal(expectedStatus, lambdaBenzeneResult.Status);
        Assert.Empty(lambdaBenzeneResult.Errors);
    }

    [Theory]
    [InlineData("200", BenzeneResultStatus.Ok)]
    [InlineData("201", BenzeneResultStatus.Created)]
    [InlineData("204", BenzeneResultStatus.Ok)]
    public void MapSuccessTest_NullDefaultString(string responseStatusCode, string expectedStatus)
    {
        var lambdaResponse = new BenzeneMessageClientResponse(responseStatusCode, JsonConvert.SerializeObject(null));

        var lambdaBenzeneResult = lambdaResponse.AsBenzeneResult<Guid>(new JsonSerializer());

        Assert.Equal(expectedStatus, lambdaBenzeneResult.Status);
        Assert.Empty(lambdaBenzeneResult.Errors);
    }

    [Theory]
    [InlineData("200", BenzeneResultStatus.Ok)]
    [InlineData("201", BenzeneResultStatus.Created)]
    [InlineData("204", BenzeneResultStatus.Ok)]
    public void MapSuccessTest_HandleGuid(string responseStatusCode, string expectedStatus)
    {
        var lambdaResponse = new BenzeneMessageClientResponse(responseStatusCode, JsonConvert.SerializeObject("b2d20bc3-9e29-4164-9983-1b568a1b70be"));

        var lambdaBenzeneResult = lambdaResponse.AsBenzeneResult<Guid>(new JsonSerializer());

        Assert.Equal(expectedStatus, lambdaBenzeneResult.Status);
        Assert.Empty(lambdaBenzeneResult.Errors);
        Assert.Equal(Guid.Parse("b2d20bc3-9e29-4164-9983-1b568a1b70be"), lambdaBenzeneResult.Payload);
    }

    [Theory]
    [InlineData("400", BenzeneResultStatus.BadRequest)]
    [InlineData("401", BenzeneResultStatus.Unauthorized)]
    [InlineData("403", BenzeneResultStatus.Forbidden)]
    [InlineData("404", BenzeneResultStatus.NotFound)]
    [InlineData("409", BenzeneResultStatus.Conflict)]
    [InlineData("422", BenzeneResultStatus.ValidationError)]
    [InlineData("503", BenzeneResultStatus.ServiceUnavailable)]
    public void MapFailureTestGuid(string responseStatusCode, string expectedStatus)
    {
        var lambdaResponse = new BenzeneMessageClientResponse(responseStatusCode, JsonConvert.SerializeObject(new ProblemDetails { Detail = "some-error" }));
        var lambdaBenzeneResult = lambdaResponse.AsBenzeneResult<Guid>(new JsonSerializer());

        Assert.Equal(expectedStatus, lambdaBenzeneResult.Status);
        Assert.Equal("some-error", lambdaBenzeneResult.Errors[0].Message);
    }

    [Theory]
    [InlineData("400", BenzeneResultStatus.BadRequest)]
    [InlineData("401", BenzeneResultStatus.Unauthorized)]
    [InlineData("403", BenzeneResultStatus.Forbidden)]
    [InlineData("404", BenzeneResultStatus.NotFound)]
    [InlineData("409", BenzeneResultStatus.Conflict)]
    [InlineData("422", BenzeneResultStatus.ValidationError)]
    [InlineData("503", BenzeneResultStatus.ServiceUnavailable)]
    public void MapFailureTestObject(string responseStatusCode, string expectedStatus)
    {
        var lambdaResponse = new BenzeneMessageClientResponse(responseStatusCode, JsonConvert.SerializeObject(new ProblemDetails { Detail = "some-error" }));

        var lambdaBenzeneResult = lambdaResponse.AsBenzeneResult<object>(new JsonSerializer());

        Assert.Equal(expectedStatus, lambdaBenzeneResult.Status);
        Assert.Equal("some-error", lambdaBenzeneResult.Errors[0].Message);
    }

    [Theory]
    [InlineData("400", BenzeneResultStatus.BadRequest)]
    [InlineData("401", BenzeneResultStatus.Unauthorized)]
    [InlineData("403", BenzeneResultStatus.Forbidden)]
    [InlineData("404", BenzeneResultStatus.NotFound)]
    [InlineData("409", BenzeneResultStatus.Conflict)]
    [InlineData("422", BenzeneResultStatus.ValidationError)]
    [InlineData("503", BenzeneResultStatus.ServiceUnavailable)]
    public void MapFailureTest_NullPayload(string responseStatusCode, string expectedStatus)
    {
        var lambdaResponse = new BenzeneMessageClientResponse(responseStatusCode, null);

        var lambdaBenzeneResult = lambdaResponse.AsBenzeneResult<Guid>(new JsonSerializer());

        Assert.Equal(expectedStatus, lambdaBenzeneResult.Status);
        Assert.Empty(lambdaBenzeneResult.Errors);
    }

    [Theory]
    [InlineData("400", BenzeneResultStatus.BadRequest)]
    [InlineData("401", BenzeneResultStatus.Unauthorized)]
    [InlineData("403", BenzeneResultStatus.Forbidden)]
    [InlineData("404", BenzeneResultStatus.NotFound)]
    [InlineData("409", BenzeneResultStatus.Conflict)]
    [InlineData("422", BenzeneResultStatus.ValidationError)]
    [InlineData("503", BenzeneResultStatus.ServiceUnavailable)]
    public void MapFailureTest_NullStringPayload(string responseStatusCode, string expectedStatus)
    {
        var lambdaResponse = new BenzeneMessageClientResponse(responseStatusCode, JsonConvert.SerializeObject(null));

        var lambdaBenzeneResult = lambdaResponse.AsBenzeneResult<Guid>(new JsonSerializer());

        Assert.Equal(expectedStatus, lambdaBenzeneResult.Status);
        Assert.Empty(lambdaBenzeneResult.Errors);
    }

    // The BenzeneMessage envelope carries raw Benzene statuses (docs/specification/wire-contracts.md),
    // which the client preserves verbatim - including the ones that would collapse under the numeric
    // HTTP mapping (Updated/Deleted -> 204 -> Ok).
    [Theory]
    [InlineData(BenzeneResultStatus.Ok)]
    [InlineData(BenzeneResultStatus.Created)]
    [InlineData(BenzeneResultStatus.Accepted)]
    [InlineData(BenzeneResultStatus.Updated)]
    [InlineData(BenzeneResultStatus.Deleted)]
    [InlineData(BenzeneResultStatus.Ignored)]
    public void MapSuccessTest_RawBenzeneStatusIsPreservedVerbatim(string benzeneStatus)
    {
        var lambdaResponse = new BenzeneMessageClientResponse(benzeneStatus,
            JsonConvert.SerializeObject(new ExamplePayload { Name = "some-name" }));

        var lambdaBenzeneResult = lambdaResponse.AsBenzeneResult<ExamplePayload>(new JsonSerializer());

        Assert.Equal(benzeneStatus, lambdaBenzeneResult.Status);
        Assert.True(lambdaBenzeneResult.IsSuccessful);
        Assert.Equal("some-name", lambdaBenzeneResult.Payload.Name);
    }

    [Theory]
    [InlineData(BenzeneResultStatus.BadRequest)]
    [InlineData(BenzeneResultStatus.ValidationError)]
    [InlineData(BenzeneResultStatus.Unauthorized)]
    [InlineData(BenzeneResultStatus.Forbidden)]
    [InlineData(BenzeneResultStatus.NotFound)]
    [InlineData(BenzeneResultStatus.Conflict)]
    [InlineData(BenzeneResultStatus.NotImplemented)]
    [InlineData(BenzeneResultStatus.ServiceUnavailable)]
    [InlineData(BenzeneResultStatus.UnexpectedError)]
    public void MapFailureTest_RawBenzeneStatusIsPreservedVerbatim(string benzeneStatus)
    {
        var lambdaResponse = new BenzeneMessageClientResponse(benzeneStatus,
            JsonConvert.SerializeObject(new ProblemDetails { Detail = "some-error" }));

        var lambdaBenzeneResult = lambdaResponse.AsBenzeneResult<ExamplePayload>(new JsonSerializer());

        Assert.Equal(benzeneStatus, lambdaBenzeneResult.Status);
        Assert.False(lambdaBenzeneResult.IsSuccessful);
        Assert.Equal("some-error", lambdaBenzeneResult.Errors[0].Message);
    }

    // Phase 5 of work/archive/problem-details-plan-2026-08.md: a multi-error problem body's "errors" member is
    // authoritative and round-trips as structured BenzeneErrors (field/code/order intact) rather than
    // collapsing into a single joined-string message (ruling §5.2's defect).
    [Fact]
    public void MapFailure_ProblemWithStructuredErrors_RoundTripsFieldCodeAndOrder()
    {
        var problem = new ProblemDetails
        {
            BenzeneStatus = BenzeneResultStatus.ValidationError,
            Detail = "Name must not be empty, Age must be greater than 0",
            Errors = new[]
            {
                new BenzeneError("Name must not be empty", "Name", "NotEmptyValidator"),
                new BenzeneError("Age must be greater than 0", "Age", "GreaterThanValidator"),
            },
        };
        var lambdaResponse = new BenzeneMessageClientResponse("422", JsonConvert.SerializeObject(problem));

        var result = lambdaResponse.AsBenzeneResult<ExamplePayload>(new JsonSerializer());

        Assert.False(result.IsSuccessful);
        Assert.Equal(BenzeneResultStatus.ValidationError, result.Status);
        Assert.Equal(2, result.Errors.Count);
        Assert.Equal("Name must not be empty", result.Errors[0].Message);
        Assert.Equal("Name", result.Errors[0].Field);
        Assert.Equal("NotEmptyValidator", result.Errors[0].Code);
        Assert.Equal("Age must be greater than 0", result.Errors[1].Message);
        Assert.Equal("Age", result.Errors[1].Field);
        Assert.Equal("GreaterThanValidator", result.Errors[1].Code);
    }

    [Fact]
    public void MapFailure_ProblemWithStructuredErrors_AttachesTheReceivedDocumentForGetProblem()
    {
        var problem = new ProblemDetails
        {
            Type = "https://benzene.app/problems/validation-error",
            BenzeneStatus = BenzeneResultStatus.ValidationError,
            Errors = new[] { new BenzeneError("Name must not be empty", "Name", "NotEmptyValidator") },
        };
        var lambdaResponse = new BenzeneMessageClientResponse("422", JsonConvert.SerializeObject(problem));

        var result = lambdaResponse.AsBenzeneResult<ExamplePayload>(new JsonSerializer());

        var receivedProblem = result.GetProblem();
        Assert.Equal("https://benzene.app/problems/validation-error", receivedProblem.Type);
        var error = Assert.Single(receivedProblem.Errors!);
        Assert.Equal("Name", error.Field);
    }

    [Fact]
    public void MapFailure_ProblemWithNoErrorsMember_FallsBackToAMessageOnlyErrorFromDetail()
    {
        // Unchanged behavior for an older producer still emitting only { status, detail }.
        var lambdaResponse = new BenzeneMessageClientResponse("404", JsonConvert.SerializeObject(new ProblemDetails { Detail = "Order 123 not found" }));

        var result = lambdaResponse.AsBenzeneResult<ExamplePayload>(new JsonSerializer());

        var error = Assert.Single(result.Errors);
        Assert.Equal("Order 123 not found", error.Message);
        Assert.Null(error.Field);
        Assert.Null(error.Code);
    }

    [Fact]
    public void Map_UnrecognizedStatusCode_PassesThroughVerbatimButStaysUnsuccessful()
    {
        // "999" is neither a known Benzene status nor one of the mapped numeric HTTP codes, so it
        // round-trips verbatim (an application-defined status, per NormalizeStatus) instead of being
        // coerced to unexpected-error. With no wire isSuccessful (this sender didn't provide one),
        // classification falls back to the known-status vocabulary, which doesn't recognize "999" -
        // so it still comes back unsuccessful, matching the historical safe default.
        var lambdaResponse = new BenzeneMessageClientResponse("999", null);

        var lambdaBenzeneResult = lambdaResponse.AsBenzeneResult<ExamplePayload>(new JsonSerializer());

        Assert.Equal("999", lambdaBenzeneResult.Status);
        Assert.False(lambdaBenzeneResult.IsSuccessful);
    }
}
