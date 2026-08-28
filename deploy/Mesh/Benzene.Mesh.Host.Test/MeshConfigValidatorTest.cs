using Xunit;

namespace Benzene.Mesh.Host.Test;

/// <summary>
/// Backs <c>--validate-config</c> (<see cref="Program"/>): binding and validating <c>mesh.json</c>
/// without starting the host is the only way to test a config change without deploying it. Guards that
/// a valid config binds cleanly and an invalid one throws naming the problem - the same
/// <see cref="MeshSourceRegistrar"/> rules <see cref="Startup"/> itself runs, so the two cannot
/// silently disagree.
/// </summary>
public class MeshConfigValidatorTest
{
    private static string WriteTempConfig(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mesh-config-validator-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void ValidConfig_BindsAndReturnsIt()
    {
        var path = WriteTempConfig("""
            {
              "services": [ { "name": "orders-api" } ],
              "usage": [ { "source": "cloudwatch" } ],
              "fleet": { "source": "xray" }
            }
            """);
        try
        {
            var config = MeshConfigValidator.Validate(path);

            Assert.Single(config.Services);
            Assert.Equal("cloudwatch", config.Usage[0].Source);
            Assert.Equal("xray", config.Fleet.Source);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ComposeSampleShape_Validates()
    {
        // examples/K8sMesh/compose/mesh.json's shape - the pre-existing, section-free config - must
        // still validate cleanly under the new schema.
        var path = WriteTempConfig("""
            {
              "artifactRootDirectory": "/data/mesh-artifacts",
              "pollIntervalSeconds": 15,
              "services": [
                { "name": "orders", "specUrl": "http://orders:8080/benzene/spec?type=benzene", "healthUrl": "http://orders:8080/benzene/health" }
              ]
            }
            """);
        try
        {
            var config = MeshConfigValidator.Validate(path);

            Assert.Equal("/data/mesh-artifacts", config.ArtifactRootDirectory);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnknownUsageSource_ThrowsNamingTheBadValue()
    {
        var path = WriteTempConfig("""
            {
              "usage": [ { "source": "cloudwtach" } ]
            }
            """);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => MeshConfigValidator.Validate(path));

            Assert.Equal("Unknown usage source 'cloudwtach'. Valid values: cloudwatch, applicationInsights.", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingRequiredOption_ThrowsNamingTheMissingKey()
    {
        var path = WriteTempConfig("""
            {
              "fleet": { "source": "tempo" }
            }
            """);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => MeshConfigValidator.Validate(path));

            Assert.Contains("'url'", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // WP-1(a) (#19): --validate-config runs the exact same MeshAuthGate.Validate rules Startup.Configure
    // does, including the dispatch.enabled x auth.mode satisfiability check - this catches the
    // misconfiguration before a deploy, not after one.
    [Fact]
    public void DispatchEnabledWithAuthModeNone_ThrowsNamingBothKeys()
    {
        var path = WriteTempConfig("""
            {
              "dispatch": { "enabled": true },
              "auth": { "mode": "none" }
            }
            """);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => MeshConfigValidator.Validate(path));

            Assert.Contains("dispatch.enabled", exception.Message);
            Assert.Contains("none", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MeshConfigPathSetToMissingFile_ThrowsNamingThePath()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json");

        var exception = Assert.Throws<FileNotFoundException>(() => MeshConfigValidator.Validate(missingPath));

        Assert.Contains(missingPath, exception.Message);
    }

    // --- #247/#248: fleet.options' new searchConcurrency/correlationSearchLimit keys, and dispatch's
    // new request/response/rate-limit bound keys, all bind and validate through this same entry point. --

    [Fact]
    public void FleetTempoWithSearchConcurrencyAndCorrelationSearchLimit_Validates()
    {
        var path = WriteTempConfig("""
            {
              "fleet": {
                "source": "tempo",
                "options": { "url": "http://tempo:3200", "searchConcurrency": "4", "correlationSearchLimit": "50" }
              }
            }
            """);
        try
        {
            var config = MeshConfigValidator.Validate(path);
            Assert.Equal("tempo", config.Fleet.Source);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FleetTempoSearchConcurrencyAboveCeiling_ThrowsNamingTheKey()
    {
        var path = WriteTempConfig("""
            {
              "fleet": { "source": "tempo", "options": { "url": "http://tempo:3200", "searchConcurrency": "10000" } }
            }
            """);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => MeshConfigValidator.Validate(path));
            Assert.Contains("searchConcurrency", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FleetTempoSearchConcurrencyZero_Validates()
    {
        // 0 is the documented "unbounded" value (TempoTraceSourceOptions.SearchConcurrency's remarks) -
        // never rejected, even though it's outside every OTHER bound's [min, max] shape.
        var path = WriteTempConfig("""
            {
              "fleet": { "source": "tempo", "options": { "url": "http://tempo:3200", "searchConcurrency": "0" } }
            }
            """);
        try
        {
            MeshConfigValidator.Validate(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FleetJaegerSearchConcurrencyAboveCeiling_ThrowsNamingTheKey()
    {
        var path = WriteTempConfig("""
            {
              "fleet": { "source": "jaeger", "options": { "url": "http://jaeger:16686", "searchConcurrency": "10000" } }
            }
            """);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => MeshConfigValidator.Validate(path));
            Assert.Contains("searchConcurrency", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DispatchMaxRequestBytesWithinBounds_Validates()
    {
        var path = WriteTempConfig("""
            {
              "dispatch": { "maxRequestBytes": 1000 }
            }
            """);
        try
        {
            var config = MeshConfigValidator.Validate(path);
            Assert.Equal(1000, config.Dispatch.MaxRequestBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DispatchMaxRequestBytesAboveTheKestrelCeiling_ThrowsNamingTheKey()
    {
        // #247/#248: the ceiling is Benzene.Mesh.Dispatch.MeshDispatchGuardOptions.DefaultMaxRequestBytes
        // (131,072) itself - Program.cs pins Kestrel's own MaxRequestBodySize to that same constant, so
        // a value above it would be silently unreachable, never a working larger cap.
        var path = WriteTempConfig("""
            {
              "dispatch": { "maxRequestBytes": 999999999 }
            }
            """);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => MeshConfigValidator.Validate(path));
            Assert.Contains("dispatch.maxRequestBytes", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DispatchMaxRequestBytesZero_Throws()
    {
        // Unlike the rate-limit fields, 0 has no "disable" meaning for a byte cap - a zero-byte cap
        // would reject every request outright, not turn the check off.
        var path = WriteTempConfig("""
            {
              "dispatch": { "maxRequestBytes": 0 }
            }
            """);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => MeshConfigValidator.Validate(path));
            Assert.Contains("dispatch.maxRequestBytes", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DispatchMaxPerMinutePerIdentityZero_Validates()
    {
        // 0 is the documented "disable the per-identity limit" value (MeshDispatchGuardOptions.
        // MaxPerMinutePerIdentity's own remarks) - never rejected.
        var path = WriteTempConfig("""
            {
              "dispatch": { "maxPerMinutePerIdentity": 0 }
            }
            """);
        try
        {
            var config = MeshConfigValidator.Validate(path);
            Assert.Equal(0, config.Dispatch.MaxPerMinutePerIdentity);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DispatchMaxPerMinutePerTargetNegative_ThrowsNamingTheKey()
    {
        var path = WriteTempConfig("""
            {
              "dispatch": { "maxPerMinutePerTarget": -1 }
            }
            """);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => MeshConfigValidator.Validate(path));
            Assert.Contains("dispatch.maxPerMinutePerTarget", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DispatchMaxResponseBytesWithinBounds_Validates()
    {
        var path = WriteTempConfig("""
            {
              "dispatch": { "maxResponseBytes": 500000 }
            }
            """);
        try
        {
            var config = MeshConfigValidator.Validate(path);
            Assert.Equal(500000, config.Dispatch.MaxResponseBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DispatchMaxResponseBytesAboveCeiling_ThrowsNamingTheKey()
    {
        var path = WriteTempConfig("""
            {
              "dispatch": { "maxResponseBytes": 999999999 }
            }
            """);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => MeshConfigValidator.Validate(path));
            Assert.Contains("dispatch.maxResponseBytes", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
