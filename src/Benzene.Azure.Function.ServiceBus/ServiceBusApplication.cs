using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Azure.Function.Core;
using Benzene.Core.Middleware;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Benzene.Azure.Function.ServiceBus;

/// <summary>
/// The entry point application for a Service Bus-triggered Azure Function. Maps each message in the
/// triggered batch to a <see cref="ServiceBusContext"/> and runs them all through the middleware pipeline,
/// tagging the transport as <c>"service-bus"</c> for the duration.
/// </summary>
public class ServiceBusApplication : EntryPointMiddlewareApplication<ServiceBusReceivedMessage[]>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceBusApplication"/> class.
    /// </summary>
    /// <param name="pipeline">The built Service Bus middleware pipeline to run each message through.</param>
    /// <param name="serviceResolverFactory">The service resolver factory used to process each batch.</param>
    /// <param name="options">
    /// Configures how a handler's exceptions and failure results are handled. Defaults to a new
    /// <see cref="ServiceBusOptions"/> instance (safe-by-default:
    /// <see cref="ServiceBusOptions.RaiseOnFailureStatus"/> on,
    /// <see cref="ServiceBusOptions.CatchExceptions"/> off) if omitted.
    /// </param>
    public ServiceBusApplication(IMiddlewarePipeline<ServiceBusContext> pipeline, IServiceResolverFactory serviceResolverFactory, ServiceBusOptions? options = null)
        : base(new ServiceBusBatchApplication(pipeline, options), serviceResolverFactory)
    { }
}

/// <summary>
/// Runs every message in a Service Bus trigger batch through the middleware pipeline concurrently,
/// each in its own service scope, applying <see cref="ServiceBusOptions"/> to decide whether a
/// message's exception or failure result is contained (logged, doesn't affect the rest of the batch)
/// or left to cascade and fail the whole invocation - and, when <see cref="ServiceBusOptions.AckMode"/>
/// is <see cref="ServiceBusAckMode.Explicit"/>, to complete or abandon each message individually
/// based on that same outcome.
/// </summary>
/// <remarks>
/// The fan-out/settle/escalate/log skeleton itself lives in
/// <see cref="AzureFunctionBatchApplicationBase{TContext, TState}"/>. Service Bus is the one
/// transport package that plugs into every hook, because <see cref="ServiceBusOptions.AckMode"/> =
/// <see cref="ServiceBusAckMode.Explicit"/> needs per-message state beyond the context itself (the
/// <see cref="ServiceBusMessageActions"/> to complete/abandon against, and whether this message has
/// already been acted on) - carried as this class's private <see cref="AckState"/>, the base's
/// <c>TState</c>.
/// </remarks>
public class ServiceBusBatchApplication : AzureFunctionBatchApplicationBase<ServiceBusContext, ServiceBusBatchApplication.AckState>, IMiddlewareApplication<ServiceBusReceivedMessage[]>, IMiddlewareApplication<ServiceBusTriggerBatch>
{
    private readonly ServiceBusOptions _options;

    public ServiceBusBatchApplication(IMiddlewarePipeline<ServiceBusContext> pipeline, ServiceBusOptions? options = null)
        : base(pipeline, TransportNames.ServiceBus, (options ??= new ServiceBusOptions()).CatchExceptions, options.RaiseOnFailureStatus, options.MaxDegreeOfParallelism)
    {
        _options = options;
    }

    /// <summary>
    /// Handles a batch with no <see cref="ServiceBusMessageActions"/> available - <see cref="ServiceBusOptions.AckMode"/>
    /// has no effect here even if set to <see cref="ServiceBusAckMode.Explicit"/>, since there is
    /// nothing to complete/abandon against; use the <see cref="ServiceBusTriggerBatch"/> overload
    /// (via <c>Extensions.HandleServiceBusMessages(IAzureFunctionApp, ServiceBusMessageActions,
    /// ServiceBusReceivedMessage[])</c>) for explicit ack mode to take effect.
    /// </summary>
    public Task HandleAsync(ServiceBusReceivedMessage[] @event, IServiceResolverFactory serviceResolverFactory)
        => HandleAsync(@event, serviceResolverFactory, CancellationToken.None);

    /// <summary>
    /// Handles a batch with no <see cref="ServiceBusMessageActions"/> available, additionally seeding
    /// <b>each</b> message's own scope with the ambient cancellation token so any component resolved
    /// during that message's pipeline run can observe cancellation via
    /// <see cref="ICancellationTokenAccessor"/>.
    /// </summary>
    public Task HandleAsync(ServiceBusReceivedMessage[] @event, IServiceResolverFactory serviceResolverFactory, CancellationToken cancellationToken)
        => HandleAsync(@event, messageActions: null, serviceResolverFactory, cancellationToken);

    /// <summary>
    /// Handles a batch together with the <see cref="ServiceBusMessageActions"/> needed to complete/abandon
    /// each message - required for <see cref="ServiceBusOptions.AckMode"/> = <see cref="ServiceBusAckMode.Explicit"/>
    /// to actually complete/abandon messages.
    /// </summary>
    public Task HandleAsync(ServiceBusTriggerBatch @event, IServiceResolverFactory serviceResolverFactory)
        => HandleAsync(@event, serviceResolverFactory, CancellationToken.None);

