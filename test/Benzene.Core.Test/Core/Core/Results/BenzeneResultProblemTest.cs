using Benzene.Abstractions.Results;
using Benzene.Results;
using Xunit;

namespace Benzene.Test.Core.Core.Results;

/// <summary>
/// Coverage for Phase 5 of work/problem-details-plan.md: <see cref="BenzeneResult.Problem{T}"/> (the
/// handler-authored-problem factory), <see cref="IHasProblemDetails"/>, and the
/// <see cref="BenzeneResultExtensions.GetProblem"/> typed accessor - received vs. synthesized vs.
/// handler-authored.
/// </summary>
public class BenzeneResultProblemTest
{
    [Fact]
    public void Problem_BuildsAnUnsuccessfulResult_WithStatusFromTheProblemsBenzeneStatus()
    {
        var problem = new ProblemDetails
        {
            Type = "https://example.com/problems/out-of-stock",
            Title = "Out of stock",
            BenzeneStatus = BenzeneResultStatus.Conflict,
            Detail = "SKU 123 is out of stock",
        };

        var result = BenzeneResult.Problem<Void>(problem);

        Assert.Equal(BenzeneResultStatus.Conflict, result.Status);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public void Problem_NonGeneric_BuildsAnUnsuccessfulResult_WithStatusFromTheProblemsBenzeneStatus()
    {
        var problem = new ProblemDetails { BenzeneStatus = BenzeneResultStatus.NotFound };

        var result = BenzeneResult.Problem(problem);

        Assert.Equal(BenzeneResultStatus.NotFound, result.Status);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public void Problem_ProjectsErrorsFromTheProblemsErrorsMember()
    {
        var errors = new[]
        {
            new BenzeneError("Name must not be empty", "Name", "NotEmptyValidator"),
            new BenzeneError("Age must be greater than 0", "Age", "GreaterThanValidator"),
        };
        var problem = new ProblemDetails { BenzeneStatus = BenzeneResultStatus.ValidationError, Errors = errors };

        var result = BenzeneResult.Problem<Void>(problem);

        Assert.Equal(errors, result.Errors);
    }

    [Fact]
    public void Problem_NullErrorsOnTheProblem_YieldsAnEmptyErrorsList()
    {
        var problem = new ProblemDetails { BenzeneStatus = BenzeneResultStatus.BadRequest };

        var result = BenzeneResult.Problem<Void>(problem);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Problem_MissingBenzeneStatus_ThrowsArgumentException()
    {
        var problem = new ProblemDetails { Detail = "no status here" };

        var exception = Assert.Throws<System.ArgumentException>(() => BenzeneResult.Problem<Void>(problem));
        Assert.Contains("BenzeneStatus", exception.Message);
    }

    [Fact]
    public void Problem_EmptyBenzeneStatus_ThrowsArgumentException()
    {
        var problem = new ProblemDetails { BenzeneStatus = "" };

        Assert.Throws<System.ArgumentException>(() => BenzeneResult.Problem<Void>(problem));
    }

    [Fact]
    public void Problem_NullProblem_ThrowsArgumentNullException()
    {
        Assert.Throws<System.ArgumentNullException>(() => BenzeneResult.Problem<Void>(null!));
    }

    [Fact]
    public void GetProblem_HandlerAuthoredProblem_ReturnsItVerbatim()
    {
        var problem = new ProblemDetails
        {
            Type = "https://example.com/problems/out-of-stock",
            Title = "Out of stock",
            Instance = "https://example.com/orders/42",
            BenzeneStatus = BenzeneResultStatus.Conflict,
            Detail = "SKU 123 is out of stock",
        };
        var result = BenzeneResult.Problem<Void>(problem);

        var returned = result.GetProblem();

        Assert.Same(problem, returned);
    }

    [Fact]
    public void GetProblem_OrdinaryFailure_SynthesizesFromTheRegistry()
    {
        var result = BenzeneResult.NotFound("Order 123 not found");

        var problem = result.GetProblem();

        Assert.Equal("https://benzene.app/problems/not-found", problem.Type);
        Assert.Equal(BenzeneResultStatus.NotFound, problem.BenzeneStatus);
        Assert.Equal("Order 123 not found", problem.Detail);
    }

    [Fact]
    public void GetProblem_OrdinaryFailure_CalledTwice_ReturnsDistinctButEqualContentInstances()
    {
        var result = BenzeneResult.NotFound("Order 123 not found");

        var first = result.GetProblem();
        var second = result.GetProblem();

        Assert.NotSame(first, second);
        Assert.Equal(first.Type, second.Type);
        Assert.Equal(first.Detail, second.Detail);
    }

    [Fact]
    public void GetProblem_SuccessfulResult_StillReturnsANonNullSynthesizedDocument()
    {
        // GetProblem is a total function - even a successful result gets a (largely empty) document
        // back rather than null, since ProblemTypes.From has no special case for success.
        var result = BenzeneResult.Ok();

        var problem = result.GetProblem();

        Assert.NotNull(problem);
        Assert.Null(problem.Type);
    }
}
