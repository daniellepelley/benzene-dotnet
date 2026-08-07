using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Benzene.Clients;
using Benzene.Core.Middleware;

namespace Benzene.MapReduce;

/// <summary>
/// Scatter-gather (map-reduce) over Benzene's topic-routed message sender: dispatch one worker call
/// per shard concurrently (the map), then fold their partial results into one answer (the reduce).
/// </summary>
/// <remarks>
/// Benzene ships no dedicated scatter-gather primitive because it composes from parts it already has
/// — this is the thin, supported form of that composition: a bounded parallel fan-out
/// (<see cref="BoundedFanOut"/>) over <see cref="IBenzeneMessageSender.SendAsync{TRequest,TResponse}"/>,
/// plus an app-owned fold. Workers resolve through the routing table, so on AWS each shard is a
/// Lambda-to-Lambda invoke.
/// </remarks>
public static class ScatterGatherExtensions
{
    /// <summary>
    /// Sends one message per shard to <paramref name="topic"/> concurrently and reduces the successful
    /// partial responses into a single value.
    /// </summary>
    /// <typeparam name="TShard">The work-unit sent to each worker.</typeparam>
    /// <typeparam name="TPartial">The worker's partial response.</typeparam>
    /// <typeparam name="TAccum">The reduced accumulator.</typeparam>
    /// <param name="sender">The message sender the shards are dispatched through.</param>
    /// <param name="topic">The worker topic each shard is sent to.</param>
    /// <param name="shards">The work units to scatter.</param>
    /// <param name="seed">The initial accumulator value.</param>
    /// <param name="reduce">Folds each successful partial response into the accumulator.</param>
    /// <param name="options">Concurrency cap and partial-failure policy. Defaults apply if <c>null</c>.</param>
    /// <returns>The reduced value plus any failed shards (per the failure policy).</returns>
    /// <exception cref="ScatterGatherPartialFailureException">
    /// Thrown under <see cref="PartialFailureMode.ThrowOnAnyFailure"/> when any shard failed.
    /// </exception>
    public static async Task<ScatterGatherResult<TShard, TAccum>> ScatterGatherAsync<TShard, TPartial, TAccum>(
        this IBenzeneMessageSender sender,
        string topic,
        IEnumerable<TShard> shards,
        TAccum seed,
        Func<TAccum, TPartial, TAccum> reduce,
        ScatterGatherOptions? options = null)
    {
        options ??= new ScatterGatherOptions();
        var shardList = shards.ToList();

        var outcomes = await BoundedFanOut.WhenAllAsync(
            shardList,
            async shard =>
            {
                try
                {
                    var result = await sender.SendAsync<TShard, TPartial>(topic, shard);
                    return result.IsSuccessful
                        ? Outcome<TShard, TPartial>.Ok(shard, result.Payload)
                        : Outcome<TShard, TPartial>.Failed(shard);
                }
                catch
                {
                    // A transport-level throw is a failed shard; the policy below decides what that means.
                    return Outcome<TShard, TPartial>.Failed(shard);
                }
            },
            options.MaxDegreeOfParallelism);

        var failed = outcomes.Where(o => !o.Success).Select(o => o.Shard).ToList();

        if (options.PartialFailureMode == PartialFailureMode.ThrowOnAnyFailure && failed.Count > 0)
        {
            throw new ScatterGatherPartialFailureException(failed.Count, shardList.Count);
        }

        var value = outcomes
            .Where(o => o.Success)
            .Aggregate(seed, (acc, o) => reduce(acc, o.Partial!));

        return new ScatterGatherResult<TShard, TAccum>(value, failed);
    }

    private readonly struct Outcome<TShard, TPartial>
    {
        private Outcome(TShard shard, bool success, TPartial? partial)
        {
            Shard = shard;
            Success = success;
            Partial = partial;
        }

        public TShard Shard { get; }
        public bool Success { get; }
        public TPartial? Partial { get; }

        public static Outcome<TShard, TPartial> Ok(TShard shard, TPartial partial) => new(shard, true, partial);
        public static Outcome<TShard, TPartial> Failed(TShard shard) => new(shard, false, default);
    }
}
