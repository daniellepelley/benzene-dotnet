using Benzene.Abstractions.DI;
using Benzene.Abstractions.Hosting;
using Benzene.Core;
using Benzene.Kafka.Core;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Benzene.Kafka.Streaming;

/// <summary>
/// A long-running worker that consumes Kafka records, accumulates them into windows, and runs each
/// window through a Benzene <em>streaming</em> pipeline as a single
/// <c>StreamContext&lt;ConsumeResult&lt;TKey,TValue&gt;&gt;</c> with real per-partition offset
/// checkpointing — the windowed/checkpointed counterpart of the per-record
/// <see cref="BenzeneKafkaWorker{TKey,TValue}"/>, and the Kafka counterpart of
/// <c>KinesisStreamApplication</c> (AWS) and <c>BenzeneCosmosChangeFeedWorker</c> (Azure).
/// </summary>
/// <remarks>
/// <para>Same self-hosted lifecycle contract as its siblings: <see cref="StartAsync"/> starts the
/// consume loop on a background task and returns immediately; <see cref="StopAsync"/> signals
/// shutdown and waits (bounded by <see cref="BenzeneKafkaConfig.DrainTimeout"/>) for the loop to
/// finish and close the consumer.</para>
/// <para>Unlike Kinesis (where AWS's event source mapping hands Lambda a batch) and Cosmos (where
/// the SDK's Change Feed Processor delivers one), Kafka has no batch trigger: the loop polls
/// <c>Consume</c> itself and flushes the accumulated batch when <em>either</em>
/// <see cref="KafkaStreamOptions.MaxBatchSize"/> records have arrived <em>or</em>
/// <see cref="KafkaStreamOptions.MaxBatchWait"/> has elapsed since the batch's first record.</para>
/// <para>Offsets are always managed by hand here — <c>EnableAutoOffsetStore</c> is forced off at
/// startup and an offset only ever advances through <see cref="KafkaStreamCheckpointer{TKey,TValue}"/>,
/// i.e. only for records the pipeline has genuinely processed. The per-record worker's
/// <c>CommitOnlyOnSuccess</c> knob has no counterpart here because that behavior is unconditional.</para>
/// </remarks>
/// <typeparam name="TKey">The consumer's key type.</typeparam>
/// <typeparam name="TValue">The consumer's value type.</typeparam>
public class BenzeneKafkaStreamWorker<TKey, TValue> : IBenzeneWorker, IDisposable
{
    private readonly IServiceResolverFactory _serviceResolverFactory;
    private readonly KafkaStreamApplication<TKey, TValue> _application;
    private readonly BenzeneKafkaConfig _benzeneKafkaConfig;
    private readonly KafkaStreamOptions _options;
    private readonly ILogger<BenzeneKafkaStreamWorker<TKey, TValue>> _logger;
    private readonly IKafkaConsumerFactory<TKey, TValue> _consumerFactory;
    private readonly CancellationTokenSource _stoppingCts = new();
    private IConsumer<TKey, TValue>? _consumer;
    private Task? _runTask;
    private CancellationTokenSource? _linkedCts;

