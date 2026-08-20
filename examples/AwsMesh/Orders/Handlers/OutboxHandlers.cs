using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Outbox;
using Benzene.Outbox.DynamoDb;
using Benzene.Results;
using Microsoft.Extensions.Logging;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Examples.AwsMesh.Orders.Handlers;

/// <summary>
/// The Lambda relay pair the outbox plan documents as the recommended default for AWS Lambda
/// (<c>work/archive/outbox-plan-2026-08.md</c> §2.5): streams dispatch for latency, a low-frequency scheduled sweep
/// for retry/park/cleanup. Both handlers are intentionally a few lines — <see cref="IOutboxDispatcher"/>
/// (registered by <c>AddOutbox</c> in <c>Startup</c>) is the whole engine; these just call in.
/// </summary>

/// <summary>
/// Fires once per row inserted into the <c>orders-outbox</c> DynamoDB table — wired via the
/// <c>aws_lambda_event_source_mapping</c> in <c>deploy/main.tf</c> from the table's stream to this
/// Lambda, consumed here as topic <c>orders-outbox:INSERT</c> (table:eventName, plan decision DS2 —
/// see <c>Benzene.Aws.Lambda.DynamoDb</c>). Dispatches the just-captured envelope near-real-time,
/// rather than waiting for <see cref="OutboxSweepMessageHandler"/>'s schedule.
/// </summary>
/// <remarks>
/// If the dispatch throws, this handler's exception fails the stream batch — the event source
/// mapping's <c>maximum_retry_attempts</c> redrives it a bounded number of times before giving up on
/// that record, at which point the envelope simply stays <c>Pending</c> until the next
/// <see cref="OutboxSweepMessageHandler"/> run claims it. So a persistently failing downstream target
/// cannot wedge this Lambda forever — the sweep is always the backstop.
/// </remarks>
[Message("orders-outbox:INSERT")]
public class OutboxStreamDispatchMessageHandler : IMessageHandler<OutboxStreamImage, Void>
{
    private readonly IOutboxDispatcher _dispatcher;
    private readonly ILogger<OutboxStreamDispatchMessageHandler> _logger;

    public OutboxStreamDispatchMessageHandler(IOutboxDispatcher dispatcher, ILogger<OutboxStreamDispatchMessageHandler> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task<IBenzeneResult<Void>> HandleAsync(OutboxStreamImage request)
    {
        var outcome = await _dispatcher.DispatchOneAsync(request.Id);
        _logger.LogInformation("outbox stream dispatch {envelopeId} ({topic}): {outcome}", request.Id, request.Topic, outcome);
        return BenzeneResult.Ok<Void>();
    }
}

/// <summary>
/// Fires on the schedule EventBridge rule in <c>deploy/main.tf</c> — the same "scheduled rule invokes
/// this Lambda directly, detail-type-routed" shape <c>examples/AwsMesh/Mesh</c>'s <c>mesh:aggregate</c>
/// already uses, handled here by the ordinary <c>UseEventBridge</c> ingress every service already
/// mounts. <b>Deliberately an app-chosen topic, never <c>benzene:*</c></b> — reserved topics are spec
/// surface (<c>work/archive/outbox-plan-2026-08.md</c> §2.5/§3). Redrives whatever
/// <see cref="OutboxStreamDispatchMessageHandler"/> missed (a permission blip, a cold start that
/// outran the stream, a crash between claim and send), retries with backoff, and parks anything past
/// <c>OutboxOptions.MaxAttempts</c>.
/// </summary>
[Message("orders:outbox-sweep")]
public class OutboxSweepMessageHandler : IMessageHandler<Void, OutboxSweepResult>
{
    private readonly IOutboxDispatcher _dispatcher;
    private readonly ILogger<OutboxSweepMessageHandler> _logger;

    public OutboxSweepMessageHandler(IOutboxDispatcher dispatcher, ILogger<OutboxSweepMessageHandler> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task<IBenzeneResult<OutboxSweepResult>> HandleAsync(Void request)
    {
        var result = await _dispatcher.RunOnceAsync();
        _logger.LogInformation(
            "outbox sweep: {dispatched} dispatched, {rescheduled} rescheduled, {parked} parked, {deletedRetired} retired",
            result.Dispatched, result.Rescheduled, result.Parked, result.DeletedRetired);
        return BenzeneResult.Ok(new OutboxSweepResult(result.Dispatched, result.Rescheduled, result.Parked, result.DeletedRetired));
    }
}

/// <summary>The sweep's tally, so a direct-invoke caller (or a human via the Lambda test tool) can see it.</summary>
public record OutboxSweepResult(int Dispatched, int Rescheduled, int Parked, int DeletedRetired);
