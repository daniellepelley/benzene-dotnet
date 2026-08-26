using System.Text;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Hosting;
using Benzene.Kafka.Core.KafkaMessage;
using Benzene.SelfHost;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Benzene.Kafka.Core;

/// <summary>
/// A self-hosted background worker that consumes Kafka records via a Confluent.Kafka
/// <see cref="IConsumer{TKey,TValue}"/> and dispatches them through a <see cref="KafkaApplication{TKey,TValue}"/>
/// pipeline, applying <see cref="BenzeneKafkaConfig"/>'s concurrency, ordering, offset-commit, and
/// dead-lettering behavior.
/// </summary>
public class BenzeneKafkaWorker<TKey, TValue> : IBenzeneWorker, IDisposable
{
    private readonly IServiceResolverFactory _serviceResolverFactory;
    private readonly KafkaApplication<TKey, TValue> _kafkaApplication;
    private readonly BenzeneKafkaConfig _benzeneKafkaConfig;
    private readonly ILogger<BenzeneKafkaWorker<TKey, TValue>> _logger;
    private readonly IKafkaConsumerFactory<TKey, TValue> _consumerFactory;
    private readonly KafkaDeadLetterOptions<TKey, TValue>? _deadLetterOptions;
    private readonly CancellationTokenSource _stoppingCts = new();
    private bool _managesOffsetsManually;
    private IConsumer<TKey, TValue>? _consumer;
    private Task? _runTask;
    private CancellationTokenSource? _linkedCts;

    /// <summary>Initializes a new instance of the <see cref="BenzeneKafkaWorker{TKey,TValue}"/> class.</summary>
    /// <param name="serviceResolverFactory">Creates the per-record DI scope the pipeline runs in.</param>
    /// <param name="kafkaApplication">The pipeline each consumed record is dispatched through.</param>
    /// <param name="benzeneKafkaConfig">The worker's configuration.</param>
    /// <param name="logger">Logs worker lifecycle and per-record failures.</param>
    /// <param name="consumerFactory">Builds the underlying <see cref="IConsumer{TKey,TValue}"/>; defaults to <see cref="KafkaConsumerFactory{TKey,TValue}"/>.</param>
    /// <param name="deadLetterOptions">Dead-letter topic/producer configuration; <c>null</c> disables dead-lettering.</param>
    public BenzeneKafkaWorker(IServiceResolverFactory serviceResolverFactory,
        KafkaApplication<TKey, TValue> kafkaApplication, BenzeneKafkaConfig benzeneKafkaConfig,
        ILogger<BenzeneKafkaWorker<TKey, TValue>> logger,
        IKafkaConsumerFactory<TKey, TValue>? consumerFactory = null,
        KafkaDeadLetterOptions<TKey, TValue>? deadLetterOptions = null)
    {
        _benzeneKafkaConfig = benzeneKafkaConfig;
        _kafkaApplication = kafkaApplication;
        _serviceResolverFactory = serviceResolverFactory;
        _logger = logger;
        _consumerFactory = consumerFactory ?? new KafkaConsumerFactory<TKey, TValue>();
        _deadLetterOptions = deadLetterOptions;

        if (_deadLetterOptions is { DeadLetterTopic: not null } && _deadLetterOptions.Producer == null)
        {
            throw new InvalidOperationException(
                $"{nameof(KafkaDeadLetterOptions<TKey, TValue>)}.{nameof(KafkaDeadLetterOptions<TKey, TValue>.DeadLetterTopic)} " +
                $"is set but {nameof(KafkaDeadLetterOptions<TKey, TValue>.Producer)} is null - dead-lettering needs a caller-built producer.");
        }
    }

    /// <summary>
    /// Starts the consume loop on a background task and returns immediately - it does not wait for
    /// the loop to run to completion. Use <see cref="StopAsync"/> to signal shutdown and wait for
    /// in-flight messages to drain.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var deadLetterEnabled = _deadLetterOptions is { IsEnabled: true };

        if (_benzeneKafkaConfig.CommitOnlyOnSuccess && _benzeneKafkaConfig.CatchHandlerExceptions)
        {
            throw new InvalidOperationException(
                $"{nameof(BenzeneKafkaConfig.CommitOnlyOnSuccess)} requires " +
                $"{nameof(BenzeneKafkaConfig.CatchHandlerExceptions)} = false - otherwise a handler " +
                "exception is swallowed and the message's offset would never be stored, but later, " +
                "successful messages on the same partition would still advance the commit watermark " +
                "past it.");
        }