    /// <summary>
    /// Initializes a new instance of the <see cref="BenzeneKafkaStreamWorker{TKey,TValue}"/> class.
    /// </summary>
    /// <param name="serviceResolverFactory">The service resolver factory used to process each batch.</param>
    /// <param name="application">The application that runs each batch through the streaming pipeline.</param>
    /// <param name="benzeneKafkaConfig">
    /// The Kafka connection configuration, shared with <see cref="BenzeneKafkaWorker{TKey,TValue}"/>.
    /// Only <c>ConsumerConfig</c>, <c>Topics</c>, <c>DrainTimeout</c> and
    /// <c>ConsumeExceptionRetryDelay</c> apply here — the per-record dispatch knobs
    /// (<c>ConcurrentRequests</c>, <c>PreserveOrderPerPartition</c>, <c>CatchHandlerExceptions</c>,
    /// <c>CommitOnlyOnSuccess</c>, <c>DrainOnRevoke</c>) describe a fan-out model this worker
    /// doesn't use; see <see cref="KafkaStreamOptions"/> for the streaming equivalents.
    /// </param>
    /// <param name="options">The windowing/checkpointing behavior. Defaults to a new <see cref="KafkaStreamOptions"/>.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="consumerFactory">
    /// Optionally supplies the underlying <c>IConsumer</c> — the same seam
    /// <see cref="BenzeneKafkaWorker{TKey,TValue}"/> uses, for deserializers, handlers, or
    /// <c>SetOAuthBearerTokenRefreshHandler</c>. Defaults to <see cref="KafkaConsumerFactory{TKey,TValue}"/>.
    /// </param>
    public BenzeneKafkaStreamWorker(IServiceResolverFactory serviceResolverFactory,
        KafkaStreamApplication<TKey, TValue> application, BenzeneKafkaConfig benzeneKafkaConfig,
        KafkaStreamOptions? options, ILogger<BenzeneKafkaStreamWorker<TKey, TValue>> logger,
        IKafkaConsumerFactory<TKey, TValue>? consumerFactory = null)
    {
        _serviceResolverFactory = serviceResolverFactory;
        _application = application;
        _benzeneKafkaConfig = benzeneKafkaConfig;
        _options = options ?? new KafkaStreamOptions();
        _logger = logger;
        _consumerFactory = consumerFactory ?? new KafkaConsumerFactory<TKey, TValue>();
    }

    /// <summary>
    /// Validates the options, forces manual offset storage, then starts the batching consume loop on
    /// a background task and returns immediately — it does not wait for the loop to run to
    /// completion. Use <see cref="StopAsync"/> to signal shutdown and wait for it to finish.
    /// </summary>
    /// <param name="cancellationToken">Linked into the loop's own stopping token.</param>
    /// <returns>A task that completes once the loop has been started.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A <see cref="KafkaStreamOptions"/> value is out of range.</exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _options.Validate();

