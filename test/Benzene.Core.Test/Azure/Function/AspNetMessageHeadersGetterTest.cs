using Benzene.Azure.Function.AspNet;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Benzene.Test.Azure.Function;

/// <summary>
/// Regression coverage for <see cref="AspNetMessageHeadersGetter"/>: header field names are
/// lower-cased for lookup stability, but VALUES must be preserved verbatim (they used to be
/// lower-cased too, which corrupts bearer tokens, correlation IDs, base64, etc.).
/// </summary>
public class AspNetMessageHeadersGetterTest
{
    private static AspNetContext ContextWithHeaders(params (string Key, string Value)[] headers)
    {
        var httpContext = new DefaultHttpContext();
        foreach (var (key, value) in headers)
        {
            httpContext.Request.Headers[key] = value;
        }

        return new AspNetContext(httpContext.Request);
    }

    [Fact]
    public void GetHeaders_PreservesCaseSensitiveValues_LowerCasingOnlyTheName()
    {
        var getter = new AspNetMessageHeadersGetter();

        var result = getter.GetHeaders(ContextWithHeaders(
            ("Authorization", "Bearer AbCdEf123=="),
            ("X-Correlation-Id", "Corr-XYZ")));

        // Name lower-cased for stability; value untouched.
        Assert.Equal("Bearer AbCdEf123==", result["authorization"]);
        // x-correlation-id passes through unmapped (like every other header getter), so the
        // diagnostics module's inbound trace tag - which reads the same default key - finds it here.
        Assert.Equal("Corr-XYZ", result["x-correlation-id"]);
    }

    [Fact]
    public void GetHeaders_ResultDictionary_IsCaseInsensitiveRegardlessOfLookupCasing()
    {
        // #165/#164: ToDictionary with no comparer built a plain-ordinal dictionary of
        // already-lower-cased keys, so a lookup with any casing other than the exact lower-cased
        // form chosen here would fail even though every key had already been lower-cased.
        var getter = new AspNetMessageHeadersGetter();

        var result = getter.GetHeaders(ContextWithHeaders(("X-Tenant-Id", "tenant-1")));

        Assert.True(result.TryGetValue("x-tenant-id", out var lowerCase));
        Assert.Equal("tenant-1", lowerCase);
        Assert.True(result.TryGetValue("X-TENANT-ID", out var upperCase));
        Assert.Equal("tenant-1", upperCase);
    }
}
