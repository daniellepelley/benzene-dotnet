using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Middleware;
using Benzene.Core;
using Benzene.Core.MessageHandlers.Info;
using Benzene.Core.Middleware;
using Microsoft.Extensions.Logging;

namespace Benzene.Azure.Function.Core;

/// <summary>
/// Shared fan-out/settle/escalate/log skeleton for a Functions-trigger batch application. Maps a
/// triggered batch to per-item contexts, runs each one through the middleware pipeline concurrently -
/// each in its own DI scope, seeded with the ambient cancellation token, via
/// <see cref="Benzene.Core.Middleware.BoundedFanOut"/> - and applies the CatchExceptions/
/// RaiseOnFailureStatus/MaxDegreeOfParallelism contract every Azure Functions trigger package exposes
/// via its own <c>*Options</c> type: a non-exception failure result recorded on
/// <see cref="IHasMessageResult.MessageResult"/> is escalated into a thrown, transport-specific
/// processing exception, and an exception caught under <c>CatchExceptions</c> is logged with
/// <see cref="BenzeneFailure"/> distinguishing a mis-wiring (infrastructure) failure from a
/// per-message one in the log line.
/// </summary>
/// <typeparam name="TContext">
/// The per-item pipeline context; must record its outcome via <see cref="IHasMessageResult"/>.
/// </typeparam>
/// <typeparam name="TState">
/// Per-item state threaded alongside the context into every hook below, for a transport that needs
/// more than the context itself to settle an item - for example Service Bus's per-message
/// <c>ServiceBusMessageActions</c> complete/abandon call and its "already acked" tracking. A transport
/// with nothing extra to track uses <c>object?</c> and pairs each context with <c>null</c> when
/// building the entries passed to <see cref="HandleBatchAsync"/>.
/// </typeparam>
/// <remarks>
/// Every hook is virtual with a behavior-preserving default (no-op / always escalate / never clean up).
/// Only <c>Benzene.Azure.Function.ServiceBus</c>'s <c>ServiceBusBatchApplication</c> overrides them, to
/// preserve its documented deviation: under <c>ServiceBusOptions.AckMode = Explicit</c> the message is
/// already completed/abandoned per-message by the time the escalation check runs, so escalating a
/// failure result into a second, batch-failing throw would be redundant - see that override's own doc
/// comments.
/// </remarks>
public abstract class AzureFunctionBatchApplicationBase<TContext, TState>
    where TContext : IHasMessageResult
{
    private readonly IMiddlewarePipeline<TContext> _pipeline;
    private readonly bool _catchExceptions;
    private readonly bool _raiseOnFailureStatus;
    private readonly int? _maxDegreeOfParallelism;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="AzureFunctionBatchApplicationBase{TContext, TState}"/> class.
    /// </summary>
    /// <param name="pipeline">
    /// The transport's built middleware pipeline. Wrapped internally in a
    /// <see cref="TransportMiddlewarePipeline{TContext}"/> that tags <paramref name="transportName"/>
    /// as the current transport for the duration of each item - callers pass the raw pipeline, not a
    /// pre-wrapped one.
    /// </param>
    /// <param name="transportName">The transport name recorded via <c>ICurrentTransport</c> (e.g. <c>TransportNames.ServiceBus</c>).</param>
    /// <param name="catchExceptions">
    /// Whether an item's unhandled exception is caught and logged instead of left to cascade and fail
    /// the whole batch invocation.
    /// </param>
    /// <param name="raiseOnFailureStatus">
    /// Whether a non-exception failure result is escalated into a thrown
    /// <see cref="CreateProcessingException"/> (subject to <see cref="ShouldEscalateFailure"/>).
    /// </param>
    /// <param name="maxDegreeOfParallelism">
    /// The maximum number of items processed concurrently; <c>null</c> or &lt;= 0 leaves the fan-out
    /// unbounded.
    /// </param>
    protected AzureFunctionBatchApplicationBase(
        IMiddlewarePipeline<TContext> pipeline,
        string transportName,
        bool catchExceptions,
        bool raiseOnFailureStatus,
        int? maxDegreeOfParallelism)
    {
        _pipeline = new TransportMiddlewarePipeline<TContext>(transportName, pipeline);
        _catchExceptions = catchExceptions;
        _raiseOnFailureStatus = raiseOnFailureStatus;
        _maxDegreeOfParallelism = maxDegreeOfParallelism;
    }

    /// <summary>Builds the transport-specific exception thrown to escalate a failure result.</summary>
    protected abstract Exception CreateProcessingException(TContext context);

    /// <summary>The value logged as this item's identifier (message id, sequence number, topic, ...).</summary>
    protected abstract object? GetLogId(TContext context);

    /// <summary>
    /// The structured-logging message template for a failed item, with the one placeholder consumed
    /// by <see cref="GetLogId"/> - e.g. <c>"Processing Service Bus message {messageId} failed"</c>.
    /// </summary>
    protected abstract string FailureLogMessageTemplate { get; }

    /// <summary>Resolves the transport's own typed <c>ILogger&lt;TApplication&gt;</c> from a scope.</summary>
    protected abstract ILogger GetLogger(IServiceResolver serviceResolver);

    /// <summary>
    /// Runs after the pipeline completes successfully, before the failure-result escalation check.
    /// Default no-op. Service Bus overrides this to complete/abandon the message under
    /// <c>ServiceBusOptions.AckMode = Explicit</c>.
    /// </summary>
    protected virtual Task OnPipelineSucceededAsync(TContext context, TState state) => Task.CompletedTask;

    /// <summary>
    /// Whether a failure result recorded on <paramref name="context"/> (with this application's own
    /// <c>raiseOnFailureStatus</c> already true) should be escalated into a thrown
    /// <see cref="CreateProcessingException"/> right now. Default <c>true</c>. Service Bus overrides
    /// this to <c>false</c> under <c>AckMode = Explicit</c>, since the message was already abandoned
    /// by <see cref="OnPipelineSucceededAsync"/> - escalating too would needlessly fail the whole
    /// (possibly batched) invocation even though every message was settled individually.
    /// </summary>
    protected virtual bool ShouldEscalateFailure(TContext context, TState state) => true;

    /// <summary>
    /// Whether an unestablished outcome (no result recorded - typically an unrouted message no handler
    /// matched) is escalated like a failure. True for queue-shaped transports, which have a DLQ /
    /// poison / dequeue-count backstop to catch a retained message. Overridden to false by the stream
    /// transports, which have no per-record dead-letter path: retaining an unrouted record there would
    /// replay the partition from that offset forever. See work/settlement-consistency-fix-plan.md.
    /// </summary>
    protected virtual bool EscalateUnestablishedOutcome => true;

    /// <summary>
    /// Runs inside the <c>catch (Exception ex) when (catchExceptions)</c> block, before the exception
    /// is logged. Default no-op. Service Bus overrides this to abandon a not-yet-settled message.
    /// </summary>
    /// <param name="serviceResolver">
    /// A scope created (and disposed) around this call and the failure-log line that follows it -
    /// lets an override resolve its own logger to report a failure <em>inside</em> this hook (e.g.
    /// Service Bus's fallback-abandon itself throwing) without needing to carry a resolver of its
    /// own. An override should guard its own risky work in its own try/catch-and-log too (see
    /// <c>ServiceBusBatchApplication.OnExceptionCaughtAsync</c>) rather than relying solely on
    /// <see cref="ProcessItemAsync"/>'s own guard around this call - that guard exists so a future
    /// transport's hook can't reintroduce the masking this parameter and guard exist to prevent, not
    /// as a substitute for guarding the hook itself.
    /// </param>
    protected virtual Task OnExceptionCaughtAsync(TContext context, TState state, Exception exception, IServiceResolver serviceResolver) => Task.CompletedTask;

    /// <summary>
    /// Whether an exception NOT swallowed under <c>catchExceptions</c> should still run
    /// <see cref="CleanUpBeforeRethrowAsync"/> before it cascades unhandled. Default <c>false</c>.
    /// Service Bus overrides this so a not-yet-settled message is abandoned even when
    /// <c>CatchExceptions</c> is off.
    /// </summary>
    protected virtual bool ShouldCleanUpBeforeRethrow(TContext context, TState state) => false;

    /// <summary>
    /// Cleanup run immediately before an unhandled exception is rethrown, when
    /// <see cref="ShouldCleanUpBeforeRethrow"/> is true.
    /// </summary>
    /// <param name="serviceResolver">
    /// A scope created (and disposed) around this call, for the same reason
    /// <see cref="OnExceptionCaughtAsync"/> receives one - see that parameter's own doc comment.
    /// </param>
    protected virtual Task CleanUpBeforeRethrowAsync(TContext context, TState state, IServiceResolver serviceResolver) => Task.CompletedTask;

    /// <summary>
    /// Runs every <c>(context, state)</c> entry in <paramref name="entries"/> through the pipeline
    /// concurrently, each in its own service scope seeded with <paramref name="cancellationToken"/>,
    /// applying the configured CatchExceptions/RaiseOnFailureStatus/MaxDegreeOfParallelism contract.
    /// A caller builds <paramref name="entries"/> itself (e.g. <c>@event.Select(item => (new
    /// XContext(item), state))</c>) rather than this base class owning item-to-context mapping, so a
    /// per-call value only the caller has (like Service Bus's <c>ServiceBusMessageActions</c>) can
    /// flow into <typeparamref name="TState"/> via an ordinary local-variable closure - no shared
    /// mutable state on the application instance itself, which could otherwise race across concurrent
    /// invocations of the same trigger.
    /// </summary>
    protected Task HandleBatchAsync(IEnumerable<(TContext Context, TState State)> entries, IServiceResolverFactory serviceResolverFactory, CancellationToken cancellationToken)
    {
        return BoundedFanOut.WhenAllAsync(
            entries,
            entry => ProcessItemAsync(entry.Context, entry.State, serviceResolverFactory, cancellationToken),
            _maxDegreeOfParallelism,
            cancellationToken);
    }

    private async Task ProcessItemAsync(TContext context, TState state, IServiceResolverFactory serviceResolverFactory, CancellationToken cancellationToken)
    {
        try
        {
            using (var scope = serviceResolverFactory.CreateScope())
            {
                scope.SeedCancellationToken(cancellationToken);
                await _pipeline.HandleAsync(context, scope);
            }

            await OnPipelineSucceededAsync(context, state);

            // EscalateUnestablishedOutcome ? outcome != true : outcome == false - the null-outcome half
            // of the settlement policy is a per-transport override (see that property's own doc comment),
            // not a blanket polarity: queue-shaped transports (QueueStorage/EventGrid/ServiceBus) retain
            // an unestablished outcome like a failure, while Kafka/EventHub override it back to false and
            // keep ack-on-null, because they have no per-record dead-letter path.
            var outcome = context.MessageResult?.IsSuccessful;
            var shouldSettleAsFailure = EscalateUnestablishedOutcome ? outcome != true : outcome == false;

            if (_raiseOnFailureStatus && shouldSettleAsFailure && ShouldEscalateFailure(context, state))
            {
                throw CreateProcessingException(context);
            }
        }
        catch (Exception ex) when (_catchExceptions)
        {
            var isInfrastructure = BenzeneFailure.IsInfrastructure(ex);

            using (var loggingScope = serviceResolverFactory.CreateScope())
            {
                var logger = GetLogger(loggingScope);

                // Guarded in its own try/catch so a failure inside the hook (e.g. Service Bus's
                // fallback-abandon throwing because the lock already expired) can never replace or
                // suppress logging `ex` below - the original processing failure this whole catch
                // block exists to report. Mirrors BenzeneServiceBusWorker.HandleMessageAsync's guard
                // (see its own comment, the spec for this fix); guarded here on top of whatever an
                // override does itself so this masking failure mode can't be reintroduced by a
                // future transport's hook forgetting to guard its own risky work.
                try
                {
                    await OnExceptionCaughtAsync(context, state, ex, loggingScope);
                }
                catch (Exception hookEx)
                {
                    logger.LogError(hookEx,
                        "OnExceptionCaughtAsync failed while handling {logId}; the original failure is still logged below, not masked by this one",
                        GetLogId(context));
                }

                logger.LogError(ex, isInfrastructure
                    ? BenzeneFailure.InfrastructureLogPrefix + " " + FailureLogMessageTemplate + " — this service is mis-wired; the message is not at fault"
                    : FailureLogMessageTemplate, GetLogId(context));
            }

            // #257: an infrastructure/DI-wiring failure is not this item's fault and will fail
            // identically for every item - swallowing it here would mean the whole invocation reports
            // success while every item fails the same way, forever (the exact #228 defect this mirrors
            // for AWS SNS/S3/EventBridge's SingleContextEscalatingApplicationBase.ProcessAsync). It
            // escapes CatchExceptions entirely and fails the invocation loudly. For ServiceBus's
            // AckMode = Explicit, OnExceptionCaughtAsync above has already abandoned the not-yet-settled
            // message, so this composes cleanly: the message is still abandoned AND the invocation now
            // fails.
            if (isInfrastructure)
            {
                throw;
            }

            // #258: a genuine ambient cancellation (the Functions host asking this invocation to stop)
            // must not be treated as an ordinary per-item failure - #230 already makes a still-*queued*
            // item observe the same cancellation and abort the whole invocation regardless of
            // CatchExceptions; without this, an already-*running* item hit by the identical cancellation
            // was instead logged and swallowed here, giving two items affected by the same host event
            // opposite severity purely by scheduling luck. Token-verified (matching MessageHandler.cs's
            // pattern), not a bare type-based exclusion, so an application-produced
            // OperationCanceledException unrelated to host shutdown is not over-escalated.
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
        catch (Exception) when (ShouldCleanUpBeforeRethrow(context, state))
        {
            // Same guard and rationale as the OnExceptionCaughtAsync branch above: a failure inside
            // the cleanup hook must never replace the exception the `throw;` below is about to
            // rethrow.
            using (var loggingScope = serviceResolverFactory.CreateScope())
            {
                try
                {
                    await CleanUpBeforeRethrowAsync(context, state, loggingScope);
                }
                catch (Exception cleanupEx)
                {
                    GetLogger(loggingScope).LogError(cleanupEx,
                        "CleanUpBeforeRethrowAsync failed while handling {logId}; the original exception is still rethrown, not masked by this one",
                        GetLogId(context));
                }
            }

            throw;
        }
    }
}
