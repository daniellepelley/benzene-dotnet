using System.Linq;
using System.Reflection;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Messages;
using Benzene.Core.Exceptions;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Http.Routing;
using Benzene.Test.Examples;
using Moq;
using Xunit;

namespace Benzene.Test.Core.Http;

public class ReflectionHttpEndpointFinderTest
{
    [Fact]
    public void FindRoutes()
    {
        var httpEndpointFinder = new ReflectionHttpEndpointFinder(new ReflectionMessageHandlersFinder(Assembly.GetExecutingAssembly()));

        var findRoutes = httpEndpointFinder.FindDefinitions();

        var exampleRoutes = findRoutes.Where(x => x.Topic == Defaults.Topic).ToArray();
        
        Assert.Equal("GET", exampleRoutes[0].Method);
        Assert.Equal("/example/{id}", exampleRoutes[0].Path);
        
        Assert.Equal("GET", exampleRoutes[1].Method);
        Assert.Equal("/example", exampleRoutes[1].Path);
    }
    
    [Fact]
    public void FindRoutes_NoResponse()
    {
        var httpEndpointFinder = new ReflectionHttpEndpointFinder(new ReflectionMessageHandlersFinder(Assembly.GetExecutingAssembly()));

        var findRoutes = httpEndpointFinder.FindDefinitions();

        var exampleNoResponseRoute = findRoutes.First(x => x.Topic == Defaults.TopicNoResponse);
        Assert.Equal("GET", exampleNoResponseRoute.Method);
        Assert.Equal("/example-no-response", exampleNoResponseRoute.Path);
    }

    // #91: RouteFinder/CompiledRoutePath match method+path case-INSENSITIVELY at runtime, but the
    // startup duplicate-route check used to GroupBy the raw (case-sensitive) Method/Path, so two
    // handlers registering a case-differing "same" route weren't flagged - the second silently
    // became unreachable dead code instead of the documented fail-fast BenzeneException. Case-fold
    // the grouping key (not the stored values) and this must now throw.
    [HttpEndpoint("GET", "/case-fold-test")]
    private class LowerCaseRouteHandler
    {
    }

    [HttpEndpoint("get", "/CASE-FOLD-TEST")]
    private class UpperCaseRouteHandler
    {
    }

    private static IMessageHandlerDefinition BuildHandlerDefinition(System.Type handlerType, string topicId)
    {
        var topic = new Mock<ITopic>();
        topic.Setup(x => x.Id).Returns(topicId);

        var definition = new Mock<IMessageHandlerDefinition>();
        definition.Setup(x => x.HandlerType).Returns(handlerType);
        definition.Setup(x => x.Topic).Returns(topic.Object);
        return definition.Object;
    }

    [Fact]
    public void FindDefinitions_CaseDifferingDuplicateRoute_ThrowsBenzeneException()
    {
        var messageHandlersFinder = new Mock<IMessageHandlersFinder>();
        messageHandlersFinder.Setup(x => x.FindDefinitions()).Returns(new[]
        {
            BuildHandlerDefinition(typeof(LowerCaseRouteHandler), "case-fold-test:lower"),
            BuildHandlerDefinition(typeof(UpperCaseRouteHandler), "case-fold-test:upper"),
        });

        var httpEndpointFinder = new ReflectionHttpEndpointFinder(messageHandlersFinder.Object);

        Assert.Throws<BenzeneException>(() => httpEndpointFinder.FindDefinitions());
    }
}
