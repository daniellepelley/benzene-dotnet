using System;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda.AspNetCoreServer.Internal;
using Benzene.Aws.Lambda.Core;
using Benzene.Aws.Lambda.Hosting;
using Microsoft.AspNetCore.Hosting.Server;

namespace Benzene.Aws.Lambda.AspNet;

/// <summary>
/// The ASP.NET Core <c>IServer</c> that lets Benzene, not ASP.NET, own the Lambda runtime loop while
/// ASP.NET still handles HTTP.
/// </summary>
/// <remarks>
/// <para>
/// This is a native re-creation of <c>Amazon.Lambda.AspNetCoreServer.Hosting</c>'s internal
/// <c>LambdaRuntimeSupportServer</c> — the ~30 lines that make <c>AddAWSLambdaHosting</c> + <c>app.Run()</c>
/// work — but with the handler swapped. AWS's version pumps an HTTP-only handler; this one pumps the
/// Benzene entry point, so SQS, SNS, EventBridge and the rest are dispatched by Benzene and HTTP is
/// claimed by the bridge in the same pipeline. Re-creating it here (rather than referencing that
/// package) is what keeps <c>Main</c> as <c>app.Run()</c> instead of <c>AddAWSLambdaHosting</c>.
/// </para>
/// <para>
/// <see cref="StartAsync{TContext}"/> first lets the base <see cref="LambdaServer"/> capture the ASP.NET
/// <see cref="IHttpApplication{TContext}"/> — the pipeline the bridge dispatches HTTP into — then hands
/// the built Benzene entry point to the bootstrap loop, which runs until the execution environment
/// shuts down.
/// </para>
/// </remarks>
internal sealed class BenzeneLambdaServer : LambdaServer
{
    private readonly IServiceProvider _serviceProvider;
    private readonly Func<IServiceProvider, IAwsLambdaEntryPoint> _buildEntryPoint;

    /// <summary>
    /// Initializes a new instance of the <see cref="BenzeneLambdaServer"/> class.
    /// </summary>
    /// <param name="serviceProvider">The application's service provider, shared with ASP.NET and Benzene.</param>
    /// <param name="buildEntryPoint">
    /// Builds the Benzene entry point from the provider, deferred to <see cref="StartAsync{TContext}"/> so
    /// the event pipeline is built against the fully-composed container.
    /// </param>
    public BenzeneLambdaServer(
        IServiceProvider serviceProvider,
        Func<IServiceProvider, IAwsLambdaEntryPoint> buildEntryPoint)
    {
        _serviceProvider = serviceProvider;
        _buildEntryPoint = buildEntryPoint;
    }

    /// <inheritdoc />
    public override Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
    {
        // LambdaServer captures the IHttpApplication here — the ASP.NET pipeline the bridge dispatches
        // into. It opens no socket; it just records the application. This is synchronous internally, so
        // it is safe not to await before starting the loop (matching AWS's own runtime-support server).
        base.StartAsync(application, cancellationToken);

        // Now the container is built, so the event pipeline and its per-invocation resolver can be built.
        var entryPoint = _buildEntryPoint(_serviceProvider);

        // Runs until the execution environment shuts down; because GenericWebHostService awaits this,
        // app.Run() blocks here pumping invocations — exactly as AddAWSLambdaHosting does.
        return AwsLambdaBootstrap.RunAsync(entryPoint, cancellationToken);
    }
}
