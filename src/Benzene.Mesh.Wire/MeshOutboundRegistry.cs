using Benzene.Abstractions.Messages;
using Benzene.Core.Messages;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Mesh.Wire;

/// <summary>
/// This service's <b>send declarations</b> (contract-document.md §3.2): the explicit,
/// no-handler, no-destination record of the (topic, version, request type, response type) tuples it
/// may send to topics another party defines - what makes the producer side of the mesh graph as
/// reliable as the handler registry makes the consumer side (mesh.md §2: declared, never inferred).
/// </summary>
/// <remarks>
/// <para>
/// Registered in the container as <see cref="IMessageDefinitionFinder{TMessageDefinition}"/> of
/// <see cref="IMessageSenderDefinition"/> (<see cref="Extensions.AddSendDeclarations"/>, or the cloud
/// service's <c>WithSends</c>), its records become the contract document's <c>sends[]</c> - in the
/// document <c>benzene:spec</c> serves and the mesh register body alike. A generated client registers
/// its own finder for the topics it calls; every registered finder contributes. Events this service
/// publishes and defines are declared separately, with <c>AddResponseEventDeclarations</c>; a
/// (topic, version) in both is a start-up error.
/// </para>
/// <para>
/// Reuses <see cref="MessageSenderDefinition"/>; <see cref="IMessageSenderDefinition.SenderType"/> is
/// <c>Void</c>, since this registry only answers "what would we send", never constructs a sender. Not
/// thread-safe against concurrent registration: build it once at startup, like the handler registry.
/// </para>
/// </remarks>
public class MeshOutboundRegistry : IMessageDefinitionFinder<IMessageSenderDefinition>
{
    private readonly List<IMessageSenderDefinition> _definitions = new();

    /// <summary>Registers a one-way send (topic, request type) with no reply: the contract document's
    /// <c>sends[]</c> entry has no <c>response</c>.</summary>
    public MeshOutboundRegistry Register<TRequest>(string topic, string? version = null)
        => Register(topic, version, typeof(TRequest), typeof(Void));

    /// <summary>Registers a send (topic, request type, response type) that expects a reply.</summary>
    public MeshOutboundRegistry Register<TRequest, TResponse>(string topic, string? version = null)
        => Register(topic, version, typeof(TRequest), typeof(TResponse));

    /// <summary>Registers a send by CLR <see cref="Type"/>, for callers building registrations from
    /// reflected type information; a <paramref name="responseType"/> of <c>Void</c> is one-way.</summary>
    public MeshOutboundRegistry Register(string topic, string? version, Type requestType, Type responseType)
    {
        _definitions.Add(MessageSenderDefinition.CreateInstance(
            topic, version ?? string.Empty, requestType, responseType, typeof(Void)));
        return this;
    }

    /// <summary>Every send declared so far.</summary>
    public IMessageSenderDefinition[] GetAllDefinitions() => _definitions.ToArray();

    /// <inheritdoc />
    public IMessageSenderDefinition[] FindDefinitions() => GetAllDefinitions();
}
