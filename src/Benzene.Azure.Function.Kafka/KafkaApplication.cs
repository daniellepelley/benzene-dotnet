using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Azure.Function.Core;
using Benzene.Core.Middleware;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Benzene.Azure.Function.Kafka;

/// <summary>
/// The entry point application for a Kafka-triggered Azure Function. Maps each event in the triggered
/// batch to a <see cref="KafkaContext"/> and runs them all through the middleware pipeline, tagging the
/// transport as <c>"kafka"</c> for the duration.
/// </summary>
public class KafkaApplication : EntryPointMiddlewareApplication<KafkaRecord[]>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="KafkaApplication"/> class.
    /// </summary>
    /// <param name="pipeline">The built Kafka middleware pipeline to run each event through.</param>
    /// <param name="serviceResolverFactory">The service resolver factory used to process each batch.</param>
    /// <param name="options">
    /// Configures how a handler's exceptions and failure results are handled. Defaults to a new
    /// <see cref="KafkaOptions"/> instance (safe-by-default:
    /// <see cref="KafkaOptions.RaiseOnFailureStatus"/> on, <see cref="KafkaOptions.CatchExceptions"/>
    /// off) if omitted.
    /// </param>
    public KafkaApplication(IMiddlewarePipeline<KafkaContext> pipeline, IServiceResolverFactory serviceResolverFactory, KafkaOptions? options = null)
        : base(new KafkaBatchApplication(pipeline, options), serviceResolverFactory)
    { }
}

/// <summary>
/// Runs every record in a Kafka trigger batch through the middleware pipeline concurrently, each in
/// its own service scope, applying <see cref="KafkaOptions"/> to decide whether a record's exception
/// or failure result is contained (logged, doesn't affect the rest of the batch) or left to cascade
/// and fail the whole invocation. The fan-out/settle/escalate/log skeleton itself lives in
/// <see cref="AzureFunctionBatchApplicationBase{TContext, TState}"/>; this class plugs in the
/// Kafka-specific bits - Kafka uses no extra per-item state, so <c>TState</c> is <c>object?</c>.
/// </summary>
public class KafkaBatchApplication : AzureFunctionBatchApplicationBase<KafkaContext, object?>, IMiddlewareApplication<KafkaRecord[]>
{
    public KafkaBatchApplication(IMiddlewarePipeline<KafkaContext> pipeline, KafkaOptions? options = null)
        : base(pipeline, TransportNames.Kafka, (options ??= new KafkaOptions()).CatchExceptions, options.RaiseOnFailureStatus, options.MaxDegreeOfParallelism)
    { }

    public Task HandleAsync(KafkaRecord[] @event, IServiceResolverFactory serviceResolverFactory)
        => HandleAsync(@event, serviceResolverFactory, CancellationToken.None);

    /// <summary>
    /// Runs every record in the batch through the pipeline, additionally seeding <b>each</b> record's
    /// own scope with the ambient cancellation token so any component resolved during that record's
    /// pipeline run can observe cancellation via <see cref="ICancellationTokenAccessor"/>.
    /// </summary>
    public Task HandleAsync(KafkaRecord[] @event, IServiceResolverFactory serviceResolverFactory, CancellationToken cancellationToken)
        // BoundedFanOut (via the base class) optionally caps how many records run at once
        // (KafkaOptions.MaxDegreeOfParallelism); unset leaves the fan-out unbounded, exactly as before.
        => HandleBatchAsync(@event.Select(item => (new KafkaContext(item), (object?)null)), serviceResolverFactory, cancellationToken);

    /// <inheritdoc/>
    protected override Exception CreateProcessingException(KafkaContext context)
        => new KafkaMessageProcessingException(context.KafkaEvent.Topic);

    /// <inheritdoc/>
    protected override object GetLogId(KafkaContext context) => context.KafkaEvent.Topic;

    /// <inheritdoc/>
    protected override string FailureLogMessageTemplate => "Processing Kafka record on topic {topic} failed";

    /// <inheritdoc/>
    protected override ILogger GetLogger(IServiceResolver serviceResolver)
        => serviceResolver.GetService<ILogger<KafkaApplication>>();
}
