namespace Benzene.Kafka.Streaming;

/// <summary>
/// Configures how <see cref="BenzeneKafkaStreamWorker{TKey,TValue}"/> batches consumed records into
/// windows, checkpoints them, and reacts to a batch whose pipeline throws. The broker connection,
/// topics, drain timeout, and consume-error backoff come from
/// <see cref="Benzene.Kafka.Core.BenzeneKafkaConfig"/> (shared with the per-record
/// <c>BenzeneKafkaWorker</c>) — this type only covers what the streaming binding itself decides.
/// </summary>
/// <remarks>
/// Shaped to match <c>KinesisStreamOptions</c> and <c>BenzeneCosmosChangeFeedConfig</c>:
/// <see cref="AutoCheckpointOnSuccess"/> and <see cref="CatchHandlerExceptions"/> carry the same
/// meanings (and the same defaults) they do there, so a handler ports between the three streaming
/// transports without relearning the failure model.
/// </remarks>
public class KafkaStreamOptions
{
    /// <summary>
    /// The maximum number of records in one batch. Reaching this flushes the batch immediately,
    /// without waiting for <see cref="MaxBatchWait"/>. Defaults to 500 — large enough that a busy
    /// topic amortizes the per-batch pipeline/DI-scope cost, small enough that a batch stays a
    /// comfortable in-memory working set. Must be at least 1.
    /// </summary>
    public int MaxBatchSize { get; set; } = 500;

    /// <summary>
    /// The maximum time a record waits in the batch before the batch is flushed, even if it holds
    /// fewer than <see cref="MaxBatchSize"/> records. Defaults to 1 second.
    /// </summary>
    /// <remarks>
    /// <para><strong>This is a per-batch deadline measured from the batch's <em>first</em> record,
    /// not a rolling idle timer.</strong> The clock starts when the first record of an otherwise
    /// empty batch arrives and is not extended by later arrivals, so the property it guarantees is
    /// "no record is buffered for longer than <c>MaxBatchWait</c> before its batch is flushed" — a
    /// hard bound on added processing latency.</para>
    /// <para>The rejected alternative, a rolling "flush once no new record has arrived for
    /// <c>MaxBatchWait</c>" idle timer, bounds nothing under sustained load: a topic delivering a
    /// record every few milliseconds would never go idle, so the batch would only ever flush on
    /// <see cref="MaxBatchSize"/> and the oldest record's latency would be unbounded — the opposite
    /// of what a latency-sensitive aggregator needs. A first-record deadline degrades gracefully
    /// instead: under load it flushes on size, when quiet it flushes on age.</para>
    /// </remarks>
    public TimeSpan MaxBatchWait { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a single <c>Consume</c> poll blocks while a batch is filling. The batch deadline is
    /// still honored exactly (the last poll of a batch is shortened to land on it); this only caps
    /// how long the loop can be inside <c>Consume</c> without noticing a shutdown request, because
    /// Confluent.Kafka's timeout-based <c>Consume</c> overload takes no cancellation token.
    /// Defaults to 250ms. Lower it for faster shutdown, raise it to poll less often.
    /// </summary>
    public TimeSpan PollTimeout { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// When <c>true</c> (the default), a batch whose pipeline completes without throwing and whose
    /// handler never checkpointed anything itself is checkpointed to the end — every partition in
    /// the batch advances to its last record — so a fully-processed batch commits instead of being
    /// redelivered forever (the <c>UseStream((records, ct) =&gt; ...)</c> callback overload exposes
    /// no checkpointer, so it never checkpoints on its own). A handler that manages its own
    /// checkpoints is left untouched. Set <c>false</c> for fully manual control: only what the
    /// handler explicitly checkpointed is committed, and everything past it is redelivered. Never
    /// runs on the throwing path. Mirrors <c>KinesisStreamOptions.AutoCheckpointOnSuccess</c> and
    /// <c>BenzeneCosmosChangeFeedConfig.AutoCheckpointOnSuccess</c>.
    /// </summary>
    public bool AutoCheckpointOnSuccess { get; set; } = true;

    /// <summary>
    /// Gets or sets whether an unhandled exception from the batch's pipeline permanently skips the
    /// batch instead of retrying it. Defaults to <c>false</c> (retry): whatever the handler
    /// checkpointed is still committed, then every partition in the batch is <c>Seek</c>ed back to
    /// the record after its checkpoint, so the unprocessed tail is re-consumed and the batch is
    /// retried after <see cref="FailedBatchRetryDelay"/> — at-least-once, and a reliably-failing
    /// batch retries forever. Set <c>true</c> to instead log, checkpoint the batch to the end
    /// anyway, and move on, permanently passing the poison batch over.
    /// </summary>
    /// <remarks>
    /// The default matches <c>BenzeneCosmosChangeFeedConfig.CatchHandlerExceptions</c> (also
    /// <c>false</c>) for the same reason: Kafka, like the Cosmos change feed, can genuinely
    /// redeliver, so no-loss is the honest default and skipping should be opted into. Note the
    /// per-record <c>BenzeneKafkaConfig.CatchHandlerExceptions</c> defaults the other way
    /// (<c>true</c>) — that worker has no checkpoint watermark to seek back to, so catching is its
    /// only way to keep a partition moving. This option is deliberately separate from it: the two
    /// workers make different offset promises and must not share a knob.
    /// </remarks>
    public bool CatchHandlerExceptions { get; set; }

    /// <summary>
    /// How long to wait after a failed batch before re-consuming it, under the retry policy
    /// (<see cref="CatchHandlerExceptions"/> = <c>false</c>). Without a delay a batch that fails
    /// immediately — a downstream outage, say — would be seeked back and retried as fast as the
    /// pipeline can throw, burning CPU and flooding the log. Defaults to 1 second, matching
    /// <c>BenzeneKafkaConfig.ConsumeExceptionRetryDelay</c>'s rationale on the consume side.
    /// </summary>
    public TimeSpan FailedBatchRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Throws if any option is outside its supported range. Called by the worker at
    /// <c>StartAsync</c>, so a mis-configured window fails loudly at boot rather than producing a
    /// silently degenerate batching loop.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public void Validate()
    {
        if (MaxBatchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBatchSize), MaxBatchSize,
                $"{nameof(KafkaStreamOptions)}.{nameof(MaxBatchSize)} must be at least 1.");
        }

        if (MaxBatchWait <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBatchWait), MaxBatchWait,
                $"{nameof(KafkaStreamOptions)}.{nameof(MaxBatchWait)} must be greater than zero.");
        }

        if (PollTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PollTimeout), PollTimeout,
                $"{nameof(KafkaStreamOptions)}.{nameof(PollTimeout)} must be greater than zero.");
        }

        if (FailedBatchRetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(FailedBatchRetryDelay), FailedBatchRetryDelay,
                $"{nameof(KafkaStreamOptions)}.{nameof(FailedBatchRetryDelay)} cannot be negative.");
        }
    }
}
