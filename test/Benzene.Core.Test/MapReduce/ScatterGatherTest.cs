using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.Results;
using Benzene.Clients;
using Benzene.MapReduce;
using Moq;
using Xunit;

namespace Benzene.Test.MapReduce;

public class ScatterGatherTest
{
    private static IBenzeneResult<int> Ok(int payload) =>
        Mock.Of<IBenzeneResult<int>>(r => r.IsSuccessful == true && r.Payload == payload);

    private static IBenzeneResult<int> Fail() =>
        Mock.Of<IBenzeneResult<int>>(r => r.IsSuccessful == false);

    private static Mock<IBenzeneMessageSender> SenderReturning(System.Func<int, IBenzeneResult<int>> perShard)
    {
        var sender = new Mock<IBenzeneMessageSender>();
        sender
            .Setup(x => x.SendAsync<int, int>("work", It.IsAny<int>(), It.IsAny<IDictionary<string, string>?>()))
            .Returns((string _, int shard, IDictionary<string, string>? _) => Task.FromResult(perShard(shard)));
        return sender;
    }

    [Fact]
    public async Task AllShardsSucceed_ReducesToTheSum_AndIsComplete()
    {
        var sender = SenderReturning(shard => Ok(shard));   // partial == shard

        var result = await sender.Object.ScatterGatherAsync<int, int, int>(
            "work", new[] { 1, 2, 3 }, seed: 0, reduce: (acc, p) => acc + p);

        Assert.Equal(6, result.Value);
        Assert.True(result.IsComplete);
        Assert.Empty(result.FailedShards);
    }

    [Fact]
    public async Task ThrowOnAnyFailure_WhenAShardFails_Throws()
    {
        var sender = SenderReturning(shard => shard == 2 ? Fail() : Ok(shard));

        await Assert.ThrowsAsync<ScatterGatherPartialFailureException>(() =>
            sender.Object.ScatterGatherAsync<int, int, int>(
                "work", new[] { 1, 2, 3 }, seed: 0, reduce: (acc, p) => acc + p));
    }

    [Fact]
    public async Task BestEffort_WhenAShardFails_ReducesOverSuccesses_AndReportsFailures()
    {
        var sender = SenderReturning(shard => shard == 2 ? Fail() : Ok(shard));

        var result = await sender.Object.ScatterGatherAsync<int, int, int>(
            "work", new[] { 1, 2, 3 }, seed: 0, reduce: (acc, p) => acc + p,
            new ScatterGatherOptions { PartialFailureMode = PartialFailureMode.BestEffort });

        Assert.Equal(4, result.Value);           // 1 + 3; shard 2 excluded
        Assert.False(result.IsComplete);
        Assert.Equal(new[] { 2 }, result.FailedShards.Select(f => f.Shard));
        Assert.Null(result.FailedShards.Single().Reason); // an unsuccessful result, not a throw
    }

    [Fact]
    public async Task WhenAWorkerThrowsOperationCanceled_PropagatesCancellation_InsteadOfReportingAFailedShard()
    {
        var sender = new Mock<IBenzeneMessageSender>();
        sender
            .Setup(x => x.SendAsync<int, int>("work", It.IsAny<int>(), It.IsAny<IDictionary<string, string>?>()))
            .Returns((string _, int shard, IDictionary<string, string>? _) =>
                shard == 2
                    ? Task.FromCanceled<IBenzeneResult<int>>(new CancellationToken(canceled: true))
                    : Task.FromResult(Ok(shard)));

        var exception = await Record.ExceptionAsync(() =>
            sender.Object.ScatterGatherAsync<int, int, int>(
                "work", new[] { 1, 2, 3 }, seed: 0, reduce: (acc, p) => acc + p,
                new ScatterGatherOptions { PartialFailureMode = PartialFailureMode.BestEffort }));

        // TaskCanceledException (thrown here because the shard's Task was created via
        // Task.FromCanceled) is itself an OperationCanceledException - the point is that it
        // propagates as a cancellation, not that it gets reported as a failed shard.
        Assert.IsAssignableFrom<System.OperationCanceledException>(exception);
    }

