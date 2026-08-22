namespace Benzene.Core.Helper;

/// <summary>
/// Double-checked-locking cache for a single "find all definitions" call, shared by any decorator
/// that memoizes a potentially expensive discovery call (e.g. reflection-based assembly scanning) for
/// the lifetime of the process. Used to back caching decorators such as
/// <c>Benzene.Core.MessageHandlers.CacheMessageHandlersFinder</c> and
/// <c>Benzene.Http.Routing.CacheHttpEndpointFinder</c>, which differ only in the definition type they
/// wrap.
/// </summary>
/// <typeparam name="TDefinition">The definition type returned by the wrapped finder.</typeparam>
/// <remarks>
/// The cache is populated lazily on first use and never invalidated - definitions are assumed not to
/// change for the lifetime of the process. A double-checked lock is used rather than a plain `??=`:
/// the latter is a non-atomic read-then-write, so two threads racing the first call would both see
/// null and both run the (potentially expensive) discovery delegate. The volatile field publishes the
/// result safely once one thread has computed it.
/// </remarks>
public sealed class CachingFinder<TDefinition>
{
    private readonly Func<TDefinition[]> _find;
    private readonly object _lock = new();
    private volatile TDefinition[]? _definitions;

    /// <summary>
    /// Initializes a new instance of the <see cref="CachingFinder{TDefinition}"/> class.
    /// </summary>
    /// <param name="find">The (potentially expensive) discovery delegate to cache the result of.</param>
    public CachingFinder(Func<TDefinition[]> find)
    {
        _find = find;
    }

    /// <summary>
    /// Returns the cached definitions, calling the wrapped discovery delegate only on the first
    /// invocation.
    /// </summary>
    /// <returns>The cached (or newly discovered, on first call) definitions.</returns>
    public TDefinition[] FindDefinitions()
    {
        var cached = _definitions;
        if (cached != null)
        {
            return cached;
        }

        lock (_lock)
        {
            return _definitions ??= _find();
        }
    }
}
