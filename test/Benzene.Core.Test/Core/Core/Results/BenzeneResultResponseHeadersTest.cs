using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers.Response;
using Benzene.Results;
using Moq;
using Xunit;

namespace Benzene.Test.Core.Core.Results;

// FR-3: response headers on a result (WithHeader / GetResponseHeaders), and the response handler that
// writes them without replacing the pipeline's own.
public class BenzeneResultResponseHeadersTest
{
    [Fact]
    public void NoHeaders_GetResponseHeaders_IsEmpty()
    {
        Assert.Empty(BenzeneResult.Ok("x").GetResponseHeaders());
    }

    [Fact]
    public void WithHeader_ReturnsACopy_TheOriginalIsUnchanged()
    {
        var original = BenzeneResult.Created("payload");

        var withHeader = original.WithHeader("Location", "/things/1");

        Assert.Empty(original.GetResponseHeaders());
        Assert.Equal("/things/1", withHeader.GetResponseHeaders()["Location"]);
        Assert.Equal(BenzeneResultStatus.Created, withHeader.Status);
        Assert.True(withHeader.IsSuccessful);
        Assert.Equal("payload", withHeader.Payload);
    }

    [Fact]
    public void WithHeader_SameNameIgnoringCase_LastWins()
    {
        var result = BenzeneResult.Ok("x").WithHeader("ETag", "\"1\"").WithHeader("etag", "\"2\"");

        var headers = result.GetResponseHeaders();
        Assert.Single(headers);
        Assert.Equal("\"2\"", headers["ETAG"]);
    }

    [Fact]
    public void WithHeaders_AddsEveryHeader()
    {
        var result = BenzeneResult.Ok("x").WithHeaders(new Dictionary<string, string>
        {
            ["Location"] = "/a",
            ["Cache-Control"] = "no-store"
        });

        Assert.Equal(2, result.GetResponseHeaders().Count);
    }

    [Fact]
    public void WithHeader_OnAFailure_KeepsErrors()
    {
        var result = BenzeneResult.Conflict<string>("taken").WithHeader("X-Existing", "/a");

        Assert.False(result.IsSuccessful);
        Assert.Equal("taken", Assert.Single(result.Errors).Message);
        Assert.Equal("/a", result.GetResponseHeaders()["X-Existing"]);
    }

    [Fact]
    public void WithHeader_KeepsAnAttachedProblem()
    {
        var problem = new ProblemDetails { BenzeneStatus = BenzeneResultStatus.Conflict, Type = "https://example.com/taken" };

        var result = BenzeneResult.Problem<string>(problem).WithHeader("X-Existing", "/a");

        Assert.Same(problem, result.GetProblem());
    }

    [Fact]
    public void WithHeader_NonGeneric_KeepsStatusAndAddsHeader()
    {
        IBenzeneResult result = BenzeneResult.Accepted();

        var withHeader = result.WithHeader("Location", "/jobs/1");

        Assert.Equal(BenzeneResultStatus.Accepted, withHeader.Status);
        Assert.Equal("/jobs/1", withHeader.GetResponseHeaders()["Location"]);
    }

    [Theory]
    [InlineData("X-Bad", "a\r\nSet-Cookie: x=1")]
    [InlineData("X-Bad", "a\nb")]
    [InlineData("X-Bad", "a\0b")]
    [InlineData("Bad Name", "a")]
    [InlineData("Bad:Name", "a")]
    [InlineData("", "a")]
    public void WithHeader_InvalidNameOrValue_Throws(string name, string value)
    {
        Assert.Throws<ArgumentException>(() => BenzeneResult.Ok("x").WithHeader(name, value));
    }

    [Fact]
    public void As_KeepsHeadersAndProblem()
    {
        var problem = new ProblemDetails { BenzeneStatus = BenzeneResultStatus.Conflict };
        var failure = BenzeneResult.Problem<string>(problem).WithHeader("X-A", "1");
        var success = BenzeneResult.Ok("x").WithHeader("X-B", "2");

        var mappedFailure = failure.As<string, int>(_ => 1);
        var mappedSuccess = success.As<string, int>(s => s.Length);

        Assert.Equal("1", mappedFailure.GetResponseHeaders()["X-A"]);
        Assert.Same(problem, mappedFailure.GetProblem());
        Assert.Equal("2", mappedSuccess.GetResponseHeaders()["X-B"]);
        Assert.Equal(1, mappedSuccess.Payload);
    }

    [Fact]
    public async Task ResponseHandler_WritesResultHeaders_SkipsOnesThePipelineSet()
    {
        var written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Access-Control-Allow-Origin"] = "https://example.com"
        };
        var adapter = new Mock<IBenzeneResponseAdapter<object>>();
        adapter.Setup(x => x.HasResponseHeader(It.IsAny<object>(), It.IsAny<string>()))
            .Returns((object _, string name) => written.ContainsKey(name));
        adapter.Setup(x => x.SetResponseHeader(It.IsAny<object>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback((object _, string name, string value) => written[name] = value);
        var result = new Mock<IMessageHandlerResult>();
        result.Setup(x => x.BenzeneResult).Returns(BenzeneResult.Created("x")
            .WithHeader("Location", "/things/1")
            .WithHeader("Access-Control-Allow-Origin", "https://not-allowed.example"));

        await new ResultHeadersResponseHandler<object>(adapter.Object).HandleAsync(new object(), result.Object);

        Assert.Equal("/things/1", written["Location"]);
        Assert.Equal("https://example.com", written["Access-Control-Allow-Origin"]);
    }

    [Fact]
    public async Task ResponseHandler_NoHeaders_WritesNothing()
    {
        var adapter = new Mock<IBenzeneResponseAdapter<object>>(MockBehavior.Strict);
        var result = new Mock<IMessageHandlerResult>();
        result.Setup(x => x.BenzeneResult).Returns(BenzeneResult.Ok("x"));

        await new ResultHeadersResponseHandler<object>(adapter.Object).HandleAsync(new object(), result.Object);
    }
}
