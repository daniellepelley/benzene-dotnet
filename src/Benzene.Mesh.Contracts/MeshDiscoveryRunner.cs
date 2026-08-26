namespace Benzene.Mesh.Contracts;

/// <summary>
/// Runs the discovery phase: executes every registered <see cref="IMeshDiscoveryProvider"/>, unions
/// the results with an optional hand-written static seed, de-duplicates by service name, and returns
/// the <see cref="MeshServiceRegistry"/> the aggregator will consume. This is the "discovery creates
/// the config" step — decoupled from the runtime poll loop so the two can be hosted and scheduled
/// independently.
/// </summary>
public class MeshDiscoveryRunner
{
    // Matches MeshAggregator's PerServiceFetchTimeout convention: an explicit, documented bound on
    // each provider's call rather than relying solely on its own (potentially much longer) defaults -
    // one slow/hung provider (a stalled cloud API call) shouldn't be able to stall the whole run.
    private static readonly TimeSpan PerProviderTimeout = TimeSpan.FromSeconds(10);

    private readonly IReadOnlyList<IMeshDiscoveryProvider> _providers;

    /// <summary>Initializes the runner over the registered providers.</summary>
    /// <param name="providers">The discovery providers to run (mirrors the multi-source DI pattern).</param>
    public MeshDiscoveryRunner(IEnumerable<IMeshDiscoveryProvider> providers)
    {
        _providers = providers.ToArray();
    }

    /// <summary>
    /// Discovers services and merges them with <paramref name="staticSeed"/>. On a name clash the
    /// seed (and then an earlier provider) wins — a hand-pinned entry is an intentional human override
    /// that discovery must not silently replace.
    /// </summary>
    /// <remarks>
    /// One provider throwing (or exceeding <see cref="PerProviderTimeout"/>) contributes nothing but
    /// never aborts the run — the same failure-isolation rule <c>MeshAggregator.RunOnceAsync</c>
    /// applies per service. A caller that wants to know <em>which</em> provider(s) failed (rather than
    /// silently seeing fewer services than expected) passes <paramref name="failures"/>; a caller that
    /// doesn't care can omit it, same as before this method gained failure isolation.
    /// </remarks>
    /// <param name="filter">The discovery filter passed to every provider.</param>
    /// <param name="staticSeed">Optional hand-written registry to union in (wins on a name clash).</param>
    /// <param name="failures">
    /// When given, every provider that failed is appended here as a <see cref="MeshDiscoveryProviderFailure"/>
    /// (provider key + exception type name, never the message). Optional — a caller that only wants the
    /// registry can omit this.
    /// </param>
    /// <param name="cancellationToken">
    /// A token to cancel the whole run. Distinct from the internal per-provider timeout: a provider that
    /// merely times out is recorded as a failure and the loop continues, but a caller-driven cancellation
    /// (e.g. host shutdown) propagates immediately instead of being swallowed as one more failed provider.
    /// </param>
    public async Task<MeshServiceRegistry> DiscoverAsync(
        MeshDiscoveryFilter filter,
        MeshServiceRegistry? staticSeed = null,
        ICollection<MeshDiscoveryProviderFailure>? failures = null,
        CancellationToken cancellationToken = default)
    {
        var byName = new Dictionary<string, MeshServiceRegistryEntry>(StringComparer.OrdinalIgnoreCase);

        if (staticSeed != null)
        {
            foreach (var entry in staticSeed.Services)
            {
                byName[entry.Name] = entry;
            }
        }

        foreach (var provider in _providers)
        {
            IReadOnlyList<MeshServiceRegistryEntry> discovered;
            try
            {
                using var timeout = new CancellationTokenSource(PerProviderTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                discovered = await provider.DiscoverAsync(filter, linked.Token);
            }
            catch (Exception ex)
            {
                // A genuine caller-driven cancellation (not this provider's own timeout) must propagate,
                // not be recorded as "one more failed provider" and swallowed.
                cancellationToken.ThrowIfCancellationRequested();

                failures?.Add(new MeshDiscoveryProviderFailure(provider.Key, ex.GetType().Name));
                continue; // this provider contributes nothing; every other provider still runs
            }

            foreach (var entry in discovered)
            {
                if (!byName.ContainsKey(entry.Name))
                {
                    byName[entry.Name] = entry;
                }
            }
        }

        return new MeshServiceRegistry(byName.Values.ToArray());
    }
}
