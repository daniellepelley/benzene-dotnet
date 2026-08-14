using Benzene.Abstractions.Results;
using Benzene.Results;
using Xunit;

namespace Benzene.Test.Core.Core.Results;

/// <summary>
/// Coverage for the problem-type registry (<see cref="ProblemTypes"/>) - Phase 3 of
/// work/problem-details-plan.md, §2.2's registry table.
/// </summary>
public class ProblemTypesTest
{
    [Theory]
    [InlineData(BenzeneResultStatus.BadRequest, "https://benzene.app/problems/bad-request", "Bad request", 400)]
    [InlineData(BenzeneResultStatus.Unauthorized, "https://benzene.app/problems/unauthorized", "Unauthorized", 401)]
    [InlineData(BenzeneResultStatus.Forbidden, "https://benzene.app/problems/forbidden", "Forbidden", 403)]
    [InlineData(BenzeneResultStatus.NotFound, "https://benzene.app/problems/not-found", "Not found", 404)]
    [InlineData(BenzeneResultStatus.Conflict, "https://benzene.app/problems/conflict", "Conflict", 409)]
    [InlineData(BenzeneResultStatus.ValidationError, "https://benzene.app/problems/validation-error", "Validation failed", 422)]
    [InlineData(BenzeneResultStatus.TooManyRequests, "https://benzene.app/problems/too-many-requests", "Too many requests", 429)]
    [InlineData(BenzeneResultStatus.UnexpectedError, "https://benzene.app/problems/unexpected-error", "Unexpected error", 500)]
    [InlineData(BenzeneResultStatus.NotImplemented, "https://benzene.app/problems/not-implemented", "Not implemented", 501)]
    [InlineData(BenzeneResultStatus.ServiceUnavailable, "https://benzene.app/problems/service-unavailable", "Service unavailable", 503)]
    [InlineData(BenzeneResultStatus.Timeout, "https://benzene.app/problems/timeout", "Timeout", 504)]
    public void Registry_MapsEveryFailureStatus_ToItsTypeTitleAndHttpStatus(
        string status, string expectedType, string expectedTitle, int expectedHttpStatus)
    {
        Assert.Equal(expectedType, ProblemTypes.TypeFor(status));
        Assert.Equal(expectedTitle, ProblemTypes.TitleFor(status));
        Assert.Equal(expectedHttpStatus, ProblemTypes.HttpStatusFor(status));
    }

    [Fact]
    public void UnknownOrApplicationDefinedStatus_HasNoTypeOrTitle_AndFallsBackTo500()
    {
        Assert.Null(ProblemTypes.TypeFor("some-app-status"));
        Assert.Null(ProblemTypes.TitleFor("some-app-status"));
        Assert.Equal(500, ProblemTypes.HttpStatusFor("some-app-status"));
    }

    [Fact]
    public void NullStatus_HasNoTypeOrTitle_AndFallsBackTo500()
    {
        Assert.Null(ProblemTypes.TypeFor(null));
        Assert.Null(ProblemTypes.TitleFor(null));
        Assert.Equal(500, ProblemTypes.HttpStatusFor(null));
    }

    [Fact]
    public void SuccessStatus_IsNotInTheRegistry()
    {
        // Problems exist only on failure - no row for a success status.
        Assert.Null(ProblemTypes.TypeFor(BenzeneResultStatus.Ok));
        Assert.Null(ProblemTypes.TitleFor(BenzeneResultStatus.Ok));
    }

    [Fact]
    public void From_MessageOnlyFailure_BuildsRegistryTypeTitleAndJoinedDetail_WithAMessageOnlyErrorsEntry()
    {
        var result = BenzeneResult.NotFound("Order 123 not found");

        var problem = ProblemTypes.From(result);

        Assert.Equal("https://benzene.app/problems/not-found", problem.Type);
        Assert.Equal("Not found", problem.Title);
        Assert.Equal("Order 123 not found", problem.Detail);
        Assert.Equal(BenzeneResultStatus.NotFound, problem.BenzeneStatus);
        // Every failure factory that takes a message projects it to a message-only BenzeneError (the
        // ruling's params-string[] projection, work/benzene-result-errors-ruling.md), so "errors" is
        // present here even though nothing populated Field/Code.
        var error = Assert.Single(problem.Errors!);
        Assert.Equal("Order 123 not found", error.Message);
        Assert.Null(error.Field);
        Assert.Null(error.Code);
        Assert.Null(problem.Status);
    }

    [Fact]
    public void From_FailureWithNoErrorsAtAll_OmitsTheErrorsMember()
    {
        // BenzeneResult.NotFound() with no message at all - the result's Errors collection is
        // genuinely empty (the shared EmptyErrors instance), not merely message-only.
        var problem = ProblemTypes.From(BenzeneResult.NotFound());

        Assert.Null(problem.Errors);
    }

    [Fact]
    public void From_MultipleErrors_JoinsDetailWithCommaSpace()
    {
        var result = BenzeneResult.ValidationError("Name must not be empty", "Age must be greater than 0");

        var problem = ProblemTypes.From(result);

        Assert.Equal("Name must not be empty, Age must be greater than 0", problem.Detail);
    }

    [Fact]
    public void From_StructuredErrors_CarriesThemVerbatimAndOrdered()
    {
        var errors = new[]
        {
            new BenzeneError("Name must not be empty", "Name", "NotEmptyValidator"),
            new BenzeneError("Age must be greater than 0", "Age", "GreaterThanValidator"),
        };
        var result = BenzeneResult.ValidationError(errors);

        var problem = ProblemTypes.From(result);

        Assert.NotNull(problem.Errors);
        Assert.Equal(errors, problem.Errors);
    }

    [Fact]
    public void From_ApplicationDefinedStatus_HasNoTypeOrTitle_ButStillCarriesBenzeneStatusAndDetail()
    {
        var result = BenzeneResult.Set("app-defined-status", new[] { "custom failure" });

        var problem = ProblemTypes.From(result);

        Assert.Null(problem.Type);
        Assert.Null(problem.Title);
        Assert.Equal("app-defined-status", problem.BenzeneStatus);
        Assert.Equal("custom failure", problem.Detail);
    }

    [Fact]
    public void From_NeverSetsStatus_EvenForAKnownFailure()
    {
        // ProblemDetails.Status (the numeric HTTP code) is an HTTP-binding concern filled in by a
        // later phase's HTTP-aware response mapper - this transport-neutral factory never sets it.
        var problem = ProblemTypes.From(BenzeneResult.ServiceUnavailable("db unreachable"));

        Assert.Null(problem.Status);
    }
}
