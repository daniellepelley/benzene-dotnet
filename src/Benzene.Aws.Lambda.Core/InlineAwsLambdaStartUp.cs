using System;
using Benzene.Abstractions.Middleware;
using Benzene.Aws.Lambda.Core.AwsEventStream;
using Benzene.Core.MessageHandlers.StartUpChecks;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Aws.Lambda.Core;

/// <summary>
/// Provides a fluent, inline alternative to declaring an <see cref="AwsLambdaStartUp"/> subclass —
/// primarily intended for tests and small samples.
/// </summary>
/// <remarks>
/// Uses Microsoft's built-in dependency injection container. For production Lambda functions, prefer
/// deriving from <see cref="AwsLambdaStartUp"/> directly, since it can serve as the Lambda entry point
/// without needing a separate builder step.
/// </remarks>
public class InlineAwsLambdaStartUp : IAwsEntryPointBuilder
{
    private Action<IServiceCollection> _servicesAction = _ => { };
    private Action<IMiddlewarePipelineBuilder<AwsEventStreamContext>> _appAction = _ => { };

    /// <summary>
    /// Configures the action used to register services with the service collection.
    /// </summary>
    /// <param name="action">The action that registers services.</param>
    /// <returns>This instance for method chaining.</returns>
    public InlineAwsLambdaStartUp ConfigureServices(Action<IServiceCollection> action)
    {
        _servicesAction = action;
        return this;
    }

    /// <summary>
    /// Configures the action used to build the middleware pipeline.
    /// </summary>
    /// <param name="action">The action that configures the pipeline builder.</param>
    /// <returns>This instance for method chaining.</returns>
    public InlineAwsLambdaStartUp Configure(Action<IMiddlewarePipelineBuilder<AwsEventStreamContext>> action)
    {
        _appAction = action;
        return this;
    }

    /// <summary>
    /// Builds the Lambda entry point from the configured actions.
    /// </summary>
    /// <returns>The built <see cref="IAwsLambdaEntryPoint"/>, ready to handle invocations.</returns>
    /// <remarks>
    /// Deliberately runs <see cref="Benzene.Core.MessageHandlers.StartUpChecks.BenzeneStartUpCheckExtensions.RunStartUpChecks"/>
    /// (a wiring bug is exactly what a test host should catch) but not <c>WarmUp()</c> — warm-up
    /// exists to pay startup costs during Lambda's INIT phase before the first real invocation, which
    /// has no equivalent in an inline test host that's about to invoke immediately.
    /// </remarks>
    public IAwsLambdaEntryPoint Build()
    {
        var services = new ServiceCollection();
        var app = new MiddlewarePipelineBuilder<AwsEventStreamContext>(new MicrosoftBenzeneServiceContainer(services));

        // Order matches AwsLambdaHost's production order (ConfigureServices, then Configure): both
        // actions register services via TryAdd*, so whichever runs first wins a given service type.
        // Running Configure first here would let a transport's own TryAdd* default (e.g. UseSqs's
        // AddSqs) claim a registration before ConfigureServices got a chance to install its own
        // override, silently losing overrides that work fine under the production host (#106).
        _servicesAction(services);
        _appAction(app);

        var serviceResolverFactory = new MicrosoftServiceResolverFactory(services);
        var entryPoint = new AwsLambdaEntryPoint(app.Build(), serviceResolverFactory);

        // The same start-up checks AwsLambdaHost runs for a real deployment. Without this the
        // in-repo test host was the one place a wiring bug could not be caught, which is exactly
        // backwards — a test is the cheapest place to find one.
        serviceResolverFactory.RunStartUpChecks();

        return entryPoint;
    }
}
