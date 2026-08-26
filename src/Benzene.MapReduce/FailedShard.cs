namespace Benzene.MapReduce;

/// <summary>
/// One shard that failed during a scatter-gather run, paired with why — the exception the worker
/// call threw, or <c>null</c> when the worker returned a structurally unsuccessful
/// <see cref="Benzene.Abstractions.Results.IBenzeneResult{T}"/> rather than throwing.
/// </summary>
/// <typeparam name="TShard">The shard/work-unit type.</typeparam>
public readonly struct FailedShard<TShard>
{
    /// <summary>Initializes a failed-shard record.</summary>
    /// <param name="shard">The shard that failed.</param>
    /// <param name="reason">
    /// The exception the worker call threw, or <c>null</c> when it instead returned an unsuccessful
    /// result (no exception to carry).
    /// </param>
    public FailedShard(TShard shard, Exception? reason)
    {
        Shard = shard;
        Reason = reason;
    }

    /// <summary>The shard that failed.</summary>
    public TShard Shard { get; }

    /// <summary>
    /// Why it failed: the exception the worker call threw, or <c>null</c> when the worker returned an
    /// unsuccessful result rather than throwing.
    /// </summary>
    public Exception? Reason { get; }
}
