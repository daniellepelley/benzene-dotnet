using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Benzene.Abstractions.Hosting;
using Benzene.Aws.Lambda.Core;
using Benzene.Aws.Lambda.Hosting;
using Benzene.Aws.Lambda.Sqs;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Examples;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Aws.Hosting;

/// <summary>
/// <see cref="AwsLambdaBootstrap"/> collapses the custom-runtime <c>HandlerWrapper</c> +
/// <c>LambdaBootstrap</c> boilerplate to one line. These drive the loop with a pre-cancelled token so
/// it returns before contacting the (absent) Lambda runtime API, which lets the disposal contract be
/// asserted in a plain unit test.
/// </summary>
public class AwsLambdaBootstrapTest
{
    private static CancellationToken Cancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        return cts.Token;
    }

    private static async Task Within(Task task)
    {
        // A cancelled token guarantees a prompt exit; guard the test from hanging if that ever regresses.
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(task, completed);
        await task;
    }

    [Fact]
    public async Task RunAsync_WithSuppliedEntryPoint_DoesNotDisposeIt()
    {
        // The caller owns an entry point it passes in — running the loop must not dispose it out from
        // under them.
        var entryPoint = new RecordingEntryPoint();

        await Within(AwsLambdaBootstrap.RunAsync(entryPoint, Cancelled()));

        Assert.False(entryPoint.WasDisposed);
    }

    [Fact]
    public async Task RunAsync_WithBuilder_DisposesTheEntryPointItBuilt()
    {
        // The overloads that build the entry point own its disposal.
        var entryPoint = new RecordingEntryPoint();

        await Within(AwsLambdaBootstrap.RunAsync(new StubBuilder(entryPoint), Cancelled()));

        Assert.True(entryPoint.WasDisposed);
    }

    [Fact]
    public async Task RunAsync_OfStartUp_HostsAndRunsToCompletion()
    {
        // The one-liner path: construct an AwsLambdaHost<TStartUp> (WarmUp + start-up checks run in its
        // ctor), run the loop, dispose the host — all without throwing.
        await Within(AwsLambdaBootstrap.RunAsync<BootstrapTestStartUp>(Cancelled()));
    }

    private sealed class RecordingEntryPoint : IAwsLambdaEntryPoint
    {
        public bool WasDisposed { get; private set; }

        public Task<Stream> FunctionHandlerAsync(Stream stream, ILambdaContext lambdaContext)
            => throw new InvalidOperationException("The handler must not be invoked when the loop is cancelled before it starts.");

        public void Dispose() => WasDisposed = true;
    }

    private sealed class StubBuilder : IAwsEntryPointBuilder
    {
        private readonly IAwsLambdaEntryPoint _entryPoint;
        public StubBuilder(IAwsLambdaEntryPoint entryPoint) => _entryPoint = entryPoint;
        public IAwsLambdaEntryPoint Build() => _entryPoint;
    }

    private sealed class BootstrapTestStartUp : BenzeneStartUp
    {
        public override IConfiguration GetConfiguration() => new ConfigurationBuilder().Build();

        public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
            => services.UsingBenzene(x => x
                .AddBenzene()
                .AddMessageHandlers(typeof(Defaults).Assembly)
                .AddSqs());

        public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
            => app.UseAwsLambda(aws => aws.UseSqs(sqs => sqs.UseMessageHandlers()));
    }
}
