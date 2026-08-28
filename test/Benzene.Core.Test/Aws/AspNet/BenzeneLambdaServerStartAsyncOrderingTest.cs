using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Benzene.Aws.Lambda.AspNet;
using Benzene.Aws.Lambda.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Aws.AspNet;

/// <summary>
/// Pins <c>BenzeneLambdaServer</c>'s own documented ordering guarantee: <c>StartAsync</c> lets the
/// base <c>LambdaServer</c> capture the ASP.NET <c>IHttpApplication</c> - the pipeline the bridges
/// dispatch HTTP into - BEFORE the Benzene entry point is built and the bootstrap loop starts. Round
/// 15's zero-coverage finding named this exact guarantee as untested. <c>BenzeneLambdaServer</c> is
/// internal (never a public type - only ever reached through <c>IServer</c>), so this test relies on
/// <c>InternalsVisibleTo("Benzene.Test")</c> on the package to construct it directly.
/// </summary>
public class BenzeneLambdaServerStartAsyncOrderingTest
{
    private sealed class FakeHttpApplication : IHttpApplication<object>
    {
        public object CreateContext(IFeatureCollection contextFeatures) => new();

        public void DisposeContext(object context, Exception exception)
        {
        }

        public Task ProcessRequestAsync(object context) => Task.CompletedTask;
    }

    private sealed class FakeEntryPoint : IAwsLambdaEntryPoint
    {
        public Task<Stream> FunctionHandlerAsync(Stream stream, ILambdaContext lambdaContext)
            => Task.FromResult<Stream>(new MemoryStream());

        public void Dispose()
        {
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task StartAsync_CapturesTheAspNetApplication_BeforeBuildingTheBenzeneEntryPoint()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var order = new List<string>();

        BenzeneLambdaServer server = null;
        server = new BenzeneLambdaServer(provider, sp =>
        {
            order.Add("build-entry-point");

            // The ordering guarantee itself: by the time the entry point is being built, StartAsync's
            // call into the base LambdaServer has already captured the ASP.NET application - so a
            // request arriving via the bridges (which read the same captured Application) would never
            // observe a construction-in-progress server.
            Assert.NotNull(server.Application);

            Assert.Same(provider, sp);
            return new FakeEntryPoint();
        });

        Assert.Null(server.Application);

        // Pre-cancelled: AwsLambdaBootstrap.RunAsync's loop checks the token before it ever calls out
        // to the Lambda Runtime API, so StartAsync returns promptly with no network activity and no
        // running Lambda environment required - this test only needs to observe the ordering, not pump
        // a real invocation.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await server.StartAsync(new FakeHttpApplication(), cts.Token);

        Assert.NotNull(server.Application);
        Assert.Equal(new[] { "build-entry-point" }, order);
    }
}
