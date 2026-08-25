using Xunit;

namespace Benzene.Mesh.Host.Test;

/// <summary>
/// Task 5.1 (work/enterprise/slice-5-packaging.md): startup logs the effective config so "it isn't
/// picking up my Tempo URL" is answerable without a debugger - but an option value can carry a token
/// someone pasted in against advice, so redaction by key name is mandatory. Guards the one rule that
/// matters: the key always prints, the value never does when the key looks secret-shaped.
/// </summary>
public class MeshConfigSummaryTest
{
    [Fact]
    public void ArtifactStoreOptionNamedApiKey_KeyPrintedValueRedacted()
    {
        var config = new MeshHostConfig
        {
            ArtifactStore = new MeshArtifactStoreConfig
            {
                Type = "s3",
                Options = new Dictionary<string, string> { ["bucket"] = "my-bucket", ["apiKey"] = "super-secret-value-123" },
            },
        };

        var summary = MeshConfigSummary.Format(config);

        Assert.Contains("apiKey", summary);
        Assert.DoesNotContain("super-secret-value-123", summary);
        Assert.Contains("bucket=my-bucket", summary);
    }

    [Fact]
    public void ServiceSourceOptionNamedToken_KeyPrintedValueRedacted()
    {
        var config = new MeshHostConfig
        {
            Services = new[]
            {
                new MeshHostServiceConfig
                {
                    Name = "payments-fn",
                    Source = "AwsLambdaInvoke",
                    SourceOptions = new Dictionary<string, string> { ["functionName"] = "payments-fn", ["token"] = "abc123" },
                },
            },
        };

        var summary = MeshConfigSummary.Format(config);

        Assert.Contains("payments-fn", summary);
        Assert.Contains("token", summary);
        Assert.DoesNotContain("abc123", summary);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("Secret")]
    [InlineData("clientToken")]
    [InlineData("ApiKey")]
    [InlineData("credential")]
    [InlineData("ConnectionString")]
    public void SecretShapedKeyNames_AreRedactedRegardlessOfCase(string key)
    {
        var config = new MeshHostConfig
        {
            Fleet = new MeshFleetConfig { Source = "tempo", Options = new Dictionary<string, string> { [key] = "sensitive-value" } },
        };

        var summary = MeshConfigSummary.Format(config);

        Assert.Contains(key, summary);
        Assert.DoesNotContain("sensitive-value", summary);
    }

    [Theory]
    [InlineData("bucket")]
    [InlineData("windowHours")]
    [InlineData("prefix")]
    [InlineData("functionName")]
    public void OrdinaryOptionKeys_AreNotRedacted(string key)
    {
        var config = new MeshHostConfig
        {
            Fleet = new MeshFleetConfig { Source = "tempo", Options = new Dictionary<string, string> { [key] = "plain-value" } },
        };

        var summary = MeshConfigSummary.Format(config);

        Assert.Contains($"{key}=plain-value", summary);
    }

    // WP-1(a) (#27's startup-summary omission): auth.dispatchRole was bound and validated but never
    // printed in the startup summary, so an operator debugging "why is dispatch refusing everyone" had
    // no way to see it was even set without opening mesh.json.
    [Fact]
    public void AuthDispatchRoleSet_AppearsInSummary()
    {
        var config = new MeshHostConfig
        {
            Auth = new MeshAuthConfig { Mode = "oidc", DispatchRole = "mesh-admins" },
        };

        var summary = MeshConfigSummary.Format(config);

        Assert.Contains("dispatchRole=mesh-admins", summary);
    }

    [Fact]
    public void AuthDispatchRoleUnset_DoesNotAppearInSummary()
    {
        var summary = MeshConfigSummary.Format(new MeshHostConfig());

        Assert.DoesNotContain("dispatchRole", summary);
    }

    [Fact]
    public void DefaultConfig_FormatsWithoutThrowing()
    {
        // Every section defaults (services empty, no options anywhere) - the common case (no
        // mesh.json at all, env-vars-only) must not throw on an unconfigured MeshHostConfig.
        var summary = MeshConfigSummary.Format(new MeshHostConfig());

        Assert.Contains("artifactStore: type=file", summary);
        Assert.Contains("services: 0", summary);
        Assert.Contains("registryDocuments: (none)", summary);
        Assert.Contains("usage: (none)", summary);
        Assert.Contains("fleet: source=none", summary);
        Assert.Contains("auth: mode=none", summary);
    }
}