        // CommitOnlyOnSuccess and dead-lettering both manage offsets by hand - StoreOffset only after a
        // record is genuinely done (handled successfully, or re-produced to the dead-letter topic). Both
        // therefore need auto-store off and per-partition ordering: StoreOffset is a last-write-wins
        // watermark with no gap tracking, so out-of-order handling would let a later record's offset
        // advance the commit watermark past an earlier one that hasn't actually succeeded / been
        // dead-lettered yet (silent loss on the next commit).
        _managesOffsetsManually = _benzeneKafkaConfig.CommitOnlyOnSuccess || deadLetterEnabled;

        // Never mutate the caller's ConsumerConfig instance in place - it may be a shared object the
        // caller reuses elsewhere (e.g. handed to a health check, or a second worker instance), and a
        // surprise EnableAutoOffsetStore flip on someone else's config is exactly the kind of
        // action-at-a-distance bug that's hard to track down. When manual offset management needs
        // EnableAutoOffsetStore = false, apply it to a clone built from the same key/value pairs and
        // hand that clone to the consumer factory instead; the caller's object is left untouched.
        var effectiveConsumerConfig = _benzeneKafkaConfig.ConsumerConfig;
        if (_managesOffsetsManually)
        {
            if (!_benzeneKafkaConfig.PreserveOrderPerPartition)
            {
                var feature = _benzeneKafkaConfig.CommitOnlyOnSuccess
                    ? nameof(BenzeneKafkaConfig.CommitOnlyOnSuccess)
                    : "Dead-lettering";
                throw new InvalidOperationException(
                    $"{feature} requires {nameof(BenzeneKafkaConfig.PreserveOrderPerPartition)} = true - " +
                    "otherwise a partition's messages can be handled out of order, and storing a later " +
                    "message's offset first would advance the commit watermark past an earlier one still " +
                    "in flight.");
            }

            effectiveConsumerConfig = new ConsumerConfig(new Dictionary<string, string>(_benzeneKafkaConfig.ConsumerConfig))
            {
                EnableAutoOffsetStore = false,
            };
        }

