using Benzene.Mesh.Contracts;

namespace Benzene.Mesh.Aggregator;

/// <summary>
/// One aggregation pass — publish the registry that drove it, then interrogate every service in it
/// and write the catalog — run at most one at a time.
/// </summary>
/// <remarks>
/// <para>
/// Every mesh host needs this same three-step body, and the only genuine difference between hosts is
/// <em>where the registry comes from</em>: a cloud discovery pass on Azure and Kubernetes, a static
/// <c>MeshServiceRegistry.FromEnvironment()</c> elsewhere. That difference is the constructor
/// parameter; nothing else about the pass varies, so nothing else is worth a host writing out.
/// </para>
/// <para>
/// <b>The single-writer gate is the reason this is a type and not a snippet.</b> A host almost always
/// runs the pass two ways at once — a periodic background timer and an on-demand refresh endpoint —
/// against one remote artifact store. Two overlapping passes interleave their writes and can leave a
/// momentarily inconsistent catalog: <c>manifest.json</c> from one pass beside <c>services/*.json</c>
/// from the other. Serialising is not an optimisation, it is what makes the published catalog a
/// coherent snapshot, and it is exactly the kind of invariant that gets dropped when the body is
/// copied by hand — which is what happened: of the four hosts that had copied this, three took the
/// gate and the fourth did not.
/// </para>
/// <para>
/// The explicit form stays entirely available and is three lines — publish the registry, call
/// <see cref="MeshAggregator.RunOnceAsync"/>, count the services. Drop to it for a host that needs a
/// different write order, a different artifact key, or its own concurrency policy.
/// </para>
/// </remarks>
public sealed class MeshAggregationPass
{
    /// <summary>The artifact key the registry that drove a pass is published under.</summary>
    public const string RegistryArtifactKey = "registry.json";

    private readonly IMeshArtifactStore _store;
    private readonly MeshAggregator _aggregator;
    private readonly Func<CancellationToken, Task<MeshServiceRegistry>> _registrySource;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates a pass that obtains its registry from <paramref name="registrySource"/>.</summary>
    /// <param name="store">Where the driving registry is published (the "discovery creates the config" seam).</param>
    /// <param name="aggregator">The aggregator that interrogates the registry and writes the catalog.</param>
    /// <param name="registrySource">
    /// How this host obtains the registry for a pass — a discovery call, a static read, anything. It
    /// is invoked once per pass, inside the gate, so a source that is itself expensive or non-reentrant
    /// is safe here.
    /// </param>
    public MeshAggregationPass(
        IMeshArtifactStore store,
        MeshAggregator aggregator,
        Func<CancellationToken, Task<MeshServiceRegistry>> registrySource)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _registrySource = registrySource ?? throw new ArgumentNullException(nameof(registrySource));
    }

    /// <summary>Creates a pass over a registry that does not change between passes.</summary>
    /// <remarks>
    /// The shorthand for a host with no discovery — the registry is read once from configuration and
    /// reused. Exactly <c>new MeshAggregationPass(store, aggregator, _ =&gt; Task.FromResult(registry))</c>.
    /// </remarks>
    public MeshAggregationPass(IMeshArtifactStore store, MeshAggregator aggregator, MeshServiceRegistry registry)
        : this(store, aggregator, _ => Task.FromResult(registry ?? throw new ArgumentNullException(nameof(registry))))
    {
    }

    /// <summary>
    /// Runs one pass and returns how many services were in the registry that drove it. Waits, rather
    /// than skipping or throwing, if another pass is already running.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var registry = await _registrySource(cancellationToken).ConfigureAwait(false);
            await _store.PublishAsync(RegistryArtifactKey, MeshRegistryJson.Serialize(registry)).ConfigureAwait(false);
            await _aggregator.RunOnceAsync(registry).ConfigureAwait(false);
            return registry.Services.Length;
        }
        finally
        {
            _gate.Release();
        }
    }
}
