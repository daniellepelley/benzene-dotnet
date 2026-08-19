using System.Collections.Generic;
using System;
using Benzene.Abstractions.Hosting;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Hosting;

/// <summary>
/// <see cref="BenzeneStartUp.GetConfiguration"/>'s default. It became virtual because 23 of the 50
/// StartUps in this repo wrote this exact body by hand, and a new service could not compile until it
/// had - a line you pay for the default rather than for a steer away from it.
/// </summary>
public class BenzeneStartUpConfigurationTest
{
    private class DefaultStartUp : BenzeneStartUp
    {
        public override void ConfigureServices(IServiceCollection services, IConfiguration configuration) { }
        public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration) { }
    }

    private class OverridingStartUp : BenzeneStartUp
    {
        public override IConfiguration GetConfiguration() =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["SOURCE"] = "the override" })
                .Build();

        public override void ConfigureServices(IServiceCollection services, IConfiguration configuration) { }
        public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration) { }
    }

    [Fact]
    public void AStartUpThatSaysNothing_ReadsEnvironmentVariables()
    {
        // What a container, a Lambda, a Function and a Cloud Run revision all actually inject.
        var name = "BENZENE_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "from the environment");
        try
        {
            var configuration = new DefaultStartUp().GetConfiguration();

            Assert.Equal("from the environment", configuration[name]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void AStartUpThatOverridesIt_GetsItsOwnSourceAndNotTheDefault()
    {
        // The other half of the contract: making the default free must not make the steer harder.
        // 27 StartUps here still override this - appsettings.json, a base path, a shared builder.
        var name = "BENZENE_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "from the environment");
        try
        {
            var configuration = new OverridingStartUp().GetConfiguration();

            Assert.Equal("the override", configuration["SOURCE"]);
            Assert.Null(configuration[name]); // the default source is replaced, not merged into
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void TheDefault_IsAFreshConfigurationEachCall_NotASharedStaticOne()
    {
        // A host may call this more than once (a test host layering overrides on top, for one).
        // Handing back one shared instance would let those calls contaminate each other.
        var startUp = new DefaultStartUp();

        Assert.NotSame(startUp.GetConfiguration(), startUp.GetConfiguration());
    }
}
