using System.Collections.Generic;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Core.Messages;
using Benzene.HealthChecks.Core;
using Benzene.Http;
using Benzene.Results;
using Moq;
using Xunit;

namespace Benzene.Test.Http;

/// <summary>
/// The HTTP status line for a result - and the one place it reads a payload field instead: a health
/// verdict (transport-bindings.md §1). A health query is <c>ok</c> whatever it found; a route mapped to a
/// health topic answers 503 when <c>isHealthy</c> is false.
/// </summary>
public class HttpStatusCodeResponseHandlerTest
{
    private static string StatusFor(IBenzeneResult result)
    {
        var adapter = new Mock<IBenzeneResponseAdapter<object>>();
        string? status = null;
        adapter.Setup(x => x.SetStatusCode(It.IsAny<object>(), It.IsAny<string>()))
            .Callback<object, string>((_, s) => status = s);

        var handler = new HttpStatusCodeResponseHandler<object>(adapter.Object, new DefaultHttpStatusCodeMapper());
        handler.HandleAsync(new object(),
            new MessageHandlerResult(new Topic("benzene:healthcheck"), MessageHandlerDefinition.Empty(), result));
        return status!;
    }

    private static HealthCheckResponse Report(bool healthy) =>
        new(healthy, new Dictionary<string, HealthCheckResult>());

    [Fact]
    public void AnUnhealthyHealthReport_Is503()
    {
        Assert.Equal("503", StatusFor(BenzeneResult.Ok(Report(healthy: false))));
    }

    [Fact]
    public void AHealthyHealthReport_Is200()
    {
        Assert.Equal("200", StatusFor(BenzeneResult.Ok(Report(healthy: true))));
    }

    [Fact]
    public void AFailedHealthInvocation_MapsThroughTheStatusTableAsUsual()
    {
        // A health invocation that itself failed is not a verdict: it maps by its status (wire §4.1).
        Assert.Equal("500", StatusFor(BenzeneResult.UnexpectedError<HealthCheckResponse>("boom")));
    }

    [Fact]
    public void AnyOtherPayload_MapsByStatus()
    {
        Assert.Equal("201", StatusFor(BenzeneResult.Created(new { id = 1 })));
        Assert.Equal("404", StatusFor(BenzeneResult.NotFound<object>("nope")));
    }
}
