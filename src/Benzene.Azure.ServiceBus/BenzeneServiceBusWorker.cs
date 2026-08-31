using Azure.Messaging.ServiceBus;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Hosting;
using Microsoft.Extensions.Logging;

namespace Benzene.Azure.ServiceBus;

/// <summary>
/// A long-running worker that consumes a Service Bus queue or topic subscription directly via a
/// <see cref="ServiceBusProcessor"/> and dispatches each received message through the middleware
/// pipeline - for <c>Benzene.HostedService</c>/<c>Benzene.SelfHost</c>, not Azure Functions (use
/// <c>Benzene.Azure.Function.ServiceBus</c> for a Service Bus trigger).
/// </summary>
/// <remarks>
/// This is one of the "self-hosted worker" startup modes documented in <c>docs/hosting.md</c> -
/// like <c>BenzeneKafkaWorker</c>, Benzene owns the process here. Unlike the SQS/Kafka workers,
/// nothing is polled by hand: the <see cref="ServiceBusProcessor"/> owns receiving, lock renewal,
/// and bounded concurrency (<see cref="BenzeneServiceBusConfig.MaxConcurrentCalls"/>) itself, and
/// pushes each message to this worker's handler. <see cref="StartAsync"/> starts the processor and
/// returns; <see cref="StopAsync"/> stops it, waiting for in-flight handlers to finish, then
/// disposes the processor (never the client - see <see cref="StopAsync"/>'s own doc comment).
/// Receive-side failures (e.g. a transient connection error)
/// surface through the processor's error handler, are logged, and the processor keeps receiving -
/// they never end the worker.
/// </remarks>
public class BenzeneServiceBusWorker : IBenzeneWorker
{
    private readonly IServiceResolverFactory _serviceResolverFactory;
    private readonly ServiceBusConsumerApplication _application;
    private readonly BenzeneServiceBusConfig _config;
    private readonly IServiceBusClientFactory _clientFactory;
    private ServiceBusClient? _client;
    private ServiceBusProcessor? _processor;
    private ServiceBusSessionProcessor? _sessionProcessor;

    /// <summary>
    /// Initializes a new instance of the <see cref="BenzeneServiceBusWorker"/> class.
    /// </summary>
    /// <param name="serviceResolverFactory">The service resolver factory used to process each message.</param>
    /// <param name="application">The application that runs each message through the middleware pipeline.</param>
    /// <param name="config">The entity to consume and the processing behavior to use.</param>
    /// <param name="clientFactory">The factory used to create the underlying Service Bus client.</param>
    public BenzeneServiceBusWorker(IServiceResolverFactory serviceResolverFactory,
        ServiceBusConsumerApplication application, BenzeneServiceBusConfig config,
        IServiceBusClientFactory clientFactory)
    {
        _serviceResolverFactory = serviceResolverFactory;
        _application = application;
        _config = config;
        _clientFactory = clientFactory;
    }

    /// <summary>
    /// Validates the configuration, creates the processor, and starts it. Returns once the
    /// processor is running - it does not block until shutdown. Use <see cref="StopAsync"/> to
    /// stop consuming and wait for in-flight messages to finish.
    /// </summary>
    /// <param name="cancellationToken">The token used to abort startup.</param>
    /// <returns>A task that completes when the processor has started.</returns>
    /// <exception cref="InvalidOperationException">
    /// The configuration doesn't identify exactly one entity - either <see cref="BenzeneServiceBusConfig.QueueName"/>
    /// or both <see cref="BenzeneServiceBusConfig.TopicName"/> and
    /// <see cref="BenzeneServiceBusConfig.SubscriptionName"/> must be set.
    /// </exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Validate(_config);

        _client = _clientFactory.Create();

        if (_config.SessionsEnabled)
        {
            await StartSessionProcessorAsync(cancellationToken);
            return;
        }

