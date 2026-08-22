using Benzene.Abstractions.MessageHandlers;
using Benzene.Core.Helper;

namespace Benzene.Core.MessageHandlers;

/// <summary>
/// Decorates another <see cref="IMessageHandlersFinder"/>, caching the result of its first
/// <see cref="FindDefinitions"/> call so subsequent calls (e.g. per-request lookups against a
/// reflection-based finder) don't repeat potentially expensive assembly scanning.
/// </summary>
/// <remarks>
/// The cache is populated lazily on first use and never invalidated - handler definitions are
/// assumed not to change for the lifetime of the process. The caching itself (double-checked
/// locking) lives in the shared <see cref="CachingFinder{TDefinition}"/>; this type is just the
/// package-local, <see cref="IMessageHandlersFinder"/>-shaped entry point onto it - the same pattern
/// <c>Benzene.Http.Routing.CacheHttpEndpointFinder</c> uses for endpoint definitions.
/// </remarks>
internal class CacheMessageHandlersFinder : IMessageHandlersFinder
{
    private readonly CachingFinder<IMessageHandlerDefinition> _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheMessageHandlersFinder"/> class.
    /// </summary>
    /// <param name="inner">The finder to cache the results of.</param>
    public CacheMessageHandlersFinder(IMessageHandlersFinder inner)
    {
        _cache = new CachingFinder<IMessageHandlerDefinition>(inner.FindDefinitions);
    }

    /// <summary>
    /// Returns the cached handler definitions, calling the inner finder only on the first invocation.
    /// </summary>
    /// <returns>The cached (or newly discovered, on first call) handler definitions.</returns>
    public IMessageHandlerDefinition[] FindDefinitions() => _cache.FindDefinitions();
}
