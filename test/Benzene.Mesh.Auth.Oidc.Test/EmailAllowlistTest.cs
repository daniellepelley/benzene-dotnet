using System.Collections.Generic;
using Xunit;

namespace Benzene.Mesh.Auth.Oidc.Test;

public class EmailAllowlistTest
{
    [Fact]
    public void ExactMatch_IsAllowed()
    {
        Assert.True(EmailAllowlist.IsAllowed(new[] { "user@example.com" }, "user@example.com"));
    }

    [Theory]
    [InlineData("USER@EXAMPLE.COM")]
    [InlineData("User@Example.Com")]
    [InlineData("user@EXAMPLE.com")]
    public void CaseInsensitive_IsAllowed(string variant)
    {
        Assert.True(EmailAllowlist.IsAllowed(new[] { "user@example.com" }, variant));
    }

    [Fact]
    public void NotOnList_IsNotAllowed()
    {
        Assert.False(EmailAllowlist.IsAllowed(new[] { "user@example.com" }, "other@example.com"));
    }

    [Fact]
    public void SubstringMatch_IsNotAllowed()
    {
        // Exact match only - a naive "Contains" check would wrongly allow this.
        Assert.False(EmailAllowlist.IsAllowed(new[] { "user@example.com" }, "notuser@example.com"));
        Assert.False(EmailAllowlist.IsAllowed(new[] { "user@example.com" }, "user@example.com.evil.com"));
    }

    [Fact]
    public void EmptyAllowlist_DeniesEveryone()
    {
        Assert.False(EmailAllowlist.IsAllowed(System.Array.Empty<string>(), "user@example.com"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrEmptyEmail_IsNotAllowed(string? email)
    {
        Assert.False(EmailAllowlist.IsAllowed(new[] { "user@example.com" }, email));
    }

    [Fact]
    public void WhitespacePaddedAllowlistEntry_StillMatchesTrimmed()
    {
        var list = new List<string> { " user@example.com " };
        Assert.True(EmailAllowlist.IsAllowed(list, "user@example.com"));
    }
}
