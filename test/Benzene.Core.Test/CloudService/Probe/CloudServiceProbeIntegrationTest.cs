using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Benzene.AspNet.Core;
using Benzene.CloudService;
using Benzene.CloudService.Probe;
using Benzene.Test.Examples;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.CloudService.Probe;

/// <summary>
/// Stands up a REAL Kestrel-hosted ASP.NET Core app with <c>UseBenzeneCloudService</c> actually
/// wired, and probes it with <see cref="CloudServiceProbe"/> over a real socket - the test that
/// proves the two halves of the profile story (the wiring-time self-check in
/// <c>Benzene.CloudService</c> and this external probe) agree when pointed at the same real
/// service.
/// </summary>
public class CloudServiceProbeIntegrationTest
{
    [Fact]
    public async Task ProbingARealCloudService_ReportsTheProfileFullySatisfied_AndTheMeshClaimApart()
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
        try
        {
            var baseAddress = new Uri(app.Urls.First());
            using var client = new HttpClient { BaseAddress = baseAddress };

            var report = await CloudServiceProbe.RunAsync(client);

            AssertVerdict(report, "R1", CloudServiceProbeVerdict.Satisfied);
            AssertVerdict(report, "R2", CloudServiceProbeVerdict.Satisfied);
            AssertVerdict(report, "R3", CloudServiceProbeVerdict.Satisfied);
            AssertVerdict(report, "R4", CloudServiceProbeVerdict.Satisfied);
            AssertVerdict(report, "R5", CloudServiceProbeVerdict.Satisfied);
            AssertVerdict(report, "R7", CloudServiceProbeVerdict.Satisfied);
            // The profile (R1–R5, R7) needs no collector and is fully observable over HTTP.
            Assert.True(report.IsFullyConformant);

            // The meshed-service claim (M1 feeds, M2 propagation) is reported apart - and is not
            // observable from a single service no matter how conformant it is.
            Assert.Equal(new[] { "M1", "M2" }, report.MeshRequirements.Select(x => x.Id));
            Assert.All(report.MeshRequirements, x => Assert.Equal(CloudServiceProbeVerdict.Inconclusive, x.Verdict));
            var m1 = report.MeshRequirements.Single(x => x.Id == "M1");
            Assert.Contains("registration", m1.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static void AssertVerdict(CloudServiceProbeReport report, string id, CloudServiceProbeVerdict expected)
    {
        var requirement = report.Requirements.SingleOrDefault(x => x.Id == id);
        Assert.NotNull(requirement);
        Assert.True(expected == requirement!.Verdict,
            $"{id}: expected {expected} but was {requirement.Verdict} ({requirement.Reason})");
    }
}
