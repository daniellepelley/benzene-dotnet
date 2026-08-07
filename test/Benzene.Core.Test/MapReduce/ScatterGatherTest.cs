using System.Collections.Generic;
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
        Assert.Equal(new[] { 2 }, result.FailedShards);
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
        Assert.Equal(new[] { 2 }, result.FailedShards);
    }
}