        // Streaming always manages offsets by hand: nothing advances an offset except the batch's
        // checkpointer, and it only ever does so for records the pipeline has processed. Leaving
        // Confluent.Kafka's auto-store on would store an offset the instant Consume returned a
        // record - i.e. before the batch had even been assembled, let alone handled - which would
        // silently commit past every record lost to a crash or a failed batch.
        _benzeneKafkaConfig.ConsumerConfig.EnableAutoOffsetStore = false;

        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stoppingCts.Token);
        var runToken = _linkedCts.Token;

        _runTask = Task.Run(() => RunAsync(runToken), CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Signals the consume loop to stop, then waits (bounded by
    /// <see cref="BenzeneKafkaConfig.DrainTimeout"/>) for the in-flight batch to finish and the
    /// consumer to close.
    /// </summary>
    /// <param name="cancellationToken">Unobserved — shutdown is bounded by <c>DrainTimeout</c> instead.</param>
    /// <returns>A task that completes when the loop has stopped (or the drain timeout has elapsed).</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stoppingCts.Cancel();

        if (_runTask == null)
        {
            return;
        }

        try
        {
            await _runTask.WaitAsync(_benzeneKafkaConfig.DrainTimeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("The Kafka stream worker did not finish its in-flight batch within the {DrainTimeout} drain timeout; " +
                "abandoning it. Its uncheckpointed records are redelivered on restart.", _benzeneKafkaConfig.DrainTimeout);
        }
    }

    private async Task RunAsync(CancellationToken runToken)
    {
        try
        {
            _consumer = _consumerFactory.Create(_benzeneKafkaConfig.ConsumerConfig, ConfigureRebalanceCommit());
            _consumer.Subscribe(_benzeneKafkaConfig.Topics);

            while (!runToken.IsCancellationRequested)
            {
                var records = await CollectBatchAsync(runToken);

                if (records.Count == 0)
                {
                    continue;
                }

                if (runToken.IsCancellationRequested)
                {
                    // Shutting down with a partially-filled batch. Nothing has been checkpointed for
                    // these records, so abandoning them redelivers them on restart - the same call
                    // BenzeneCosmosChangeFeedWorker makes when a batch is cancelled mid-flight:
                    // never acknowledge work that wasn't done.
                    _logger.LogInformation("Shutdown requested with {RecordCount} unflushed Kafka record(s); " +
                        "abandoning them - they are uncheckpointed and will be redelivered.", records.Count);
                    break;
                }

                await FlushAsync(records, runToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown - fall through to close the consumer below.
        }
        catch (Exception ex)
        {
            // Anything unexpected - including consumer/subscribe setup failures - is logged so the
            // loop's death is visible, rather than leaving the worker silently dead with a faulted,
            // unobserved _runTask. Mirrors BenzeneKafkaWorker.
            _logger.LogCritical(ex, "Unhandled exception in the Kafka stream consume loop; worker is stopping.");
        }
        finally
        {
            _consumer?.Close();
            _consumer?.Dispose();
        }
    }

    /// <summary>
    /// Accumulates the next batch, returning once <see cref="KafkaStreamOptions.MaxBatchSize"/>
    /// records have arrived, <see cref="KafkaStreamOptions.MaxBatchWait"/> has elapsed since the
    /// batch's <em>first</em> record, or shutdown is requested. Returns an empty list when the poll
    /// window expired with nothing to show for it.
    /// </summary>
    private async Task<List<ConsumeResult<TKey, TValue>>> CollectBatchAsync(CancellationToken runToken)
    {
        var batch = new List<ConsumeResult<TKey, TValue>>();
        DateTime? deadline = null;

        while (!runToken.IsCancellationRequested && batch.Count < _options.MaxBatchSize)
        {
            var pollTimeout = _options.PollTimeout;

            if (deadline.HasValue)
            {
                var remaining = deadline.Value - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                // Shorten the last poll of a batch so it lands exactly on the deadline rather than
                // overshooting it by up to a whole PollTimeout.
                if (remaining < pollTimeout)
                {
                    pollTimeout = remaining;
                }
            }

            ConsumeResult<TKey, TValue>? result;

            try
            {
                // The timeout overload (rather than Consume(CancellationToken)) is what makes a
                // time-triggered window possible at all: a token-based Consume blocks until a record
                // arrives, which can't honor a batch deadline. PollTimeout also caps how long the
                // loop can sit inside Consume without noticing a shutdown request.
                result = _consumer!.Consume(pollTimeout);
            }
            catch (ConsumeException ex)
            {
                _logger.LogError(ex, "Kafka consume error while filling a stream batch: {Reason}", ex.Error.Reason);

                if (batch.Count > 0)
                {
                    // Flush what we already have rather than holding it while the broker misbehaves -
                    // records already in hand shouldn't inherit the retry delay.
                    break;
                }

                // Nothing in hand: back off before retrying so a persistently failing broker can't
                // spin this loop, exactly as BenzeneKafkaWorker does.
                await Task.Delay(_benzeneKafkaConfig.ConsumeExceptionRetryDelay, runToken);
                continue;
            }

            if (result == null || result.IsPartitionEOF)
            {
                // Poll window expired, or a partition-EOF marker that carries no message. Loop round:
                // the deadline/token checks above decide whether to keep waiting.
                continue;
            }

            batch.Add(result);

            // The window's clock starts at its FIRST record and is not extended by later arrivals,
            // so no record is ever buffered for longer than MaxBatchWait. See KafkaStreamOptions.
            deadline ??= DateTime.UtcNow + _options.MaxBatchWait;
        }

        return batch;
    }

    /// <summary>
    /// Runs one accumulated batch through the streaming pipeline and settles its offsets: commit
    /// what was checkpointed, auto-checkpoint a clean run that checkpointed nothing, and apply the
    /// configured skip-or-retry policy when the pipeline throws.
    /// </summary>
    private async Task FlushAsync(IReadOnlyList<ConsumeResult<TKey, TValue>> records, CancellationToken runToken)
    {
        var checkpointer = new KafkaStreamCheckpointer<TKey, TValue>(_consumer!, records, _logger);
        var batch = new KafkaStreamBatch<TKey, TValue>(records, checkpointer, runToken);

        try
        {
            // The run token is seeded into the batch's DI scope (ICancellationTokenAccessor) as well
            // as riding on the StreamContext, matching BenzeneKafkaWorker's per-record dispatch.
            var handlerCheckpointed = await _application.HandleAsync(batch, _serviceResolverFactory, runToken);

            if (!handlerCheckpointed && _options.AutoCheckpointOnSuccess)
            {
                checkpointer.CheckpointAll();
            }

            // A handler that checkpointed only part of the batch and returned successfully keeps
            // exactly that: the uncheckpointed tail is not committed, and is redelivered after a
            // restart or rebalance. Same contract as Cosmos's manual-checkpoint mode - a successful
            // return is not, by itself, a reason to rewind and re-run.
            checkpointer.Commit();
        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested)
        {
            // Shutdown cancelled the batch mid-flight. Commit only what the handler explicitly
            // checkpointed - never auto-checkpoint a partially-processed batch, which would silently
            // lose its unprocessed tail.
            checkpointer.Commit();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BenzeneFailure.IsInfrastructure(ex)
                ? BenzeneFailure.InfrastructureLogPrefix + " Processing a Kafka stream batch of {RecordCount} record(s) failed — this service is mis-wired; the message is not at fault"
                : "Processing a Kafka stream batch of {RecordCount} record(s) failed", records.Count);

            if (_options.CatchHandlerExceptions)
            {
                // Skip policy: acknowledge the whole batch anyway so the poison window is permanently
                // passed over and the partitions keep moving.
                checkpointer.CheckpointAll();
                checkpointer.Commit();
                return;
            }

            // Retry policy (default): keep whatever the handler managed to checkpoint, then rewind
            // each partition to its own first unprocessed record so the tail is re-consumed and
            // retried. This is the piece Kinesis structurally can't do - its resume point is one
            // sequence number for the whole batch - and it's why a Kafka batch spanning partitions
            // can resume mid-partition: seeking is per partition.
            checkpointer.Commit();
            checkpointer.SeekToResumeOffsets();
            await Task.Delay(_options.FailedBatchRetryDelay, runToken);
        }
    }

    /// <summary>
    /// Builds the consumer-builder step that commits already-checkpointed offsets when partitions
    /// are revoked, so the partition's next owner resumes from what this worker actually processed
    /// instead of re-running it.
    /// </summary>
    /// <remarks>
    /// Committing here is safe precisely because auto-offset-store is off: the consumer's stored
    /// offsets are the checkpointer's watermarks, never its raw read position. The in-progress batch
    /// is deliberately <em>not</em> flushed during a revoke — doing so would run a whole pipeline
    /// inside the rebalance callback and risk blowing <c>max.poll.interval.ms</c>; instead its
    /// records are simply redelivered to the partition's next owner (at-least-once, at the cost of
    /// repeating that window's work). Only wired when the consumer factory honors the
    /// <c>Create(config, configureBuilder)</c> overload; a factory that doesn't just loses the
    /// early commit, not correctness.
    /// </remarks>
    private Action<ConsumerBuilder<TKey, TValue>> ConfigureRebalanceCommit()
    {
        return builder => builder.SetPartitionsRevokedHandler((consumer, _) =>
        {
            try
            {
                consumer.Commit();
            }
            catch (KafkaException ex)
            {
                // "No offset stored" when nothing in this batch has been checkpointed yet - benign;
                // the partition simply advances no further.
                _logger.LogDebug(ex, "Committing stored Kafka offsets during a partition revoke found nothing to commit.");
            }
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stoppingCts.Dispose();
        _linkedCts?.Dispose();
        _serviceResolverFactory.Dispose();
        GC.SuppressFinalize(this);
    }
}
