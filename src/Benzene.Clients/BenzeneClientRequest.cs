using Benzene.Abstractions.Messages.BenzeneClient;

namespace Benzene.Clients;

/// <summary>Default <see cref="IBenzeneClientRequest{TMessage}"/> implementation.</summary>
/// <typeparam name="TMessage">The request message type.</typeparam>
public class BenzeneClientRequest<TMessage> : IBenzeneClientRequest<TMessage>
{
    /// <summary>Gets the topic to route the request to.</summary>
    public string Topic { get; }

    /// <summary>Gets the request message.</summary>
    public TMessage Message { get; }

    /// <summary>Gets the headers to send alongside the message.</summary>
    public IDictionary<string, string> Headers { get; }

    /// <summary>Initializes a new instance of the <see cref="BenzeneClientRequest{TMessage}"/> class.</summary>
    /// <param name="topic">The topic to route the request to.</param>
    /// <param name="message">The request message.</param>
    /// <param name="headers">The headers to send alongside the message.</param>
    public BenzeneClientRequest(string topic, TMessage message, IDictionary<string, string> headers)
    {
        Topic = topic;
        Message = message;
        Headers = headers;
    }
}