        if (_benzeneKafkaConfig.ShouldDrainOnRevoke && _consumerFactory is not KafkaConsumerFactory<TKey, TValue>)
        {
            // The rebalance-drain handler is wired via the IKafkaConsumerFactory.Create(config,
            // configureBuilder) overload (a default-interface method). A custom factory written before
            // that overload existed silently drops the callback, so draining would be a no-op with no
            // other signal - warn loudly instead.
            _logger.LogWarning(
                "{Config}.{DrainOnRevoke} is enabled but the supplied {Factory} does not honor the " +
                "builder-configuration overload (Create(config, configureBuilder)); the partitions-revoked " +
                "drain handler will NOT be registered and draining is disabled. Use the built-in " +
                "{DefaultFactory} or implement the two-argument Create overload.",
                nameof(BenzeneKafkaConfig), nameof(BenzeneKafkaConfig.DrainOnRevoke),
                _consumerFactory.GetType().Name, nameof(KafkaConsumerFactory<TKey, TValue>));
        }

        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stoppingCts.Token);
        var runToken = _linkedCts.Token;

        _runTask = Task.Run(async () =>
        {
            BoundedConcurrentDispatcher<ConsumeResult<TKey, TValue>>? dispatcher = null;

            try
            {
                Func<ConsumeResult<TKey, TValue>, int>? keySelector = _benzeneKafkaConfig.PreserveOrderPerPartition
                    ? consumeResult => consumeResult.Partition.Value
                    : null;

                var handle = BuildHandle(runToken);

                // When dead-lettering is on, the handle catches every handler exception itself (retry
                // then re-produce), so the only thing that can reach the dispatcher is a failure to
                // PRODUCE the dead-letter. That must stop the worker (catchExceptions=false → onFault),
                // because the poison record's offset was deliberately not stored: swallowing it would let
                // the next record on the partition advance the watermark past the lost record.
                var catchHandlerExceptions = !deadLetterEnabled && _benzeneKafkaConfig.CatchHandlerExceptions;

                dispatcher = new BoundedConcurrentDispatcher<ConsumeResult<TKey, TValue>>(
                    _benzeneKafkaConfig.ConcurrentRequests,
                    handle,
                    _logger,
                    keySelector,
                    catchHandlerExceptions,
                    onFault: _ => _stoppingCts.Cancel());

                // The dispatcher must exist before the consumer so the partitions-revoked handler can
                // quiesce the revoked partitions' lanes (see ConfigureRebalanceDrain). The handler runs
                // on this consume thread during Consume(), after _consumer is assigned below. When
                // draining is off there's no builder config to apply, so the original single-arg
                // Create is used - preserving behavior (and custom factories) exactly.
                var configureBuilder = ConfigureRebalanceDrain(dispatcher);
                _consumer = configureBuilder == null
                    ? _consumerFactory.Create(effectiveConsumerConfig)
                    : _consumerFactory.Create(effectiveConsumerConfig, configureBuilder);
                _consumer.Subscribe(_benzeneKafkaConfig.Topics);

                while (!runToken.IsCancellationRequested)
                {
                    try
                    {
                        var consumeResult = _consumer.Consume(runToken);
                        await dispatcher.EnqueueAsync(consumeResult, runToken);
                    }
                    catch (ConsumeException e)
                    {
                        _logger.LogError(e, "Kafka consume error: {Reason}", e.Error.Reason);

                        // A single bad message aside, a persistently failing broker/connection would
                        // otherwise spin this loop as fast as it can fail - back off before retrying.
                        // Cancellable via runToken so shutdown stays responsive during the delay.
                        await Task.Delay(_benzeneKafkaConfig.ConsumeExceptionRetryDelay, runToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown - fall through to drain and close below.
            }
            catch (Exception ex)
            {
                // Anything unexpected here - including consumer/subscribe setup failures, and any
                // KafkaException other than ConsumeException - is logged so the loop's death is
                // visible, rather than leaving the worker silently dead with a faulted, unobserved
                // _runTask. Cleanup below still runs on this path.
                _logger.LogCritical(ex, "Unhandled exception in Kafka consume loop; worker is stopping.");
            }
            finally
            {
                if (dispatcher != null)
                {
                    await dispatcher.DrainAsync(_benzeneKafkaConfig.DrainTimeout);
                }

                _consumer?.Close();
                _consumer?.Dispose();
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds the per-record handler the dispatcher runs, layering (when configured) retry-then-dead-
    /// letter over the base handle, and the manual <c>StoreOffset</c> that <c>CommitOnlyOnSuccess</c>
    /// needs. Dead-lettering catches handler faults itself (retry, then move the record aside) so a
    /// poison record neither wedges the partition nor trips the worker-stopping onFault path; only a
    /// failure to <em>produce</em> the dead-letter propagates.
    /// </summary>
    /// <remarks>
    /// Every path here runs the record through <see cref="HandleRecordAsync"/>, which turns a
    /// non-throwing failure result into a <see cref="KafkaMessageProcessingException"/> when
    /// <see cref="BenzeneKafkaConfig.RaiseOnFailureStatus"/> is on - so a returned failure settles
    /// exactly like a thrown one (dead-lettered, or its offset withheld) instead of being committed as
    /// if it had succeeded. The one exception is the default auto-store configuration, where
    /// Confluent.Kafka stored the offset before the handler even ran: nothing can hold the record back,
    /// so the failure is logged rather than escalated.
    /// </remarks>
    private Func<ConsumeResult<TKey, TValue>, CancellationToken, Task> BuildHandle(CancellationToken runToken)
    {
        var commitOnSuccess = _benzeneKafkaConfig.CommitOnlyOnSuccess;
        var deadLetter = _deadLetterOptions is { IsEnabled: true } ? _deadLetterOptions : null;

        if (deadLetter == null)
        {
            return commitOnSuccess
                ? async (consumeResult, _) =>
                {
                    // A failure result throws out of HandleRecordAsync, so StoreOffset is skipped and the
                    // record is redelivered - the same settlement a thrown exception already gets here.
                    await HandleRecordAsync(consumeResult, runToken);
                    _consumer!.StoreOffset(consumeResult);
                }
                : async (consumeResult, _) =>
                {
                    var messageResult = await _kafkaApplication.HandleAsync(consumeResult, _serviceResolverFactory, runToken);

                    if (_benzeneKafkaConfig.RaiseOnFailureStatus && messageResult?.IsSuccessful == false)
                    {
                        // Auto-offset-store is on, so this record's offset was already stored when
                        // Consume returned it - there is nothing left to withhold. Surface the loss
                        // rather than escalating a failure nothing can act on.
                        _logger.LogWarning(
                            "Kafka handler reported an unsuccessful result ({Status}) for {TopicPartitionOffset}, but the " +
                            "offset was auto-stored before the handler ran so the record cannot be redelivered. Enable " +
                            "{CommitOnlyOnSuccess} or dead-lettering to retain a failed record.",
                            messageResult.Status, consumeResult.TopicPartitionOffset,
                            nameof(BenzeneKafkaConfig.CommitOnlyOnSuccess));
                    }
                };
        }

        // With dead-lettering on, auto-offset-store is off (see StartAsync), so the worker must store
        // the offset itself once a record is genuinely done - handled successfully OR re-produced to the
        // dead-letter topic. A record whose dead-letter PRODUCE fails is never stored (ProduceToDeadLetter
        // stops the worker), so it is redelivered rather than silently dropped.
        var maxAttempts = Math.Max(1, deadLetter.MaxAttempts);
        return async (consumeResult, _) =>
        {
            Exception? lastError = null;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await HandleRecordAsync(consumeResult, runToken);
                    _consumer!.StoreOffset(consumeResult);
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _logger.LogWarning(ex, "Kafka handler failed (attempt {Attempt}/{MaxAttempts}) for {TopicPartitionOffset}",
                        attempt, maxAttempts, consumeResult.TopicPartitionOffset);
                }
            }

            await ProduceToDeadLetterAsync(deadLetter, consumeResult, lastError!, runToken);
            // Advance past the poison record now that it's safely re-produced to the dead-letter topic.
            _consumer!.StoreOffset(consumeResult);
        };
    }

    /// <summary>
    /// Runs one record through the message pipeline and, when
    /// <see cref="BenzeneKafkaConfig.RaiseOnFailureStatus"/> is enabled, escalates a non-throwing
    /// failure result into a <see cref="KafkaMessageProcessingException"/> so the caller settles it on
    /// the same path as a fault. A <c>null</c> result (nothing established an outcome - most commonly
    /// an unrouted record) is deliberately not escalated: Kafka has no per-record dead-letter backstop
    /// of its own, so retaining an unrouted record would replay the partition forever.
    /// </summary>
    private async Task HandleRecordAsync(ConsumeResult<TKey, TValue> consumeResult, CancellationToken runToken)
    {
        var messageResult = await _kafkaApplication.HandleAsync(consumeResult, _serviceResolverFactory, runToken);

        if (_benzeneKafkaConfig.RaiseOnFailureStatus && messageResult?.IsSuccessful == false)
        {
            throw new KafkaMessageProcessingException(consumeResult.Topic, consumeResult.Partition.Value,
                consumeResult.Offset.Value, messageResult.Status);
        }
    }

    /// <summary>
    /// Re-produces the original record to the dead-letter topic with diagnostic <c>x-dlt-*</c> headers
    /// (the failing exception's <em>type name</em> only - never its message, which could carry payload
    /// data), preserving the record's key, value, and original headers.
    /// </summary>
    private async Task ProduceToDeadLetterAsync(KafkaDeadLetterOptions<TKey, TValue> deadLetter,
        ConsumeResult<TKey, TValue> consumeResult, Exception error, CancellationToken cancellationToken)
    {
        var headers = new Headers();
        if (consumeResult.Message.Headers != null)
        {
            foreach (var header in consumeResult.Message.Headers)
            {
                headers.Add(header.Key, header.GetValueBytes());
            }
        }

        headers.Add(KafkaDeadLetterOptions<TKey, TValue>.ReasonHeader, Encoding.UTF8.GetBytes(error.GetType().Name));
        headers.Add(KafkaDeadLetterOptions<TKey, TValue>.OriginalTopicHeader, Encoding.UTF8.GetBytes(consumeResult.Topic));
        headers.Add(KafkaDeadLetterOptions<TKey, TValue>.OriginalPartitionHeader,
            Encoding.UTF8.GetBytes(consumeResult.Partition.Value.ToString()));
        headers.Add(KafkaDeadLetterOptions<TKey, TValue>.OriginalOffsetHeader,
            Encoding.UTF8.GetBytes(consumeResult.Offset.Value.ToString()));

        var message = new Message<TKey, TValue>
        {
            Key = consumeResult.Message.Key,
            Value = consumeResult.Message.Value,
            Headers = headers,
        };

        _logger.LogError(error, "Dead-lettering {TopicPartitionOffset} to {DeadLetterTopic} after {MaxAttempts} attempt(s)",
            consumeResult.TopicPartitionOffset, deadLetter.DeadLetterTopic, Math.Max(1, deadLetter.MaxAttempts));

        try
        {
            await deadLetter.Producer!.ProduceAsync(deadLetter.DeadLetterTopic, message, cancellationToken);
        }
        catch (Exception ex)
        {
            // If we can't even dead-letter the record, do NOT store its offset (the caller only stores
            // after this returns) - stop the worker so the record is redelivered on restart rather than
            // being silently skipped when the next record on the partition advances the watermark. This
            // trades availability for no-loss: a persistently unreachable dead-letter topic wedges the
            // worker (loudly) instead of dropping poison records.
            _logger.LogCritical(ex, "Failed to produce {TopicPartitionOffset} to dead-letter topic {DeadLetterTopic}; " +
                "stopping the worker to avoid losing the record (it will be redelivered on restart).",
                consumeResult.TopicPartitionOffset, deadLetter.DeadLetterTopic);
            _stoppingCts.Cancel();
            throw;
        }
    }

    /// <summary>
    /// Builds the <see cref="ConsumerBuilder{TKey,TValue}"/> configuration that registers the
    /// partitions-revoked AND partitions-lost handlers when <c>DrainOnRevoke</c> is on. The two are
    /// deliberately different:
    /// <list type="bullet">
    /// <item><description>
    /// <b>Revoked</b> (a cooperative, planned handoff - e.g. a rebalance from scaling the group) drains
    /// the revoked partitions' dispatcher lanes (bounded by <see cref="BenzeneKafkaConfig.DrainTimeout"/>)
    /// and commits their stored offsets before releasing them, so no record is committed as done while
    /// still being handled, and none is silently reprocessed by the partition's next owner.
    /// </description></item>
    /// <item><description>
    /// <b>Lost</b> (an involuntary loss - session timeout, a long GC pause, ...) does neither. Per
    /// Confluent.Kafka's own guidance, a lost partition is likely already owned by another consumer in
    /// the group by the time this fires, so committing here would race that consumer's own offsets past
    /// the broker's generation fencing and fail, and waiting out a drain only delays this consumer
    /// rejoining the group for no benefit. Without an explicit lost handler, Confluent.Kafka falls back
    /// to calling the revoked handler above for a loss too - paying its drain wait and then a commit the
    /// broker will reject - which is exactly the bug this handler exists to avoid.
    /// </description></item>
    /// </list>
    /// Returns <c>null</c> when draining is off, so the consumer is built exactly as before (and neither
    /// handler is registered).
    /// </summary>
    private Action<ConsumerBuilder<TKey, TValue>>? ConfigureRebalanceDrain(
        BoundedConcurrentDispatcher<ConsumeResult<TKey, TValue>> dispatcher)
    {
        if (!_benzeneKafkaConfig.ShouldDrainOnRevoke)
        {
            return null;
        }

        return builder =>
        {
            builder.SetPartitionsRevokedHandler((consumer, revoked) => OnPartitionsRevoked(consumer, revoked, dispatcher));
            builder.SetPartitionsLostHandler((consumer, lost) => OnPartitionsLost(consumer, lost));
        };
    }

    /// <summary>
    /// The <c>SetPartitionsRevokedHandler</c> callback: a cooperative, planned handoff (e.g. a
    /// rebalance from scaling the group). Drains the revoked partitions' dispatcher lanes (bounded by
    /// <see cref="BenzeneKafkaConfig.DrainTimeout"/>) and commits their stored offsets before
    /// releasing them, so no record is committed as done while still being handled, and none is
    /// silently reprocessed by the partition's next owner.
    /// </summary>
    /// <remarks>
    /// Internal (rather than a private lambda body) so a test can invoke it directly against a mocked
    /// <see cref="IConsumer{TKey,TValue}"/> - <c>ConsumerBuilder{TKey,TValue}</c> stores registered
    /// handlers on non-public properties, so this is the only way to exercise the callback's actual
    /// logic without a live broker connection. See <c>InternalsVisibleTo</c> in the project file.
    /// </remarks>
    internal void OnPartitionsRevoked(IConsumer<TKey, TValue> consumer, List<TopicPartitionOffset> revoked,
        BoundedConcurrentDispatcher<ConsumeResult<TKey, TValue>> dispatcher)
    {
        try
        {
            var partitions = revoked.Select(tpo => tpo.Partition.Value).ToArray();
            // The revoked handler runs on this consume thread, so no new records are being enqueued
            // while we wait - the in-flight lanes only drain down. Blocking here is expected during a
            // rebalance.
            dispatcher.DrainLanesAsync(partitions, _benzeneKafkaConfig.DrainTimeout).GetAwaiter().GetResult();

            // Only commit when offsets are managed manually (CommitOnlyOnSuccess / dead-letter): there,
            // stored = last genuinely-processed offset per partition, so committing is safe. Under plain
            // auto-store, the stored offset is the consumer's *position* (including records still in
            // flight on OTHER, non-revoked partitions), and a blanket Commit() would mark those
            // in-flight records as done - a silent loss if the worker later crashes. In that mode we
            // only drain (wait for the revoked partitions' handlers to finish) and let the broker's own
            // rebalance commit handle offsets.
            if (_managesOffsetsManually)
            {
                consumer.Commit();
            }

            _logger.LogInformation(
                "Partitions revoked: drained {PartitionCount} partition(s) ({Partitions}); {CommitAction}.",
                partitions.Length, string.Join(",", partitions),
                _managesOffsetsManually ? "committed stored offsets" : "no commit needed (auto-store)");
        }
        catch (KafkaException ex)
        {
            // Nothing stored yet (e.g. no successful record on a revoked partition) surfaces as a "no
            // offset stored" commit error - benign; the partition simply advances no further.
            _logger.LogDebug(ex, "Commit during partition revoke found no stored offsets to commit.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error draining/committing revoked partitions during rebalance.");
        }
    }

    /// <summary>
    /// The <c>SetPartitionsLostHandler</c> callback: an involuntary loss (session timeout, a long GC
    /// pause, ...), deliberately handled differently from <see cref="OnPartitionsRevoked"/> - see
    /// #118. Per Confluent.Kafka's own docs: "If [the partitions lost handler] is not specified, the
    /// partitions revoked handler (if specified) will be called instead if partitions are lost ... The
    /// application should not commit offsets in this case, since the partitions will likely be owned
    /// by other consumers in the group (offset commits to Kafka will likely fail)." So this handler
    /// never commits, and does not drain either - by the time it fires the partitions are likely
    /// already reassigned, so waiting only delays this consumer rejoining the group for no benefit; the
    /// fastest way back to healthy is to return immediately. Without this handler, Confluent.Kafka
    /// falls back to the revoked handler for a loss too - paying its drain wait and then a commit the
    /// broker's generation fencing will reject - which is exactly the bug this handler exists to avoid.
    /// </summary>
    /// <remarks>Internal for the same direct-invocation testing reason as <see cref="OnPartitionsRevoked"/>.</remarks>
    internal void OnPartitionsLost(IConsumer<TKey, TValue> consumer, List<TopicPartitionOffset> lost)
    {
        var partitions = lost.Select(tpo => tpo.Partition.Value).ToArray();
        _logger.LogInformation(
            "Partitions LOST (not revoked): {PartitionCount} partition(s) ({Partitions}) are likely " +
            "already owned by another consumer in the group - skipping drain and commit, rejoining " +
            "immediately.",
            partitions.Length, string.Join(",", partitions));
    }

    /// <summary>
    /// Signals the consume loop to stop, then waits for it to drain in-flight messages
    /// (up to <see cref="BenzeneKafkaConfig.DrainTimeout"/>) and close the consumer.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stoppingCts.Cancel();

        if (_runTask != null)
        {
            await _runTask;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stoppingCts.Dispose();
        _linkedCts?.Dispose();
        _serviceResolverFactory.Dispose();
    }
}
