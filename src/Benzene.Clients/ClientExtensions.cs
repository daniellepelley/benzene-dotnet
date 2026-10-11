using Benzene.Abstractions.Messages;
using Benzene.Abstractions.Results;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Clients;

/// <summary>Convenience overloads over <see cref="IBenzeneMessageClient"/>/<see cref="IBenzeneMessageSender"/>.</summary>
public static class ClientExtensions
{
    /// <summary>Sends <paramref name="message"/> on <paramref name="topic"/> with no headers.</summary>
    public static Task<IBenzeneResult<TResponse>> SendMessageAsync<TMessage, TResponse>(
        this IBenzeneMessageClient source, string topic, TMessage message)
    {
        return source.SendMessageAsync<TMessage, TResponse>(topic, message, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Sends <paramref name="message"/> on <paramref name="topic"/> with the given headers.</summary>
    public static Task<IBenzeneResult<TResponse>> SendMessageAsync<TMessage, TResponse>(
        this IBenzeneMessageClient source, string topic, TMessage message, IDictionary<string, string> headers)
    {
        return source.SendMessageAsync<TMessage, TResponse>(new BenzeneClientRequest<TMessage>(topic, message, headers));
    }

    /// <summary>Sends <paramref name="request"/> on <paramref name="topic"/>, discarding any response payload (<see cref="Void"/>).</summary>
    public static async Task<IBenzeneResult> SendMessageAsync<TRequest>(this IBenzeneMessageClient client,
        string topic, TRequest request)
    {
        var clientRequest = new BenzeneClientRequest<TRequest>(topic, request, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        return await client.SendMessageAsync<TRequest, Void>(clientRequest);
    }

    /// <summary>
    /// Sends <paramref name="message"/> on <paramref name="topic"/> declaring the payload schema
    /// <paramref name="version"/>, so a consumer that upcasts (or dispatches by version) sees it. The version
    /// travels in the standard <see cref="MessageVersionHeaders.Default"/> header, merged over any existing
    /// version header in <paramref name="headers"/>.
    /// </summary>
    public static Task<IBenzeneResult<TResponse>> SendMessageAsync<TMessage, TResponse>(
        this IBenzeneMessageClient source, string topic, TMessage message, string version,
        IDictionary<string, string>? headers = null)
    {
        return source.SendMessageAsync<TMessage, TResponse>(topic, message, WithVersion(headers, version));
    }

    /// <summary>
    /// Returns a copy of <paramref name="headers"/> with the version header set to <paramref name="version"/>.
    /// A null/empty version leaves the headers unchanged (returns a non-null copy).
    /// </summary>
    /// <param name="headers">The headers to copy.</param>
    /// <param name="version">The message's version.</param>
    /// <param name="headerName">
    /// The one version header (wire-contracts.md §2). Defaults to <see cref="MessageVersionHeaders.Default"/>
    /// (<c>benzene-version</c>); an application that renamed it in <c>IBenzeneWireNames.Version</c> passes
    /// that name, so it writes the same header its own receivers read.
    /// </param>
    public static IDictionary<string, string> WithVersion(this IDictionary<string, string>? headers, string version,
        string headerName = MessageVersionHeaders.Default)
    {
        var merged = headers is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(version))
        {
            merged[string.IsNullOrEmpty(headerName) ? MessageVersionHeaders.Default : headerName] = version;
        }
        return merged;
    }

    /// <summary>
    /// Sends <paramref name="request"/> on <paramref name="topic"/> declaring the payload schema
    /// <paramref name="version"/>. The version travels in the standard
    /// <see cref="MessageVersionHeaders.Default"/> header (merged over <paramref name="headers"/>), so the
    /// receiving service can upcast an older payload to its handler's schema or dispatch to a version-specific
    /// handler.
    /// </summary>
    public static Task<IBenzeneResult<TResponse>> SendAsync<TRequest, TResponse>(
        this IBenzeneMessageSender sender, string topic, TRequest request, string version,
        IDictionary<string, string>? headers = null)
    {
        return sender.SendAsync<TRequest, TResponse>(topic, request, WithVersion(headers, version));
    }
}
