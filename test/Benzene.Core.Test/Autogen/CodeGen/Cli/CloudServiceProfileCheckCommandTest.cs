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
    private const string RelocatedInvokePath = "/custom/invoke";

    private static async Task<(Uri baseAddress, IAsyncDisposable app)> StartMostlyConformantServiceAsync(bool relocateInvoke = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers();
        builder.Services.ConfigureServiceCollection();

        var app = builder.Build();
        app.UseRouting();
        app.UseBenzene(benzene => benzene
            .UseHttp(asp => asp
                .UseBenzeneCloudService("orders-api", cloud =>
                {
                    cloud.WithHandlers(typeof(ExampleMessageHandler));
                    if (relocateInvoke)
                    {
                        cloud.WithInvokePath(RelocatedInvokePath);
                    }
                })
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

    // The profile (R1-R5, R7) is fully observable over HTTP, so a conformant service probed at its
    // default paths trips nothing - not even --fail-on inconclusive. The meshed-service claim's M1/M2
    // stay Inconclusive (never observable from one service) but are reported apart and never count.
    [Fact]
    public async Task ExecuteAsync_ConformantService_PassesEvenOnFailOnInconclusive()
    {
        var (baseAddress, app) = await StartMostlyConformantServiceAsync();
        try
        {
            await new CloudServiceProfileCheckCommand().ExecuteAsync(
                new CloudServiceProfileCheckPayload { Url = baseAddress.ToString() });
            await new CloudServiceProfileCheckCommand().ExecuteAsync(
                new CloudServiceProfileCheckPayload { Url = baseAddress.ToString(), FailOn = "inconclusive" });
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    // A probe pointed at a non-default path can't tell what the service's own defaults are, so R7 is
    // Inconclusive - the natural case to pin --fail-on's escalation on: the default threshold passes,
    // --fail-on inconclusive fails.
    [Fact]
    public async Task ExecuteAsync_DefaultFailOnNotSatisfied_DoesNotThrowOnInconclusiveOnly()
    {
        var (baseAddress, app) = await StartMostlyConformantServiceAsync(relocateInvoke: true);
        try
        {
            await new CloudServiceProfileCheckCommand().ExecuteAsync(
                new CloudServiceProfileCheckPayload { Url = baseAddress.ToString(), InvokePath = RelocatedInvokePath });
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecuteAsync_FailOnInconclusive_ThrowsOnR7()
    {
        var (baseAddress, app) = await StartMostlyConformantServiceAsync(relocateInvoke: true);
        try
        {
            var exception = await Assert.ThrowsAsync<CloudServiceProfileCheckFailedException>(() =>
                new CloudServiceProfileCheckCommand().ExecuteAsync(
                    new CloudServiceProfileCheckPayload { Url = baseAddress.ToString(), InvokePath = RelocatedInvokePath, FailOn = "inconclusive" }));

            Assert.Equal(new[] { "R7" }, exception.Report.Inconclusive);
            Assert.DoesNotContain("M1", exception.Report.Inconclusive);
            Assert.DoesNotContain("M2", exception.Report.Inconclusive);
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
