using Xunit;

namespace Benzene.Mesh.Auth.Oidc.Test;

public class ReturnToValidatorTest
{
    [Theory]
    [InlineData("/mesh-ui")]
    [InlineData("/")]
    [InlineData("/mesh/auth/../mesh-ui")]
    [InlineData("/mesh-ui?tab=catalog")]
    public void SafeRelativePaths_AreAccepted(string returnTo)
    {
        Assert.True(ReturnToValidator.IsSafe(returnTo));
    }

    [Theory]
    [InlineData("https://evil.com")]
    [InlineData("http://evil.com")]
    [InlineData("//evil.com")]
    [InlineData("/\\evil.com")]
    [InlineData("evil.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/x?redirect=https://evil.com")] // still starts with / and contains :// later - rejected by the blanket "://" ban
    public void UnsafeOrOpenRedirectValues_AreRejected(string returnTo)
    {
        Assert.False(ReturnToValidator.IsSafe(returnTo));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrEmpty_IsRejected(string? returnTo)
    {
        Assert.False(ReturnToValidator.IsSafe(returnTo));
    }

    [Fact]
    public void ControlCharacters_AreRejected()
    {
        Assert.False(ReturnToValidator.IsSafe("/\tevil"));
        Assert.False(ReturnToValidator.IsSafe("/\r\nevil"));
    }
}
