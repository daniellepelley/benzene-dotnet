using Xunit;

namespace Benzene.Mesh.Auth.Oidc.Test;

public class ValidIssuersForTest
{
    [Fact]
    public void GoogleIssuer_AcceptsBothSchemeAndSchemelessForms()
    {
        var issuers = Extensions.ValidIssuersFor("https://accounts.google.com");

        Assert.Contains("https://accounts.google.com", issuers);
        Assert.Contains("accounts.google.com", issuers);
        Assert.Equal(2, issuers.Length);
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/common/v2.0")]
    [InlineData("https://example.okta.com")]
    [InlineData("https://accounts.google.com.evil.com")]
    public void OtherIssuers_AreExactMatchOnly(string issuer)
    {
        var issuers = Extensions.ValidIssuersFor(issuer);

        Assert.Single(issuers);
        Assert.Equal(issuer, issuers[0]);
    }
}