    /// <summary>
    /// Handles a batch together with <see cref="ServiceBusMessageActions"/>, additionally seeding
    /// <b>each</b> message's own scope with the ambient cancellation token so any component resolved
    /// during that message's pipeline run can observe cancellation via
    /// <see cref="ICancellationTokenAccessor"/>.
    /// </summary>
    public Task HandleAsync(ServiceBusTriggerBatch @event, IServiceResolverFactory serviceResolverFactory, CancellationToken cancellationToken)
        => HandleAsync(@event.Messages, @event.MessageActions, serviceResolverFactory, cancellationToken);

    private Task HandleAsync(ServiceBusReceivedMessage[] messages, ServiceBusMessageActions? messageActions, IServiceResolverFactory serviceResolverFactory, CancellationToken cancellationToken)
    {
        var explicitAck = messageActions != null && _options.AckMode == ServiceBusAckMode.Explicit;

        // Built here (not via a base-class item-to-context hook) so messageActions/explicitAck - both
        // local to this one call - can flow into each message's AckState via an ordinary closure,
        // rather than through shared mutable state on this (potentially long-lived, concurrently
        // invoked) application instance.
        var entries = messages.Select(message => (new ServiceBusContext(message), new AckState(messageActions, explicitAck)));

        // BoundedFanOut (via the base class) optionally caps how many messages run at once
        // (ServiceBusOptions.MaxDegreeOfParallelism); unset leaves the fan-out unbounded, exactly as
        // before.
        return HandleBatchAsync(entries, serviceResolverFactory, cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task OnPipelineSucceededAsync(ServiceBusContext context, AckState state)
    {
        if (!state.ExplicitAck)
        {
            return;
        }

        state.Acked = true;

        // Abandon on failure OR a null result (a pipeline that short-circuited without setting one),
        // completing only on genuine success - matching the SQS reference so an unestablished outcome
        // errs toward redelivery, not silent completion/loss.
        if (context.MessageResult?.IsSuccessful != true)
        {
            await state.MessageActions!.AbandonMessageAsync(context.Message);
        }
        else
        {
            await state.MessageActions!.CompleteMessageAsync(context.Message);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Escalates a failure result to a thrown exception only under <c>AutoComplete</c>: the Functions
    /// host (<c>AutoCompleteMessages=true</c>) then abandons the message, which is the only
    /// redelivery lever there. Under Explicit ack the message was already abandoned by
    /// <see cref="OnPipelineSucceededAsync"/> above, so throwing again would be redundant and would
    /// needlessly fail the whole (possibly batched) invocation even though every message was settled
    /// individually.
    /// </remarks>
    protected override bool ShouldEscalateFailure(ServiceBusContext context, AckState state) => !state.ExplicitAck;

    /// <inheritdoc/>
    protected override async Task OnExceptionCaughtAsync(ServiceBusContext context, AckState state, Exception exception)
    {
        if (state.ExplicitAck && !state.Acked)
        {
            await state.MessageActions!.AbandonMessageAsync(context.Message);
        }
    }

    /// <inheritdoc/>
    protected override bool ShouldCleanUpBeforeRethrow(ServiceBusContext context, AckState state)
        => state.ExplicitAck && !state.Acked;

    /// <inheritdoc/>
    protected override Task CleanUpBeforeRethrowAsync(ServiceBusContext context, AckState state)
        => state.MessageActions!.AbandonMessageAsync(context.Message);

    /// <inheritdoc/>
    protected override Exception CreateProcessingException(ServiceBusContext context)
        => new ServiceBusMessageProcessingException(context.Message.MessageId);

    /// <inheritdoc/>
    protected override object GetLogId(ServiceBusContext context) => context.Message.MessageId;

    /// <inheritdoc/>
    protected override string FailureLogMessageTemplate => "Processing Service Bus message {messageId} failed";

    /// <inheritdoc/>
    protected override ILogger GetLogger(IServiceResolver serviceResolver)
        => serviceResolver.GetService<ILogger<ServiceBusApplication>>();

    /// <summary>
    /// Per-message ack-tracking state threaded through <see cref="ServiceBusBatchApplication"/>'s hook
    /// overrides: the <see cref="ServiceBusMessageActions"/> to complete/abandon against (if any - only
    /// present when the <see cref="ServiceBusTriggerBatch"/> overload was used), whether
    /// <see cref="ServiceBusOptions.AckMode"/> is <see cref="ServiceBusAckMode.Explicit"/> for this
    /// call, and whether this specific message has already been completed/abandoned.
    /// </summary>
    public sealed class AckState
    {
        internal AckState(ServiceBusMessageActions? messageActions, bool explicitAck)
        {
            MessageActions = messageActions;
            ExplicitAck = explicitAck;
        }

        internal ServiceBusMessageActions? MessageActions { get; }

        internal bool ExplicitAck { get; }

        internal bool Acked { get; set; }
    }
}
