using System;
using Xunit;

namespace Benzene.Mesh.Auth.Oidc.Test;

public class CookieHeaderTest
{
    [Fact]
    public void Parse_ReadsMultipleCookies()
    {
        var cookies = CookieHeader.Parse("a=1; b=2; c=3");

        Assert.Equal("1", cookies["a"]);
        Assert.Equal("2", cookies["b"]);
        Assert.Equal("3", cookies["c"]);
    }

    [Fact]
    public void Parse_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(CookieHeader.Parse(null));
        Assert.Empty(CookieHeader.Parse(""));
    }

    [Fact]
    public void Build_SetsSecurityAttributes()
    {
        var value = CookieHeader.Build("session", "abc123", "/", TimeSpan.FromHours(24));

        Assert.Contains("session=abc123", value);
        Assert.Contains("Path=/", value);
        Assert.Contains("Max-Age=86400", value);
        Assert.Contains("HttpOnly", value);
        Assert.Contains("Secure", value);
        Assert.Contains("SameSite=Lax", value);
    }

    [Fact]
    public void BuildExpired_SetsMaxAgeZero()
    {
        var value = CookieHeader.BuildExpired("session", "/");

        Assert.Contains("session=;", value);
        Assert.Contains("Max-Age=0", value);
    }
}
