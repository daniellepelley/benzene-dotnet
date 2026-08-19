using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Mesh.Aggregator;
using Benzene.Mesh.Contracts;
using Xunit;

namespace Benzene.Mesh.Test;

/// <summary>
/// <see cref="MeshAggregationPass"/> - the seam four mesh hosts had each written by hand, three of
/// them with a single-writer gate and one without. The gate is the reason the type exists, so it is
/// what most of this file pins.
/// </summary>
public class MeshAggregationPassTest
{
    private static MeshServiceRegistry Registry(params string[] names) =>
        new(names.Select(name => new MeshServiceRegistryEntry(
            name, $"https://{name}.example/spec", $"https://{name}.example/healthcheck")).ToArray());

    /// <summary>Records every write, and can be told to block inside one so passes can be overlapped.</summary>
    private class RecordingStore : IMeshArtifactStore
    {
        public readonly ConcurrentQueue<string> Writes = new();
        public readonly List<string> RegistryBodies = new();
        public Func<string, Task>? OnPublish;

        public async Task PublishAsync(string key, string content)
        {
            Writes.Enqueue(key);
            if (key == MeshAggregationPass.RegistryArtifactKey)
            {
                lock (RegistryBodies) { RegistryBodies.Add(content); }
            }

            if (OnPublish is not null)
            {
                await OnPublish(key);
            }
        }

        public Task<string?> TryReadAsync(string key) => Task.FromResult<string?>(null);
    }

    /// <summary>An always-unreachable source: this file is about the pass, not about interrogation.</summary>
    private class UnreachableSource : IMeshServiceSource
    {
        public string Key => MeshServiceSource.Http;

        public Task<string> FetchSpecAsync(MeshServiceRegistryEntry entry, CancellationToken cancellationToken) =>
            Task.FromException<string>(new InvalidOperationException("not reachable in this test"));

        public Task<string> FetchHealthAsync(MeshServiceRegistryEntry entry, CancellationToken cancellationToken) =>
            Task.FromException<string>(new InvalidOperationException("not reachable in this test"));
    }

    private static MeshAggregator AggregatorFor(IMeshArtifactStore store) =>
        new(new IMeshServiceSource[] { new UnreachableSource() }, store);

    [Fact]
    public async Task RunAsync_PublishesTheDrivingRegistryThenTheCatalog_AndReturnsTheServiceCount()
    {
        var store = new RecordingStore();
        var pass = new MeshAggregationPass(store, AggregatorFor(store), Registry("orders", "payments"));

        var count = await pass.RunAsync();

        Assert.Equal(2, count);
        // The registry that DROVE the pass is published first - the "discovery creates the config"
        // seam a reader inspects to see what the catalog was built from.
        Assert.Equal(MeshAggregationPass.RegistryArtifactKey, store.Writes.First());
        Assert.Contains("orders", store.RegistryBodies.Single());
    }

    [Fact]
    public async Task RunAsync_AsksTheRegistrySourceOncePerPass_SoDiscoveryIsNotCachedAcrossPasses()
    {
        var store = new RecordingStore();
        var calls = 0;
        var pass = new MeshAggregationPass(store, AggregatorFor(store), _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Registry("orders"));
        });

        await pass.RunAsync();
        await pass.RunAsync();

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RunAsync_SerialisesOverlappingPasses_SoTheCatalogIsNeverHalfFromEachOne()
    {
        // The invariant the hand-written copies drifted on. A host runs this from a periodic timer
        // AND an on-demand refresh endpoint against one remote store; two passes interleaving their
        // writes leave manifest.json from one beside services/*.json from the other.
        var store = new RecordingStore();
        var inFlight = 0;
        var maxConcurrent = 0;
        var entered = new SemaphoreSlim(0, 1);
        var release = new TaskCompletionSource();

        store.OnPublish = async _ =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref maxConcurrent, now);
            if (now == 1 && entered.CurrentCount == 0)
            {
                entered.Release();
                await release.Task;
            }
            Interlocked.Decrement(ref inFlight);
        };

        var pass = new MeshAggregationPass(store, AggregatorFor(store), Registry("orders"));

        var first = pass.RunAsync();
        await entered.WaitAsync();       // the first pass is inside a write
        var second = pass.RunAsync();    // and now a second is asked for

        // The second must not have started: give it a real chance to, then check.
        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task RunAsync_ReleasesTheGate_WhenAPassThrows()
    {
        // A failed discovery or a store outage must not wedge every later pass - the one bug a
        // hand-rolled gate gets wrong even when it remembers the gate at all.
        var store = new RecordingStore();
        var shouldThrow = true;
        var pass = new MeshAggregationPass(store, AggregatorFor(store), _ =>
            shouldThrow
                ? Task.FromException<MeshServiceRegistry>(new InvalidOperationException("discovery is down"))
                : Task.FromResult(Registry("orders")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => pass.RunAsync());

        shouldThrow = false;
        var count = await pass.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, count);
    }

    [Fact]
    public void Constructor_RejectsANullDependency_AtCompositionRatherThanOnTheFirstPass()
    {
        var store = new RecordingStore();
        var aggregator = AggregatorFor(store);

        Assert.Throws<ArgumentNullException>(() => new MeshAggregationPass(null!, aggregator, Registry()));
        Assert.Throws<ArgumentNullException>(() => new MeshAggregationPass(store, null!, Registry()));
        Assert.Throws<ArgumentNullException>(() =>
            new MeshAggregationPass(store, aggregator, (Func<CancellationToken, Task<MeshServiceRegistry>>)null!));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (value <= seen) return;
        }
        while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }
}
