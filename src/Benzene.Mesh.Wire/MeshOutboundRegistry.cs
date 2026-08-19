using Benzene.Abstractions.Messages;
using Benzene.Core.Messages;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Mesh.Wire;

/// <summary>
/// Looks up every outbound record an application has registered (docs/specification/mesh.md §2.3) -
/// the concept that mirrors core-concepts.md §9's inbound handler discovery exactly, applied to what
/// a service <b>sends</b> rather than what it serves. Consumed by <see cref="MeshDescriptorFactory"/>
/// to project <see cref="MeshServiceDescriptor.Produces"/>, the same way
/// <see cref="Benzene.Abstractions.MessageHandlers.IMessageHandlerDefinitionLookUp"/> is projected
/// into <see cref="MeshServiceDescriptor.Topics"/>. A null lookup at the factory means this port
/// (or this service) hasn't wired up outbound registration yet - the descriptor is built without a
/// consumed-topic list and the gap is recorded in <see cref="MeshServiceDescriptor.Degraded"/>,
/// never asserted as an empty "consumes nothing" claim.
/// </summary>
public interface IMeshOutboundDefinitionLookUp
{
    /// <summary>Returns every outbound record currently registered.</summary>
    IMessageSenderDefinition[] GetAllDefinitions();
}

/// <summary>
/// The explicit-registration path spec §2.3 requires every port to support: an application hands
/// this a list of (topic, version, request type, response type) records it <i>may send</i> - no
/// handler, since nothing here receives, and no destination/transport information, since that's
/// deployment configuration orthogonal to the contract. Reuses the existing
/// <see cref="MessageSenderDefinition"/>/<see cref="IMessageSenderDefinition"/> shape (the sender-side
/// counterpart of <c>IMessageHandlerDefinition</c> already in this codebase) rather than inventing a
/// parallel one; <see cref="IMessageSenderDefinition.SenderType"/> is left as <c>Void</c> here since
/// this registry only ever needs to answer the mesh's "what would we send" question, never construct
/// or resolve an actual sender.
/// <para>
/// Attribute/annotation sugar over this is an idiom (spec §2.3: "same as inbound") - this type is
/// only the explicit-registration MUST-support path. Not thread-safe against concurrent registration;
/// build it once at startup, the same lifecycle as the inbound handler registry.
/// </para>
/// </summary>
public class MeshOutboundRegistry : IMeshOutboundDefinitionLookUp
{
    private readonly List<IMessageSenderDefinition> _definitions = new();

    /// <summary>Registers an outbound (topic, request type) record with no declared response type -
    /// spec §2.3's shape: <c>responseSchema</c> projects as <c>{}</c> (unconstrained) for it.</summary>
    public MeshOutboundRegistry Register<TRequest>(string topic, string? version = null)
        => Register(topic, version, typeof(TRequest), typeof(Void));

    /// <summary>Registers an outbound (topic, request type, response type) record.</summary>
    public MeshOutboundRegistry Register<TRequest, TResponse>(string topic, string? version = null)
        => Register(topic, version, typeof(TRequest), typeof(TResponse));

    /// <summary>Registers an outbound record by CLR <see cref="Type"/>, for callers building
    /// registrations from reflected/discovered type information rather than compile-time generics.</summary>
    public MeshOutboundRegistry Register(string topic, string? version, Type requestType, Type responseType)
    {
        _definitions.Add(MessageSenderDefinition.CreateInstance(
            topic, version ?? string.Empty, requestType, responseType, typeof(Void)));
        return this;
    }

    public IMessageSenderDefinition[] GetAllDefinitions() => _definitions.ToArray();
}
