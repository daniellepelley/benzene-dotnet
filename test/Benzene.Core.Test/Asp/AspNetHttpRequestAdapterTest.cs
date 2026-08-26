using Benzene.AspNet.Core;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Benzene.Test.Asp;

// #164 (round-11 review): AspNetHttpRequestAdapter has the same header-casing/null-Method/Path defect
// class already fixed for API Gateway in #105 (ApiGatewayHttpRequestAdapterHeaderCasingTest). This
// adapter also serves the Google Cloud Functions HTTP trigger, Cloud Run, and
// Benzene.Azure.Function.AspNet, since they all run through the same ASP.NET Core request pipeline.
public class AspNetHttpRequestAdapterTest
{
    [Fact]
    public void Map_HeaderLookup_IsCaseInsensitiveRegardlessOfLookupCasing()
    {
        // Before the fix: ToDictionary(...) built a plain-ordinal dictionary of already-lower-cased
        // keys, so a lookup with any casing other than the exact lower-cased form the adapter chose
        // would fail even though every key had already been lower-cased.
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Bearer abc123";

        var request = new AspNetHttpRequestAdapter().Map(new AspNetContext(httpContext));

        Assert.True(request.Headers.TryGetValue("Authorization", out var mixedCase));
        Assert.Equal("Bearer abc123", mixedCase);
        Assert.True(request.Headers.TryGetValue("AUTHORIZATION", out var upperCase));
        Assert.Equal("Bearer abc123", upperCase);
        Assert.True(request.Headers.TryGetValue("authorization", out var lowerCase));
        Assert.Equal("Bearer abc123", lowerCase);
    }

    [Fact]
    public void Map_MixedCaseHeaders_AreLowerCased()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Origin"] = "https://example.com";

        var request = new AspNetHttpRequestAdapter().Map(new AspNetContext(httpContext));

        Assert.True(request.Headers.TryGetValue("origin", out var origin));
        Assert.Equal("https://example.com", origin);
    }

    [Fact]
    public void Map_DefaultPath_DoesNotSurfaceNull()
    {
        // PathString's implicit conversion to string is null for a default/unset PathString (e.g. a
        // hand-built HttpContext in a test, or a minimal synthetic request) - HttpRequest.Path
        // promises a non-null string.
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = default;

        var request = new AspNetHttpRequestAdapter().Map(new AspNetContext(httpContext));

        Assert.NotNull(request.Path);
        Assert.Equal(string.Empty, request.Path);
    }

    [Fact]
    public void Map_NullMethod_DefaultsToEmptyString_DoesNotThrow()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = null!;

        var request = new AspNetHttpRequestAdapter().Map(new AspNetContext(httpContext));

        Assert.NotNull(request.Method);
        Assert.Equal(string.Empty, request.Method);
    }
}
