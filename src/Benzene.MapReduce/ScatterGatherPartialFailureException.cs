using System;

namespace Benzene.MapReduce;

/// <summary>
/// Thrown by a <see cref="PartialFailureMode.ThrowOnAnyFailure"/> scatter-gather run when one or more
/// shards failed, so an incomplete result is never mistaken for a complete one.
/// </summary>
public class ScatterGatherPartialFailureException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ScatterGatherPartialFailureException"/> class.</summary>
    /// <param name="failedShardCount">How many shards failed.</param>
    /// <param name="totalShardCount">How many shards were dispatched.</param>
    public ScatterGatherPartialFailureException(int failedShardCount, int totalShardCount)
        : base($"{failedShardCount} of {totalShardCount} scatter-gather shard(s) failed; the reduced result would be incomplete.")
    {
        FailedShardCount = failedShardCount;
        TotalShardCount = totalShardCount;
    }

    /// <summary>How many shards failed.</summary>
    public int FailedShardCount { get; }

    /// <summary>How many shards were dispatched.</summary>
    public int TotalShardCount { get; }
}
