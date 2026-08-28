using Benzene.Abstractions.MessageHandlers;

namespace Benzene.Core.MessageHandlers;

/// <summary>
/// In-memory registry of handler definitions added explicitly (e.g. via <see cref="MiddlewarePipelineExtensions.AddMessageHandler{THandler,TRequest,TResponse}"/>),
/// as opposed to those discovered by reflection. Implements both <see cref="IMessageHandlersFinder"/>
/// (so it can be composed with other finders) and <see cref="IMessageHandlersList"/> (so definitions
/// can be added to it directly).
/// </summary>
/// <remarks>
/// Registered as a DI singleton, and <see cref="MessageHandlerDefinitionIndex"/>'s own remarks
/// document runtime mutation - a definition added via <see cref="Add"/> after the index was already
/// built and cached - as a supported scenario (that is what its version-stamp invalidation exists
/// for). <see cref="Add"/> and <see cref="FindDefinitions"/> are therefore synchronized: <see cref="Add"/>
/// takes a lock, and <see cref="FindDefinitions"/> takes a consistent snapshot under the same lock
/// before copying it to an array, so a concurrent <see cref="Add"/> can never be observed as a torn or
/// incomplete read.
/// </remarks>
public class MessageHandlersList : IMessageHandlersFinder, IMessageHandlersList
{
    private readonly object _lock = new();
    private readonly List<IMessageHandlerDefinition> _list = new();

    // Backing field for Version, read via Volatile.Read / written via Interlocked.Increment rather than
    // a plain auto-property. MessageHandlerDefinitionIndex.GetIndex() deliberately reads Version without
    // taking any lock (mirroring its own volatile-published _state), so the write inside Add's lock must
    // still publish with a real memory barrier - a plain int field write is visible to that lock-free
    // reader "eventually" under the CLR's memory model, but not guaranteed promptly on a weak-ordering
    // architecture (ARM64/Graviton) - the same hazard MessageHandlerDefinitionIndex's own remarks flag
    // for its _state field, addressed here the same way.
    private int _version;

    /// <summary>
    /// A monotonically increasing stamp, incremented on every <see cref="Add"/>. Consulted by
    /// <see cref="MessageHandlerDefinitionIndex"/> to detect runtime additions that should invalidate
    /// its cached index - not part of <see cref="IMessageHandlersList"/> since it's an internal
    /// implementation detail of that caching, not a public list operation. Safe to read without taking
    /// any lock.
    /// </summary>
    public int Version => Volatile.Read(ref _version);

    /// <inheritdoc />
    public IMessageHandlerDefinition[] FindDefinitions()
    {
        lock (_lock)
        {
            return _list.ToArray();
        }
    }

    /// <summary>
    /// Adds a handler definition to the registry.
    /// </summary>
    /// <param name="messageHandlerDefinition">The handler definition to add.</param>
    public void Add(IMessageHandlerDefinition messageHandlerDefinition)
    {
        lock (_lock)
        {
            _list.Add(messageHandlerDefinition);
            Interlocked.Increment(ref _version);
        }
    }
}
