using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Azure.Function.Core;
using Benzene.Core.Middleware;
using Microsoft.Extensions.Logging;

namespace Benzene.Azure.Function.QueueStorage;

/// <summary>
/// The entry point application for a Queue Storage-triggered Azure Function. Maps each message to a
/// <see cref="QueueStorageContext"/> and runs it through the middleware pipeline, tagging the
/// transport as <c>"queue-storage"</c> for the duration. Exception/failure-status behavior is
/// configurable via <see cref="QueueStorageOptions"/>, mirroring <c>Benzene.Azure.Function.Kafka</c>.
/// </summary>
public class QueueStorageApplication : EntryPointMiddlewareApplication<QueueStorageMessage[]>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="QueueStorageApplication"/> class.
    /// </summary>
    /// <param name="pipeline">The built Queue Storage middleware pipeline to run each message through.</param>
    /// <param name="serviceResolverFactory">The service resolver factory used to process each invocation.</param>
    /// <param name="options">
    /// Configures how a handler's exceptions and failure results are handled, and the batch fan-out
    /// concurrency. Defaults to a new <see cref="QueueStorageOptions"/> instance (safe-by-default:
    /// <see cref="QueueStorageOptions.RaiseOnFailureStatus"/> on,
    /// <see cref="QueueStorageOptions.CatchExceptions"/> off) if omitted.
    /// </param>
    public QueueStorageApplication(IMiddlewarePipeline<QueueStorageContext> pipeline, IServiceResolverFactory serviceResolverFactory, QueueStorageOptions options = null)
        : base(new QueueStorageBatchApplication(pipeline, options), serviceResolverFactory)
    { }
}

/// <summary>
/// Runs every message in a Queue Storage delivery through the middleware pipeline concurrently, each
/// in its own service scope, applying <see cref="QueueStorageOptions"/> to decide whether a message's
/// exception or failure result is contained (logged) or left to cascade and fail the invocation (so
/// the host's poison handling engages). The fan-out/settle/escalate/log skeleton itself lives in
/// <see cref="AzureFunctionBatchApplicationBase{TContext, TState}"/>; this class plugs in the
/// Queue Storage-specific bits - Queue Storage uses no extra per-item state, so <c>TState</c> is
/// <c>object?</c>.
/// </summary>
public class QueueStorageBatchApplication : AzureFunctionBatchApplicationBase<QueueStorageContext, object?>, IMiddlewareApplication<QueueStorageMessage[]>
{
    public QueueStorageBatchApplication(IMiddlewarePipeline<QueueStorageContext> pipeline, QueueStorageOptions? options = null)
        : base(pipeline, TransportNames.QueueStorage, (options ??= new QueueStorageOptions()).CatchExceptions, options.RaiseOnFailureStatus, options.MaxDegreeOfParallelism)
    { }

    public Task HandleAsync(QueueStorageMessage[] @event, IServiceResolverFactory serviceResolverFactory)
        => HandleAsync(@event, serviceResolverFactory, CancellationToken.None);

    /// <summary>
    /// Runs every message in the delivery through the pipeline, additionally seeding <b>each</b>
    /// message's own scope with the ambient cancellation token so any component resolved during that
    /// message's pipeline run can observe cancellation via <see cref="ICancellationTokenAccessor"/>.
    /// </summary>
    public Task HandleAsync(QueueStorageMessage[] @event, IServiceResolverFactory serviceResolverFactory, CancellationToken cancellationToken)
        => HandleBatchAsync(@event.Select(item => (new QueueStorageContext(item), (object?)null)), serviceResolverFactory, cancellationToken);

    /// <inheritdoc/>
    protected override Exception CreateProcessingException(QueueStorageContext context)
        => new QueueStorageMessageProcessingException(context.Message.MessageId ?? "unknown");

    /// <inheritdoc/>
    protected override object? GetLogId(QueueStorageContext context) => context.Message.MessageId;

    /// <inheritdoc/>
    protected override string FailureLogMessageTemplate => "Processing Queue Storage message {messageId} failed";

    /// <inheritdoc/>
    protected override ILogger GetLogger(IServiceResolver serviceResolver)
        => serviceResolver.GetService<ILogger<QueueStorageApplication>>();
}
