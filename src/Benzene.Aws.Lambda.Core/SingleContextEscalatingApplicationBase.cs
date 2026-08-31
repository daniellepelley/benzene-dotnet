using System;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Middleware;
using Benzene.Core;
using Microsoft.Extensions.Logging;

namespace Benzene.Aws.Lambda.Core;

/// <summary>
/// Shared try/catch/escalate/log logic for the "run one context through the pipeline, in its own DI
/// scope, and decide whether an exception or a returned failure result should cascade" shape that
/// <c>Benzene.Aws.Lambda.Sns</c>, <c>Benzene.Aws.Lambda.S3</c>, and
/// <c>Benzene.Aws.Lambda.EventBridge</c> each implement (per-record for SNS/S3's fan-out batches,
/// per-invocation for EventBridge's single event) - factored here so those three near-identical
/// implementations can't keep drifting the way SQS/Kafka's own near-identical per-record logic
/// already has. Not adopted by SQS/Kafka themselves (their extra batch-failure-mode/partition
/// concerns don't fit this single-context shape) or by DynamoDB/Kinesis (deliberately different
/// ordered-stream shapes - see their own class docs).
/// </summary>
/// <typeparam name="TSelf">
/// The concrete derived application type (e.g. <c>SnsApplication</c>) - used only to key the
/// <see cref="ILogger{TSelf}"/> category resolved for the failure log, so each transport's logs keep
/// their own recognizable category instead of every transport sharing this base's.
/// </typeparam>
/// <typeparam name="TContext">The per-record/per-event pipeline context type.</typeparam>
public abstract class SingleContextEscalatingApplicationBase<TSelf, TContext>
    where TContext : IHasMessageResult
{
    private readonly IMiddlewarePipeline<TContext> _pipeline;
    private readonly bool _catchExceptions;
    private readonly bool _raiseOnFailureStatus;
    private readonly Func<TContext, string?> _idSelector;
    private readonly Func<string?, Exception> _exceptionFactory;
    private readonly string _failureLogMessage;

    /// <summary>
    /// Initializes the shared escalation logic.
    /// </summary>
    /// <param name="pipeline">The pipeline to run each context through (already transport-tagged).</param>
    /// <param name="catchExceptions">
    /// Whether an exception (including one this instance itself throws when escalating a failure
    /// result - see <paramref name="raiseOnFailureStatus"/>) is caught (logged, not left to cascade)
    /// instead of propagated out of <see cref="ProcessAsync"/>.
    /// </param>
    /// <param name="raiseOnFailureStatus">
    /// Whether a context whose <see cref="IHasMessageResult.MessageResult"/> comes back unsuccessful -
    /// including a null/unestablished outcome (typically an unrouted message: no handler matched the
    /// topic), which is treated the same as an explicit failure, not as success - and didn't itself
    /// throw is escalated into a thrown exception via <paramref name="exceptionFactory"/>. See
    /// work/settlement-consistency-fix-plan.md.
    /// </param>
    /// <param name="idSelector">Extracts the transport's own correlation id from a context (e.g. an SNS <c>MessageId</c>).</param>
    /// <param name="exceptionFactory">Builds the transport's own <c>*MessageProcessingException</c> from that id.</param>
    /// <param name="failureLogMessage">
    /// The log message template used when an exception is caught, with a single <c>{id}</c>
    /// placeholder for the context's correlation id (e.g. <c>"Processing SNS message {id} failed"</c>).
    /// </param>
    protected SingleContextEscalatingApplicationBase(
        IMiddlewarePipeline<TContext> pipeline,
        bool catchExceptions,
        bool raiseOnFailureStatus,
        Func<TContext, string?> idSelector,
        Func<string?, Exception> exceptionFactory,
        string failureLogMessage)
    {
        _pipeline = pipeline;
        _catchExceptions = catchExceptions;
        _raiseOnFailureStatus = raiseOnFailureStatus;
        _idSelector = idSelector;
        _exceptionFactory = exceptionFactory;
        _failureLogMessage = failureLogMessage;
    }

    /// <summary>
    /// Runs one context through the pipeline in its own service scope. Whether a thrown exception or
    /// an escalated failure result propagates out of this call (and therefore, for the caller's own
    /// batch/invocation, cascades further) is governed by the <c>catchExceptions</c>/
    /// <c>raiseOnFailureStatus</c> options this instance was constructed with.
    /// </summary>
    /// <param name="context">The context to process.</param>
    /// <param name="serviceResolverFactory">The service resolver factory used to create this context's scope.</param>
    protected async Task ProcessAsync(TContext context, IServiceResolverFactory serviceResolverFactory)
    {
        try
        {
            using (var scope = serviceResolverFactory.CreateScope())
            {
                await _pipeline.HandleAsync(context, scope);
            }

            // A null/unestablished outcome (MessageResult never set - typically an unrouted message: no
            // handler matched the topic) is escalated the same as an explicit failure, not treated as
            // success. SNS/S3/EventBridge each have a redelivery backstop for the resulting retry (SNS
            // subscription retry/redrive, S3 async-invoke retry + on-failure destination, EventBridge
            // rule target retry/DLQ), so retaining an unrouted message here is safe - unlike Kafka/Event
            // Hub, which have no per-record dead-letter path and carve this out instead of retaining (see
            // work/settlement-consistency-fix-plan.md).
            if (_raiseOnFailureStatus && context.MessageResult?.IsSuccessful != true)
            {
                // _idSelector runs here, inside the try, deliberately - not unconditionally before it.
                // Some transports' selectors (SNS, EventBridge) dereference nested event properties with
                // no null-conditional chaining, matching how those two only ever touched them on this
                // same already-escalating path before this base existed. Evaluating it any earlier would
                // mean a null/malformed record throws unconditionally on every context, bypassing
                // catchExceptions entirely instead of being subject to it like everything else here.
                throw _exceptionFactory(_idSelector(context));
            }
        }
        catch (Exception ex) when (_catchExceptions)
        {
            var isInfrastructure = BenzeneFailure.IsInfrastructure(ex);

            using (var loggingScope = serviceResolverFactory.CreateScope())
            {
                loggingScope.GetService<ILogger<TSelf>>()
                    .LogError(ex, isInfrastructure
                        ? BenzeneFailure.InfrastructureLogPrefix + " " + _failureLogMessage + " — this service is mis-wired; the message is not at fault. Infrastructure failure — rethrowing despite CatchExceptions."
                        : _failureLogMessage, SafeId(context));
            }

            // An infrastructure failure is not this message's fault and is not retryable per message.
            // Unlike a business failure (a handler exception, or the failure result escalated above),
            // this transport has no partial-failure channel to report it on - swallowing it here would
            // mean the whole invocation reports success while every message fails the same way, forever.
            // So it escapes CatchExceptions entirely and fails the invocation, exactly as SqsApplication's
            // own unconditional infra rethrow does for the same reason.
            if (isInfrastructure)
            {
                throw;
            }
        }
    }

    /// <summary>
    /// <see cref="_idSelector"/>, but guaranteed not to throw: this runs inside the catch block purely
    /// to label a log line, and a selector that itself faults on a malformed context must never mask
    /// the real exception being logged or escape the catch that's supposed to be swallowing it.
    /// </summary>
    private string? SafeId(TContext context)
    {
        try
        {
            return _idSelector(context);
        }
        catch
        {
            return "unknown";
        }
    }
}