        var options = new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = _config.AckMode == ServiceBusConsumerAckMode.AutoComplete,
            MaxConcurrentCalls = _config.MaxConcurrentCalls,
            PrefetchCount = _config.PrefetchCount,
        };

        if (_config.MaxAutoLockRenewalDuration.HasValue)
        {
            options.MaxAutoLockRenewalDuration = _config.MaxAutoLockRenewalDuration.Value;
        }

        _processor = !string.IsNullOrEmpty(_config.QueueName)
            ? _client.CreateProcessor(_config.QueueName, options)
            : _client.CreateProcessor(_config.TopicName!, _config.SubscriptionName!, options);

        _processor.ProcessMessageAsync += OnProcessMessageAsync;
        _processor.ProcessErrorAsync += OnProcessErrorAsync;

        await _processor.StartProcessingAsync(cancellationToken);
    }

    private async Task StartSessionProcessorAsync(CancellationToken cancellationToken)
    {
        // A session processor locks each session to one handler and delivers its messages FIFO;
        // different sessions run concurrently (MaxConcurrentSessions), one message at a time within a
        // session (MaxConcurrentCallsPerSession = 1 by default).
        var options = new ServiceBusSessionProcessorOptions
        {
            AutoCompleteMessages = _config.AckMode == ServiceBusConsumerAckMode.AutoComplete,
            MaxConcurrentSessions = _config.MaxConcurrentSessions,
            MaxConcurrentCallsPerSession = _config.MaxConcurrentCallsPerSession,
            PrefetchCount = _config.PrefetchCount,
        };

        if (_config.MaxAutoLockRenewalDuration.HasValue)
        {
            options.MaxAutoLockRenewalDuration = _config.MaxAutoLockRenewalDuration.Value;
        }

        _sessionProcessor = !string.IsNullOrEmpty(_config.QueueName)
            ? _client!.CreateSessionProcessor(_config.QueueName, options)
            : _client!.CreateSessionProcessor(_config.TopicName!, _config.SubscriptionName!, options);

        _sessionProcessor.ProcessMessageAsync += OnProcessSessionMessageAsync;
        _sessionProcessor.ProcessErrorAsync += OnProcessErrorAsync;

        await _sessionProcessor.StartProcessingAsync(cancellationToken);
    }

    /// <summary>
    /// Stops the processor - waiting for in-flight message handlers to finish - then disposes it.
    /// Does <em>not</em> dispose the <see cref="ServiceBusClient"/> <see cref="IServiceBusClientFactory.Create"/>
    /// returned: unlike the processor (which this worker alone creates and owns), the client is not
    /// necessarily this worker's to close - <c>UseServiceBus(..., healthCheck: true)</c> (the default)
    /// hands the very same factory to the auto-wired dependency health check, which keeps its own
    /// reference to whatever client <c>Create()</c> returns for the life of the app; disposing it here
    /// would break every health-check probe after this worker stops even though the bus itself is
    /// fine. The caller who built the factory owns the client's lifetime - mirroring
    /// <c>Benzene.Azure.EventHub.BenzeneEventHubWorker</c>, which never disposes the client its own
    /// factory returns either.
    /// </summary>
    /// <param name="cancellationToken">The token used to abort the wait for in-flight handlers.</param>
    /// <returns>A task that completes when the processor has stopped and been disposed.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor != null)
        {
            await _processor.StopProcessingAsync(cancellationToken);
            await _processor.DisposeAsync();
            _processor = null;
        }

        if (_sessionProcessor != null)
        {
            await _sessionProcessor.StopProcessingAsync(cancellationToken);
            await _sessionProcessor.DisposeAsync();
            _sessionProcessor = null;
        }

        _client = null;
    }

    private Task OnProcessMessageAsync(ProcessMessageEventArgs args)
        => HandleMessageAsync(new ProcessMessageSettler(args));

    private Task OnProcessSessionMessageAsync(ProcessSessionMessageEventArgs args)
        => HandleMessageAsync(new ProcessSessionMessageSettler(args));

    private async Task HandleMessageAsync(IServiceBusMessageSettler settler)
    {
        if (_config.AckMode == ServiceBusConsumerAckMode.AutoComplete)
        {
            // The processor settles from whether this handler throws: complete on return, abandon
            // on throw (surfacing the exception to OnProcessErrorAsync for logging either way).
            await _application.HandleAsync(settler.Message, _serviceResolverFactory, settler.CancellationToken);
            return;
        }

        ServiceBusSettlementDecision decision;
        try
        {
            decision = await _application.HandleAsync(settler.Message, _serviceResolverFactory, settler.CancellationToken);
        }
        catch (Exception ex)
        {
            // The rethrow surfaces the exception to OnProcessErrorAsync, but that only has the
            // entity/error-source - not which message failed. Log here with the message id so a
            // failure is diagnosable to a specific message, matching the other workers (SQS/Kafka).
            using (var loggingScope = _serviceResolverFactory.CreateScope())
            {
                loggingScope.GetService<ILogger<BenzeneServiceBusWorker>>()
                    .LogError(ex, "Processing Service Bus message {messageId} failed", settler.Message.MessageId);
            }

            // The abandon call is wrapped in its own try/catch so a failure abandoning (e.g. the
            // lock already expired) can never replace the original handler exception below - without
            // this, an exception thrown out of AbandonMessageAsync would propagate from the bare
            // `throw;` in its place, masking the real cause of the failure.
            try
            {
                await settler.AbandonMessageAsync();
            }
            catch (Exception abandonEx)
            {
                using (var loggingScope = _serviceResolverFactory.CreateScope())
                {
                    loggingScope.GetService<ILogger<BenzeneServiceBusWorker>>()
                        .LogError(abandonEx,
                            "Abandoning Service Bus message {messageId} after a processing failure also failed; " +
                            "the message will remain locked until the lock expires and then be redelivered",
                            settler.Message.MessageId);
                }
            }

            throw;
        }

        // Deliberately OUTSIDE the handler's own try/catch above (#277): the handler already
        // completed successfully at this point, so a settlement failure here (e.g. the lock was
        // lost by the time settlement runs, or a transient broker error on the complete/abandon/
        // dead-letter/defer call itself) is a distinct failure mode - it must not be logged with
        // the handler-failure template above, and it must not trigger an abandon on a message that
        // was already correctly and fully processed. Log it distinctly and let the lock's own
        // natural expiry drive redelivery, mirroring the Cosmos/EventHub siblings' "don't force an
        // extra side effect on top of an already-decided-successful outcome" checkpoint handling.
        try
        {
            await SettleAsync(settler, decision);
        }
        catch (Exception ex)
        {
            using (var loggingScope = _serviceResolverFactory.CreateScope())
            {
                loggingScope.GetService<ILogger<BenzeneServiceBusWorker>>()
                    .LogError(ex,
                        "Settling Service Bus message {messageId} failed after it was already successfully " +
                        "processed; the message lock will expire naturally and Service Bus will redeliver it",
                        settler.Message.MessageId);
            }
        }
    }

    private static async Task SettleAsync(IServiceBusMessageSettler settler, ServiceBusSettlementDecision decision)
    {
        // An explicit settlement the handler requested wins; otherwise fall back to the
        // outcome-based default (unsuccessful result → abandon, else complete).
        var settlement = decision.Settlement?.Override;
        if (settlement != null)
        {
            switch (settlement.Value)
            {
                case ServiceBusSettlement.Complete:
                    await settler.CompleteMessageAsync();
                    return;
                case ServiceBusSettlement.Abandon:
                    await settler.AbandonMessageAsync();
                    return;
                case ServiceBusSettlement.DeadLetter:
                    await settler.DeadLetterMessageAsync(decision.Settlement!.DeadLetterReason, decision.Settlement.DeadLetterDescription);
                    return;
                case ServiceBusSettlement.Defer:
                    await settler.DeferMessageAsync();
                    return;
            }
        }

        // Abandon on a failure OR a null result (a pipeline that short-circuited without setting one),
        // completing only on a genuine success - matching the SQS reference's "null errs toward
        // redelivery, never toward silent loss". Previously `== false` completed a null result, dropping
        // a message whose outcome was never established.
        if (decision.MessageResult?.IsSuccessful != true)
        {
            await settler.AbandonMessageAsync();
        }
        else
        {
            await settler.CompleteMessageAsync();
        }
    }

    // Settlement (Complete/Abandon/DeadLetter/Defer) below is deliberately CancellationToken.None on
    // every call, never _args.CancellationToken. Per the WP's settlement-on-shutdown principle: by the
    // time SettleAsync/AbandonMessageAsync runs, the handler has already finished and the outcome is
    // decided - settling it is part of graceful drain, not more handler work. Per the SDK's own docs,
    // _args.CancellationToken "will be cancelled when StopProcessingAsync is called" - and
    // StopProcessingAsync awaits this very in-flight handler rather than cancelling it and moving on,
    // so a settle call gated on that token can be cancelled for a message whose handler already
    // succeeded, silently leaving it unsettled for redelivery/double-processing after the lock expires.
    // CancellationToken.None is used (rather than MessageLockCancellationToken, which fires on lock
    // loss/expiry, not shutdown) so the call is bounded only by the SDK's own operation timeout, not by
    // any cancellation source unrelated to the settle operation itself; a lock genuinely lost by then
    // still fails the call with the SDK's own error, which is logged rather than masked.
    private sealed class ProcessMessageSettler : IServiceBusMessageSettler
    {
        private readonly ProcessMessageEventArgs _args;
        public ProcessMessageSettler(ProcessMessageEventArgs args) => _args = args;
        public ServiceBusReceivedMessage Message => _args.Message;
        public CancellationToken CancellationToken => _args.CancellationToken;
        public Task CompleteMessageAsync() => _args.CompleteMessageAsync(_args.Message, CancellationToken.None);
        public Task AbandonMessageAsync() => _args.AbandonMessageAsync(_args.Message, cancellationToken: CancellationToken.None);
        public Task DeadLetterMessageAsync(string? reason, string? description) => _args.DeadLetterMessageAsync(_args.Message, reason, description, CancellationToken.None);
        public Task DeferMessageAsync() => _args.DeferMessageAsync(_args.Message, cancellationToken: CancellationToken.None);
    }

    private sealed class ProcessSessionMessageSettler : IServiceBusMessageSettler
    {
        private readonly ProcessSessionMessageEventArgs _args;
        public ProcessSessionMessageSettler(ProcessSessionMessageEventArgs args) => _args = args;
        public ServiceBusReceivedMessage Message => _args.Message;
        public CancellationToken CancellationToken => _args.CancellationToken;
        public Task CompleteMessageAsync() => _args.CompleteMessageAsync(_args.Message, CancellationToken.None);
        public Task AbandonMessageAsync() => _args.AbandonMessageAsync(_args.Message, cancellationToken: CancellationToken.None);
        public Task DeadLetterMessageAsync(string? reason, string? description) => _args.DeadLetterMessageAsync(_args.Message, reason, description, CancellationToken.None);
        public Task DeferMessageAsync() => _args.DeferMessageAsync(_args.Message, cancellationToken: CancellationToken.None);
    }

    private Task OnProcessErrorAsync(ProcessErrorEventArgs args)
    {
        using var loggingScope = _serviceResolverFactory.CreateScope();
        loggingScope.GetService<ILogger<BenzeneServiceBusWorker>>()
            .LogError(args.Exception, "Service Bus processing for {entityPath} failed during {errorSource}",
                args.EntityPath, args.ErrorSource);
        return Task.CompletedTask;
    }

    private static void Validate(BenzeneServiceBusConfig config)
    {
        var hasQueue = !string.IsNullOrEmpty(config.QueueName);
        var hasSubscription = !string.IsNullOrEmpty(config.TopicName) && !string.IsNullOrEmpty(config.SubscriptionName);

        if (hasQueue == hasSubscription)
        {
            throw new InvalidOperationException(
                $"{nameof(BenzeneServiceBusConfig)} must identify exactly one entity: set either " +
                $"{nameof(BenzeneServiceBusConfig.QueueName)}, or both {nameof(BenzeneServiceBusConfig.TopicName)} " +
                $"and {nameof(BenzeneServiceBusConfig.SubscriptionName)}.");
        }
    }
}