    [Fact]
    public async Task BestEffort_WhenAWorkerThrows_TreatsShardAsFailed()
    {
        var sender = new Mock<IBenzeneMessageSender>();
        sender
            .Setup(x => x.SendAsync<int, int>("work", It.IsAny<int>(), It.IsAny<IDictionary<string, string>?>()))
            .Returns((string _, int shard, IDictionary<string, string>? _) =>
                shard == 2
                    ? Task.FromException<IBenzeneResult<int>>(new System.InvalidOperationException("boom"))
                    : Task.FromResult(Ok(shard)));

        var result = await sender.Object.ScatterGatherAsync<int, int, int>(
            "work", new[] { 1, 2, 3 }, seed: 0, reduce: (acc, p) => acc + p,
            new ScatterGatherOptions { PartialFailureMode = PartialFailureMode.BestEffort });

        Assert.Equal(4, result.Value);
        Assert.Equal(new[] { 2 }, result.FailedShards.Select(f => f.Shard));
        Assert.IsType<System.InvalidOperationException>(result.FailedShards.Single().Reason);
        Assert.Equal("boom", result.FailedShards.Single().Reason!.Message);
    }

    // #259: an empty shard collection must reduce cleanly to the seed, report complete, and never
    // touch the sender at all (MockBehavior.Strict - any unexpected call throws).
    [Fact]
    public async Task EmptyShards_ReducesToTheSeed_AndIsComplete_WithoutCallingTheSender()
    {
        var sender = new Mock<IBenzeneMessageSender>(MockBehavior.Strict);

        var result = await sender.Object.ScatterGatherAsync<int, int, int>(
            "work", System.Array.Empty<int>(), seed: 42, reduce: (acc, p) => acc + p);

        Assert.Equal(42, result.Value);
        Assert.True(result.IsComplete);
        Assert.Empty(result.FailedShards);
        sender.Verify(
            x => x.SendAsync<int, int>(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IDictionary<string, string>?>()),
            Times.Never);
    }

    // #259: the reduce delegate itself throwing mid-fold (after some shards already reduced
    // successfully) must propagate that exception directly - it is not a "failed shard" (every worker
    // call succeeded here; the fold is the app's own code, run after every shard has already
    // completed) and must not be swallowed or reported via FailedShards/ScatterGatherPartialFailureException.
    [Fact]
    public async Task ReduceDelegateThrows_MidFold_PropagatesDirectly()
    {
        var sender = SenderReturning(shard => Ok(shard)); // every shard succeeds

        var thrown = await Assert.ThrowsAsync<System.InvalidOperationException>(() =>
            sender.Object.ScatterGatherAsync<int, int, int>(
                "work", new[] { 1, 2, 3 }, seed: 0,
                reduce: (acc, p) =>
                {
                    if (p == 2)
                    {
                        throw new System.InvalidOperationException("reduce blew up");
                    }

                    return acc + p;
                }));

        Assert.Equal("reduce blew up", thrown.Message);
    }

    // #259: MaxDegreeOfParallelism must actually bound how many worker calls are in flight at once,
    // end to end through ScatterGatherAsync - not just unit-tested in isolation on BoundedFanOut. Six
    // shards, cap of 2: every worker call parks on a shared gate the moment it starts, so the number
    // in flight can only ever grow past the cap if the bound isn't actually being enforced - this is
    // deterministic (no timing-based flakiness), not merely "usually stays under the cap".
    [Fact]
    public async Task MaxDegreeOfParallelism_BoundsConcurrency_EndToEnd()
    {
        var current = 0;
        var maxObserved = 0;
        var gate = new TaskCompletionSource();

        var sender = new Mock<IBenzeneMessageSender>();
        sender
            .Setup(x => x.SendAsync<int, int>("work", It.IsAny<int>(), It.IsAny<IDictionary<string, string>?>()))
            .Returns(async (string _, int shard, IDictionary<string, string>? _) =>
            {
                var now = Interlocked.Increment(ref current);
                InterlockedMax(ref maxObserved, now);
                await gate.Task; // held open until the test explicitly releases it below.
                Interlocked.Decrement(ref current);
                return Ok(shard);
            });

        var shards = System.Linq.Enumerable.Range(1, 6).ToArray();
        var runTask = sender.Object.ScatterGatherAsync<int, int, int>(
            "work", shards, seed: 0, reduce: (acc, p) => acc + p,
            new ScatterGatherOptions { MaxDegreeOfParallelism = 2 });

        // Wait for exactly `cap` workers to be admitted and parked on the gate - proves the cap
        // actually gated entry, not a race that merely happened to look bounded.
        await WaitUntilAsync(() => Volatile.Read(ref current) == 2, System.TimeSpan.FromSeconds(5));
        // A brief settle window for an over-admission bug to show up before asserting the ceiling.
        await Task.Delay(50);
        Assert.Equal(2, Volatile.Read(ref current));
        Assert.Equal(2, maxObserved);

        gate.SetResult();
        var result = await runTask;

        Assert.Equal(21, result.Value); // 1+2+3+4+5+6
        Assert.True(result.IsComplete);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int initial, computed;
        do
        {
            initial = target;
            computed = initial > value ? initial : value;
        } while (Interlocked.CompareExchange(ref target, computed, initial) != initial);
    }

