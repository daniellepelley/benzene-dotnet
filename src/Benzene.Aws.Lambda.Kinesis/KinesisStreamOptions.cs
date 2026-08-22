namespace Benzene.Aws.Lambda.Kinesis;

/// <summary>
/// Configures how <see cref="KinesisStreamApplication"/> checkpoints a Kinesis batch.
/// </summary>
public class KinesisStreamOptions
{
    /// <summary>
    /// When <c>true</c> (the default), a batch whose pipeline completes without throwing and whose
    /// handler never checkpointed anything itself is checkpointed to the end - so a fully-processed
    /// batch advances its resume point instead of being redelivered by Kinesis forever (the
    /// <c>UseStream((records, ct) =&gt; ...)</c> callback overload never checkpoints on its own). Set
    /// <c>false</c> to leave the resume point at exactly what the handler explicitly checkpointed even
    /// on success (full manual control). Auto-checkpoint never runs when the pipeline throws - the
    /// resume point then stays at the handler's last explicit checkpoint, the correct Kinesis
    /// shard-ordered retry signal. Mirrors Cosmos's <c>AutoCheckpointOnSuccess</c>.
    /// </summary>
    public bool AutoCheckpointOnSuccess { get; set; } = true;

    /// <summary>
    /// Gets or sets whether an exception from the stream pipeline is caught (logged, and the batch
    /// response returned with the resume point computed from whatever the handler had checkpointed
    /// before failing) instead of left to cascade out of the Lambda invocation. Defaults to
    /// <c>true</c> - unlike the fan-out transports' <c>CatchExceptions</c> (which defaults
    /// <c>false</c>: an uncaught exception is the safer default there), Kinesis's checkpointer resume
    /// point already <em>is</em> the correct failure signal for the shard-ordered retry contract, so
    /// catching and still returning a real response loses no information and is the safer default
    /// here - see
    /// <c>work/archive/kinesis-batch-failure-handling-design-2026-07.md</c> §3.3. Set <c>false</c> to
    /// let the exception cascade and fail the whole invocation instead (losing the partial-resume
    /// information in the response, but matching the fan-out transports' opt-out shape) if your
    /// deployment's monitoring/alerting depends on invocation failures rather than the batch response
    /// or logs.
    /// </summary>
    public bool CatchExceptions { get; set; } = true;
}
