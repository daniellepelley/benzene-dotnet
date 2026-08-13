using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Benzene.AspNet.Core;
using Benzene.CloudService;
using Benzene.CodeGen.Cli.Core.Commands.CloudServiceProfile;
using Benzene.Test.Examples;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Cli;

// `benzene profile-check` wraps CloudServiceProbe.RunAsync over a real HTTP target and turns the
// report into a CI-friendly exit code: throws (-> non-zero, per Phase 2's fail-loud CLI) when the
// report trips --fail-on, otherwise returns normally (-> exit 0). Before this test existed the
// command never threw at all regardless of the probe's verdicts, so `profile-check` always
// silently exited 0 - unusable as a CI gate despite the CLI's own doc comment claiming otherwise.
public class CloudServiceProfileCheckCommandTest
{
    private static async Task<(Uri baseAddress, IAsyncDisposable app)> StartMostlyConformantServiceAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers();
        builder.Services.ConfigureServiceCollection();

        var app = builder.Build();
        app.UseRouting();
        app.UseBenzene(benzene => benzene
            .UseHttp(asp => asp
                .UseBenzeneCloudService("orders-api", cloud => cloud
                    .WithHandlers(typeof(ExampleMessageHandler))
                )
            )
        );
        app.UseEndpoints(_ => { });

        await app.StartAsync();
        return (new Uri(app.Urls.First()), app);
    }

    [Fact]
    public async Task ExecuteAsync_MissingUrl_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new CloudServiceProfileCheckCommand().ExecuteAsync(new CloudServiceProfileCheckPayload { FailOn = "none" }));
    }

    [Fact]
    public async Task ExecuteAsync_InvalidFailOn_Throws()
    {
        var (baseAddress, app) = await StartMostlyConformantServiceAsync();
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => new CloudServiceProfileCheckCommand().ExecuteAsync(
                new CloudServiceProfileCheckPayload { Url = baseAddress.ToString(), FailOn = "some-nonsense" }));
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    // R8 (and half of R6) are structurally unobservable by a single-service probe - a real
    // service, however conformant, always reports at least one Inconclusive verdict. That makes
    // this fixture's Inconclusive R8 the natural, always-true case to pin --fail-on's escalation
    // on, rather than needing a deliberately non-conformant fixture service.
    [Fact]
    public async Task ExecuteAsync_DefaultFailOnNotSatisfied_DoesNotThrowOnInconclusiveOnly()
    {
        var (baseAddress, app) = await StartMostlyConformantServiceAsync();
        try
        {
            await new CloudServiceProfileCheckCommand().ExecuteAsync(
                new CloudServiceProfileCheckPayload { Url = baseAddress.ToString() });
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecuteAsync_FailOnInconclusive_ThrowsOnR8()
    {
        var (baseAddress, app) = await StartMostlyConformantServiceAsync();
        try
        {
            var exception = await Assert.ThrowsAsync<CloudServiceProfileCheckFailedException>(() =>
                new CloudServiceProfileCheckCommand().ExecuteAsync(
                    new CloudServiceProfileCheckPayload { Url = baseAddress.ToString(), FailOn = "inconclusive" }));

            Assert.Contains("R8", exception.Report.Inconclusive);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecuteAsync_FailOnNone_NeverThrows()
    {
        var (baseAddress, app) = await StartMostlyConformantServiceAsync();
        try
        {
            await new CloudServiceProfileCheckCommand().ExecuteAsync(
                new CloudServiceProfileCheckPayload { Url = baseAddress.ToString(), FailOn = "none" });
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecuteAsync_NotSatisfied_ThrowsWithTheReportAttached()
    {
        // A bogus base address that refuses the connection: every requirement the probe can only
        // assess by reaching the service comes back NotSatisfied or Inconclusive, never Satisfied -
        // deterministic without needing a deliberately-misconfigured fixture service.
        var unreachable = new Uri("http://127.0.0.1:1");

        var exception = await Assert.ThrowsAsync<CloudServiceProfileCheckFailedException>(() =>
            new CloudServiceProfileCheckCommand().ExecuteAsync(
                new CloudServiceProfileCheckPayload { Url = unreachable.ToString() }));

        Assert.True(exception.Report.NotSatisfied.Count > 0);
    }
}