    private static async Task WaitUntilAsync(System.Func<bool> condition, System.TimeSpan timeout)
    {
        var deadline = System.DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(System.DateTime.UtcNow <= deadline, "Timed out waiting for the expected concurrency to be reached.");
            await Task.Delay(5);
        }
    }

    // Regression test for #92: ScatterGatherAsync used to discard per-shard exception detail
    // (Outcome.Failed carried only the shard), so a ScatterGatherPartialFailureException thrown from
    // ThrowOnAnyFailure had InnerException == null and no way to tell which shard failed for which
    // reason. Five of ten shards throw five DIFFERENT exceptions concurrently; assert every failed
    // shard's own reason is captured and distinguishable - not just the shard identity or the count.
    [Fact]
    public async Task ThrowOnAnyFailure_CarriesEachFailedShardsDistinctException()
    {
        var reasonFor = new System.Collections.Generic.Dictionary<int, System.Exception>
        {
            [1] = new System.InvalidOperationException("shard 1 blew up"),
            [3] = new System.TimeoutException("shard 3 timed out"),
            [5] = new System.Net.Http.HttpRequestException("shard 5 network error"),
            [7] = new System.FormatException("shard 7 bad format"),
            [9] = new System.NotSupportedException("shard 9 unsupported"),
        };

        var sender = new Mock<IBenzeneMessageSender>();
        sender
            .Setup(x => x.SendAsync<int, int>("work", It.IsAny<int>(), It.IsAny<IDictionary<string, string>?>()))
            .Returns((string _, int shard, IDictionary<string, string>? _) =>
                reasonFor.TryGetValue(shard, out var ex)
                    ? Task.FromException<IBenzeneResult<int>>(ex)
                    : Task.FromResult(Ok(shard)));

        var shards = System.Linq.Enumerable.Range(0, 10).ToArray(); // 0..9, five throw (odd ids above)

        var thrown = await Assert.ThrowsAsync<ScatterGatherPartialFailureException>(() =>
            sender.Object.ScatterGatherAsync<int, int, int>(
                "work", shards, seed: 0, reduce: (acc, p) => acc + p));

        Assert.Equal(5, thrown.FailedShardCount);
        Assert.Equal(10, thrown.TotalShardCount);

        // Every failed shard's own reason is present and distinguishable by shard identity.
        Assert.Equal(5, thrown.Failures.Count);
        foreach (var (shard, reason) in thrown.Failures)
        {
            var shardId = Assert.IsType<int>(shard);
            Assert.True(reasonFor.ContainsKey(shardId));
            Assert.Same(reasonFor[shardId], reason);
        }

        // And aggregated onto InnerException so ordinary .NET exception inspection also finds them.
        var aggregate = Assert.IsType<System.AggregateException>(thrown.InnerException);
        Assert.Equal(5, aggregate.InnerExceptions.Count);
        foreach (var reason in reasonFor.Values)
        {
            Assert.Contains(reason, aggregate.InnerExceptions);
        }

        // Distinct exception types, not just distinct instances/messages.
        Assert.Equal(5, aggregate.InnerExceptions.Select(e => e.GetType()).Distinct().Count());
    }
}
