using Benzene.Abstractions;
using Benzene.Abstractions.Serialization;
using Benzene.Core.Messages.TestHelpers;
using Benzene.Core.MessageHandlers.Serialization;

namespace Benzene.Azure.Function.QueueStorage.TestHelpers;

/// <summary>
/// Test helpers that turn a <see cref="IMessageBuilder{T}"/> into a <see cref="QueueStorageMessage"/>,
/// so a component test can push the demo message through a built Azure Function app's Queue Storage
/// entry point (<c>HandleQueueMessages</c>) exactly as the trigger would deliver it.
/// </summary>
/// <remarks>
/// A Queue Storage message carries no properties for a topic to ride on, so routing comes from a
/// Benzene message envelope (<c>{ "topic": ..., "headers": ..., "body": ... }</c>) in the message text
/// - the shape a <c>UseBenzeneMessage(...)</c> pipeline consumes. The message text is that envelope,
/// serialized with the same serializer the pipeline deserializes it with.
/// </remarks>
public static class MessageBuilderExtensions
{
    /// <summary>
    /// Builds a <see cref="QueueStorageMessage"/> whose text is the Benzene message envelope, using the
    /// default JSON serializer.
    /// </summary>
    /// <typeparam name="T">The message body type.</typeparam>
    /// <param name="source">The message builder.</param>
    /// <returns>The Queue Storage message.</returns>
    public static QueueStorageMessage AsQueueStorageBenzeneMessage<T>(this IMessageBuilder<T> source)
    {
        return source.AsQueueStorageBenzeneMessage(new JsonSerializer());
    }

    /// <summary>
    /// Builds a <see cref="QueueStorageMessage"/> whose text is the Benzene message envelope, using the
    /// supplied serializer for the envelope's <c>Body</c> only - the envelope itself is always JSON,
    /// matching <see cref="BenzeneMessageQueueStorageHandler"/>, which always deserializes the envelope
    /// with the DI-resolved default serializer regardless of the body's own format.
    /// </summary>
    /// <typeparam name="T">The message body type.</typeparam>
    /// <param name="source">The message builder.</param>
    /// <param name="serializer">The serializer used to render the envelope's <c>Body</c>.</param>
    /// <returns>The Queue Storage message.</returns>
    public static QueueStorageMessage AsQueueStorageBenzeneMessage<T>(this IMessageBuilder<T> source, ISerializer serializer)
    {
        return new QueueStorageMessage(new JsonSerializer().Serialize(source.AsBenzeneMessage(serializer)));
    }
}
