using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Benzene.MapReduce;

/// <summary>
/// Thrown by a <see cref="PartialFailureMode.ThrowOnAnyFailure"/> scatter-gather run when one or more
/// shards failed, so an incomplete result is never mistaken for a complete one. Carries every failed
/// shard's own reason (<see cref="Failures"/>) - both individually and aggregated into
/// <see cref="System.Exception.InnerException"/> as an <see cref="System.AggregateException"/> - so
/// which shard failed and why is diagnosable from the thrown exception alone, not just the count.
/// </summary>
public class ScatterGatherPartialFailureException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ScatterGatherPartialFailureException"/> class.</summary>
    /// <param name="failedShardCount">How many shards failed.</param>
    /// <param name="totalShardCount">How many shards were dispatched.</param>
    /// <param name="failures">
    /// Every failed shard paired with the exception its worker call threw (<c>null</c> when it instead
    /// returned an unsuccessful result rather than throwing).
    /// </param>
    public ScatterGatherPartialFailureException(
        int failedShardCount,
        int totalShardCount,
        IReadOnlyList<(object? Shard, Exception? Reason)> failures)
        : base(BuildMessage(failedShardCount, totalShardCount, failures), BuildInnerException(failures))
    {
        FailedShardCount = failedShardCount;
        TotalShardCount = totalShardCount;
        Failures = failures;
    }

    /// <summary>How many shards failed.</summary>
    public int FailedShardCount { get; }

    /// <summary>How many shards were dispatched.</summary>
    public int TotalShardCount { get; }

    /// <summary>
    /// Every failed shard paired with the exception its worker call threw (<c>null</c> when it instead
    /// returned an unsuccessful result). The same per-shard reasons carried, individually, by this
    /// exception's <see cref="System.Exception.InnerException"/> (an <see cref="System.AggregateException"/>
    /// over the non-null reasons) - use this property when the failing shard's identity matters, not
    /// just its exception.
    /// </summary>
    public IReadOnlyList<(object? Shard, Exception? Reason)> Failures { get; }

    private static Exception? BuildInnerException(IReadOnlyList<(object? Shard, Exception? Reason)> failures)
    {
        var reasons = failures.Where(f => f.Reason != null).Select(f => f.Reason!).ToList();
        return reasons.Count > 0 ? new AggregateException(reasons) : null;
    }

    private static string BuildMessage(
        int failedShardCount,
        int totalShardCount,
        IReadOnlyList<(object? Shard, Exception? Reason)> failures)
    {
        var message = new StringBuilder()
            .Append(failedShardCount)
            .Append(" of ")
            .Append(totalShardCount)
            .Append(" scatter-gather shard(s) failed; the reduced result would be incomplete.");

        foreach (var (shard, reason) in failures)
        {
            message.Append(" [shard=").Append(shard)
                .Append(": ")
                .Append(reason == null ? "unsuccessful result" : $"{reason.GetType().Name}: {reason.Message}")
                .Append(']');
        }

        return message.ToString();
    }
}
