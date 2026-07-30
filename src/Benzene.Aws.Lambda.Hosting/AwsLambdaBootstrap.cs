using System;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda.RuntimeSupport;
using Benzene.Aws.Lambda.Core;
using Benzene.Microsoft.Dependencies;

namespace Benzene.Aws.Lambda.Hosting;

/// <summary>
/// Runs a Benzene AWS Lambda entry point in the custom-runtime bootstrap loop, so a pure-Benzene
/// function's <c>Main</c> is one line.
/// </summary>
/// <remarks>
/// <para>
/// .NET has no managed Lambda runtime from .NET 8 onward, so a Benzene function ships as a
/// self-contained executable on <c>provided.al2023</c> and pumps invocations itself with
/// <see cref="LambdaBootstrap"/>. That is the same three lines in every function:
/// </para>
/// <code>
/// using var handlerWrapper = HandlerWrapper.GetHandlerWrapper(function.FunctionHandlerAsync);
/// using var bootstrap = new LambdaBootstrap(handlerWrapper);
/// await bootstrap.RunAsync();
/// </code>
/// <para>
/// This collapses them to <c>await AwsLambdaBootstrap.RunAsync&lt;StartUp&gt;();</c>. It is the
/// ASP.NET-free host — for a function that also serves HTTP through ASP.NET Core, see
/// <c>Benzene.Aws.Lambda.AspNet</c>, whose <c>AddBenzeneAwsLambdaHosting</c> drives the same loop from
/// inside <c>app.Run()</c>.
/// </para>
/// </remarks>
public static class AwsLambdaBootstrap
{
    /// <summary>
    /// Hosts a platform-neutral <see cref="BenzeneStartUp"/> as a Lambda function and runs the
    /// bootstrap loop until the execution environment shuts down.
    /// </summary>
    /// <typeparam name="TStartUp">The Benzene start-up to host.</typeparam>
    /// <param name="cancellationToken">Signals the bootstrap loop to stop pumping invocations.</param>
    /// <returns>A task that completes when the loop stops.</returns>
    /// <example>
    /// <code>
    /// // Program.cs
    /// await AwsLambdaBootstrap.RunAsync&lt;StartUp&gt;();
    /// </code>
    /// </example>
    public static Task RunAsync<TStartUp>(CancellationToken cancellationToken = default)
        where TStartUp : BenzeneStartUp, new()
        => RunAndDisposeAsync(new AwsLambdaHost<TStartUp>(), cancellationToken);

    /// <summary>
    /// Builds an entry point from an <see cref="IAwsEntryPointBuilder"/> and runs the bootstrap loop
    /// until the execution environment shuts down.
    /// </summary>
    /// <param name="builder">The builder that produces the entry point to host.</param>
    /// <param name="cancellationToken">Signals the bootstrap loop to stop pumping invocations.</param>
    /// <returns>A task that completes when the loop stops.</returns>
    public static Task RunAsync(IAwsEntryPointBuilder builder, CancellationToken cancellationToken = default)
        => RunAndDisposeAsync(builder.Build(), cancellationToken);

    /// <summary>
    /// Runs an already-built <see cref="IAwsLambdaEntryPoint"/> in the bootstrap loop until the
    /// execution environment shuts down. The caller owns the entry point's lifetime.
    /// </summary>
    /// <param name="entryPoint">The entry point to host. Not disposed by this overload.</param>
    /// <param name="cancellationToken">Signals the bootstrap loop to stop pumping invocations.</param>
    /// <returns>A task that completes when the loop stops.</returns>
    /// <remarks>
    /// Use this overload to host a custom <see cref="AwsLambdaHost{TStartUp}"/> subclass (for example
    /// one overriding <c>OnInvocationCompleteAsync</c> to flush telemetry): <c>await
    /// AwsLambdaBootstrap.RunAsync(new Function());</c>.
    /// </remarks>
    public static async Task RunAsync(IAwsLambdaEntryPoint entryPoint, CancellationToken cancellationToken = default)
    {
        // The entry point already deals in Stream in/Stream out, so no serializer is needed here — the
        // pipeline's bindings deserialize each event to its own type.
        using var handlerWrapper = HandlerWrapper.GetHandlerWrapper(entryPoint.FunctionHandlerAsync);
        using var bootstrap = new LambdaBootstrap(handlerWrapper);
        await bootstrap.RunAsync(cancellationToken);
    }

    // The overloads that build the entry point own its disposal; the caller-supplied overload does not.
    private static async Task RunAndDisposeAsync(IAwsLambdaEntryPoint entryPoint, CancellationToken cancellationToken)
    {
        using (entryPoint)
        {
            await RunAsync(entryPoint, cancellationToken);
        }
    }
}
