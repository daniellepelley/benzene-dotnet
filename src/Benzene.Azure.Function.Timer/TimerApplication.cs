using System;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Core;
using Benzene.Core.MessageHandlers.Info;
using Benzene.Core.Middleware;
using Microsoft.Extensions.Logging;

namespace Benzene.Azure.Function.Timer;

/// <summary>
/// The entry point application for a timer-triggered Azure Function. Maps the tick to a
/// <see cref="TimerContext"/> and runs it through the middleware pipeline, tagging the transport as
/// <c>"timer"</c> for the duration. Exception/failure-status behavior is configurable via
/// <see cref="TimerOptions"/>, mirroring every sibling Azure Function trigger package.
/// </summary>
public class TimerApplication : EntryPointMiddlewareApplication<TimerTriggerInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TimerApplication"/> class.
    /// </summary>
    /// <param name="pipeline">The built timer middleware pipeline to run each tick through.</param>
    /// <param name="serviceResolverFactory">The service resolver factory used to process each invocation.</param>
    /// <param name="options">
    /// Configures how the tick's exceptions and failure results are handled. Defaults to a new
    /// <see cref="TimerOptions"/> instance (safe-by-default: <see cref="TimerOptions.RaiseOnFailureStatus"/>
    /// on, <see cref="TimerOptions.CatchExceptions"/> off) if omitted.
    /// </param>
    public TimerApplication(IMiddlewarePipeline<TimerContext> pipeline, IServiceResolverFactory serviceResolverFactory, TimerOptions? options = null)
        : base(new TimerTickApplication(pipeline, options), serviceResolverFactory)
    { }
}

/// <summary>
/// Runs a single timer tick through the middleware pipeline, applying <see cref="TimerOptions"/> to
/// decide whether the tick's exception or failure result is contained (logged) or left to cascade and
/// fail the invocation - the same CatchExceptions/RaiseOnFailureStatus contract every other Azure
/// Function trigger package exposes via its own <c>*Options</c> type
/// (see <see cref="Benzene.Azure.Function.Core.AzureFunctionBatchApplicationBase{TContext, TState}"/>),
/// applied here to a single tick rather than a batch.
/// </summary>
public class TimerTickApplication : IMiddlewareApplication<TimerTriggerInfo>
{
    private readonly IMiddlewarePipeline<TimerContext> _pipeline;
    private readonly TimerOptions _options;

    /// <summary>Initializes a new instance of the <see cref="TimerTickApplication"/> class.</summary>
    /// <param name="pipeline">The built timer middleware pipeline to run each tick through.</param>
    /// <param name="options">
    /// Configures how the tick's exceptions and failure results are handled. Defaults to a new
    /// <see cref="TimerOptions"/> instance if omitted.
    /// </param>
    public TimerTickApplication(IMiddlewarePipeline<TimerContext> pipeline, TimerOptions? options = null)
    {
        _pipeline = new TransportMiddlewarePipeline<TimerContext>(TransportNames.Timer, pipeline);
        _options = options ?? new TimerOptions();
    }

    /// <inheritdoc/>
    public Task HandleAsync(TimerTriggerInfo @event, IServiceResolverFactory serviceResolverFactory)
        => HandleAsync(@event, serviceResolverFactory, CancellationToken.None);

    /// <summary>
    /// Runs the tick through the pipeline in its own service scope, seeded with
    /// <paramref name="cancellationToken"/>. After the pipeline completes, if
    /// <see cref="TimerOptions.RaiseOnFailureStatus"/> is set and the tick's
    /// <see cref="TimerContext.MessageResult"/> was explicitly recorded as unsuccessful
    /// (<c>IsSuccessful == false</c>), throws a <see cref="TimerMessageProcessingException"/> so the
    /// Functions host records a failed invocation instead of silently succeeding. An exception
    /// (including that escalation throw) is caught and logged instead of cascading when
    /// <see cref="TimerOptions.CatchExceptions"/> is set.
    /// </summary>
    /// <remarks>
    /// Deliberately <c>== false</c>, not the <c>!= true</c> convention every message-routed batch
    /// trigger uses (WP-B, round 15): those transports run every item through <c>MessageRouter</c>,
    /// which unconditionally records a result, so an unset (<c>null</c>) result only ever means "the
    /// router never got to run" - itself worth escalating. A timer tick has no
    /// such guarantee: <c>UseTick(...)</c> (this package's primary, documented consumption mode) never
    /// touches <see cref="TimerContext.MessageResult"/> at all, so treating <c>null</c> as a failure
    /// would escalate <em>every</em> plain tick by default. Only an explicit <c>IsSuccessful == false</c>
    /// - meaning a message handler actually ran (via <c>UsePresetTopic(...).UseMessageHandlers()</c>)
    /// and reported failure - is escalated; a tick that never routes to a handler is unaffected.
    /// </remarks>
    /// <param name="event">The tick's timer information.</param>
    /// <param name="serviceResolverFactory">The service resolver factory used to create a scope for the tick.</param>
    /// <param name="cancellationToken">
    /// The isolated worker's cancellation token for this invocation, or <see cref="CancellationToken.None"/>
    /// if it has no signal.
    /// </param>
    public async Task HandleAsync(TimerTriggerInfo @event, IServiceResolverFactory serviceResolverFactory, CancellationToken cancellationToken)
    {
        var context = new TimerContext(@event);

        try
        {
            using (var scope = serviceResolverFactory.CreateScope())
            {
                scope.SeedCancellationToken(cancellationToken);
                await _pipeline.HandleAsync(context, scope);
            }

            if (_options.RaiseOnFailureStatus && context.MessageResult?.IsSuccessful == false)
            {
                throw new TimerMessageProcessingException(context.Timer.ScheduleStatus?.Next);
            }
        }
        catch (Exception ex) when (_options.CatchExceptions)
        {
            using var loggingScope = serviceResolverFactory.CreateScope();
            var logger = loggingScope.GetService<ILogger<TimerApplication>>();
            logger.LogError(ex, BenzeneFailure.IsInfrastructure(ex)
                ? BenzeneFailure.InfrastructureLogPrefix + " Processing timer tick scheduled for {scheduledFor} failed — this service is mis-wired; the tick is not at fault"
                : "Processing timer tick scheduled for {scheduledFor} failed", context.Timer.ScheduleStatus?.Next);
        }
    }
}
