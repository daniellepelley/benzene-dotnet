namespace Benzene.MapReduce;

/// <summary>
/// How a scatter-gather run treats shards that fail (a worker that returns an unsuccessful result or
/// throws).
/// </summary>
public enum PartialFailureMode
{
    /// <summary>
    /// If any shard fails, throw <see cref="ScatterGatherPartialFailureException"/> once every shard
    /// has completed — the combined result is invalid unless every shard succeeded (e.g. a regulatory
    /// total that must be complete).
    /// </summary>
    ThrowOnAnyFailure,

    /// <summary>
    /// Reduce over the successful shards and return, exposing the failed shards on the result so a
    /// reduced-coverage answer can say so rather than silently dropping work.
    /// </summary>
    BestEffort
}

/// <summary>
/// Tuning for a scatter-gather run.
/// </summary>
public class ScatterGatherOptions
{
    /// <summary>
    /// The maximum number of shards dispatched concurrently. <c>null</c> (the default) is unbounded —
    /// set a cap to avoid opening thousands of simultaneous worker invocations.
    /// </summary>
    public int? MaxDegreeOfParallelism { get; set; }

    /// <summary>
    /// How to treat failed shards. Defaults to <see cref="PartialFailureMode.ThrowOnAnyFailure"/>.
    /// </summary>
    public PartialFailureMode PartialFailureMode { get; set; } = PartialFailureMode.ThrowOnAnyFailure;
}
