using System.Collections.Generic;
using Amazon.Lambda.APIGatewayEvents;
using Benzene.Aws.Lambda.ApiGateway;
using Benzene.Http.Routing;
using Moq;
using Xunit;

namespace Benzene.Test.Aws.ApiGateway;

// #89 (worth-fixing, security-adjacent): ApiGatewayHttpRequestAdapter (v1) never normalized header
// casing - it passed AWS's raw, case-sensitive, original-casing dictionary straight through, unlike
// AspNetHttpRequestAdapter and ApiGatewayV2Context.CombinedHeaders(). Every consumer (auth/CORS
// middleware) reads headers by lowercase literal key, relying on HttpRequest.AsLowerCase() having
// been called first - so this is the round-9 repro: a raw Map() result's TryGetValue("authorization",
// ...) must now succeed WITHOUT AsLowerCase() being called first.
public class ApiGatewayHttpRequestAdapterHeaderCasingTest
{
    [Fact]
    public void Map_MixedCaseHeaders_AreLowerCasedWithoutNeedingAsLowerCase()
    {
        var context = new ApiGatewayContext(new APIGatewayProxyRequest
        {
            Path = "/example",
            HttpMethod = "GET",
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer abc123",
                ["Origin"] = "https://example.com"
            }
        });

        var request = new ApiGatewayHttpRequestAdapter().Map(context);

        Assert.True(request.Headers.TryGetValue("authorization", out var authorization));
        Assert.Equal("Bearer abc123", authorization);
        Assert.True(request.Headers.TryGetValue("origin", out var origin));
        Assert.Equal("https://example.com", origin);
    }

    [Fact]
    public void Map_NullHeaders_ReturnsEmptyDictionary_DoesNotThrow()
    {
        var context = new ApiGatewayContext(new APIGatewayProxyRequest
        {
            Path = "/example",
            HttpMethod = "GET",
            Headers = null
        });

        var request = new ApiGatewayHttpRequestAdapter().Map(context);

        Assert.Empty(request.Headers);
    }
}

// #90 (worth-fixing): AspNetRequestEnricher takes the FIRST value for a repeated query key, while
// the API Gateway v1/v2 enrichers passed QueryStringParameters through as-is - which, per AWS's
// payload shapes, effectively keeps only the LAST value for v1 (the single-value map) and a
// comma-joined value for v2. Chosen policy: first-value-wins everywhere (matching AspNet, the more
// common convention). Verifies the policy is now applied identically across all three enrichers.
public class ApiGatewayQueryStringFirstWinsTest
{
    private static Mock<IRouteFinder> RouteFinderMatchingAnything()
    {
        var routeFinder = new Mock<IRouteFinder>();
        routeFinder
            .Setup(x => x.Find(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new HttpTopicRoute("example:topic", new Dictionary<string, object>()));
        return routeFinder;
    }

    private static Mock<Benzene.Http.IHttpHeaderMappings> NoHeaderMappings()
    {
        var mappings = new Mock<Benzene.Http.IHttpHeaderMappings>();
        mappings.Setup(x => x.GetMappings()).Returns(new Dictionary<string, string>());
        return mappings;
    }

    [Fact]
    public void V1_RepeatedQueryKey_MultiValueMapAvailable_FirstValueWins()
    {
        // Real API Gateway REST API (v1) proxy events populate BOTH maps: QueryStringParameters
        // (single value - AWS keeps the LAST occurrence) and MultiValueQueryStringParameters (every
        // value, in order).
        var request = new APIGatewayProxyRequest
        {
            Path = "/example",
            HttpMethod = "GET",
            QueryStringParameters = new Dictionary<string, string> { ["status"] = "inactive" },
            MultiValueQueryStringParameters = new Dictionary<string, IList<string>>
            {
                ["status"] = new List<string> { "active", "inactive" }
            }
        };

        var enricher = new ApiGatewayRequestEnricher(RouteFinderMatchingAnything().Object, NoHeaderMappings().Object);
        var dictionary = enricher.Enrich<object>(null, new ApiGatewayContext(request));

        Assert.Equal("active", dictionary["status"]);
    }

    [Fact]
    public void V1_RepeatedQueryKey_NoMultiValueMap_FallsBackToSingleValueMap()
    {
        // A hand-built payload (or a genuinely single-valued request) that only sets the single-value
        // field must still work.
        var request = new APIGatewayProxyRequest
        {
            Path = "/example",
            HttpMethod = "GET",
            QueryStringParameters = new Dictionary<string, string> { ["status"] = "active" }
        };

        var enricher = new ApiGatewayRequestEnricher(RouteFinderMatchingAnything().Object, NoHeaderMappings().Object);
        var dictionary = enricher.Enrich<object>(null, new ApiGatewayContext(request));

        Assert.Equal("active", dictionary["status"]);
    }

    [Fact]
    public void V2_RepeatedQueryKey_CommaJoinedByApiGateway_FirstSegmentWins()
    {
        // API Gateway HTTP API (v2, payload format 2.0) joins repeated values for the same key into
        // one comma-separated string before Lambda ever sees the event - there is no multi-value map.
        var request = new APIGatewayHttpApiV2ProxyRequest
        {
            RawPath = "/example",
            QueryStringParameters = new Dictionary<string, string> { ["status"] = "active,inactive" },
            RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
            {
                Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription { Method = "GET", Path = "/example" }
            }
        };

        var enricher = new ApiGatewayV2RequestEnricher(RouteFinderMatchingAnything().Object, NoHeaderMappings().Object);
        var dictionary = enricher.Enrich<object>(null, new ApiGatewayV2Context(request));

        Assert.Equal("active", dictionary["status"]);
    }

    [Fact]
    public void V1AndV2AndAspNet_AgreeOnFirstWinsPolicy()
    {
        // Documents the cross-transport parity #90 asks for: given the same logical repeated query
        // key, all three transports resolve to the SAME (first) value for the identical route.
        var v1Request = new APIGatewayProxyRequest
        {
            Path = "/example",
            HttpMethod = "GET",
            MultiValueQueryStringParameters = new Dictionary<string, IList<string>>
            {
                ["status"] = new List<string> { "active", "inactive" }
            }
        };
        var v1Dictionary = new ApiGatewayRequestEnricher(RouteFinderMatchingAnything().Object, NoHeaderMappings().Object)
            .Enrich<object>(null, new ApiGatewayContext(v1Request));

        var v2Request = new APIGatewayHttpApiV2ProxyRequest
        {
            RawPath = "/example",
            QueryStringParameters = new Dictionary<string, string> { ["status"] = "active,inactive" },
            RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
            {
                Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription { Method = "GET", Path = "/example" }
            }
        };
        var v2Dictionary = new ApiGatewayV2RequestEnricher(RouteFinderMatchingAnything().Object, NoHeaderMappings().Object)
            .Enrich<object>(null, new ApiGatewayV2Context(v2Request));

        Assert.Equal("active", v1Dictionary["status"]);
        Assert.Equal("active", v2Dictionary["status"]);
    }
}
