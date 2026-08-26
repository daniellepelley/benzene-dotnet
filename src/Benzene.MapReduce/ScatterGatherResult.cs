using System.Collections.Generic;
using System.Linq;

namespace Benzene.MapReduce;

/// <summary>
/// The outcome of a scatter-gather run: the reduced value plus any shards that failed (populated only
/// under <see cref="PartialFailureMode.BestEffort"/>).
/// </summary>
/// <typeparam name="TShard">The shard/work-unit type.</typeparam>
/// <typeparam name="TAccum">The reduced accumulator type.</typeparam>
public class ScatterGatherResult<TShard, TAccum>
{
    /// <summary>Initializes a new instance of the <see cref="ScatterGatherResult{TShard, TAccum}"/> class.</summary>
    /// <param name="value">The reduced value over the successful shards.</param>
    /// <param name="failedShards">The shards that failed, each paired with why (empty when every shard succeeded).</param>
    public ScatterGatherResult(TAccum value, IReadOnlyList<FailedShard<TShard>> failedShards)
    {
        Value = value;
        FailedShards = failedShards;
    }

    /// <summary>The reduced value, folded over the shards that succeeded.</summary>
    public TAccum Value { get; }

    /// <summary>
    /// The shards that failed, each paired with the exception the worker call threw (<c>null</c> when
    /// it instead returned an unsuccessful result). Empty when coverage was complete.
    /// </summary>
    public IReadOnlyList<FailedShard<TShard>> FailedShards { get; }

    /// <summary>Whether every shard succeeded (the reduced value covers the whole input).</summary>
    public bool IsComplete => !FailedShards.Any();
}
