using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Mesh.Contracts;

namespace Benzene.Mesh.Dispatch;

/// <summary>
/// Dispatches to an HTTP-reachable service by POSTing the Benzene message envelope
/// (<c>{ topic, headers, body }</c>) to its wire-envelope endpoint. The endpoint URL comes from the
/// entry's <c>SourceOptions["invokeUrl"]</c> when present, otherwise it's derived from the entry's
/// <see cref="MeshServiceRegistryEntry.SpecUrl"/> origin as <c>&lt;origin&gt;/benzene-message</c>
/// (Benzene's default receiving path).
/// </summary>
public class HttpMeshServiceDispatcher : IMeshServiceDispatcher
{
    /// <summary>The <see cref="MeshServiceRegistryEntry.SourceOptions"/> key overriding the invoke URL.</summary>
    public const string InvokeUrlOption = "invokeUrl";

    /// <summary>
    /// Default <see cref="MaxResponseBytes"/> - deliberately matches
    /// <see cref="MeshDispatchGuardOptions.DefaultMaxRequestBytes"/>, the request-side cap this mirrors:
    /// the same bound applies symmetrically to what a target is allowed to send back.
    /// </summary>
    public const int DefaultMaxResponseBytes = MeshDispatchGuardOptions.DefaultMaxRequestBytes;

    /// <summary>Appended to a response body that was cut off at <see cref="MaxResponseBytes"/>.</summary>
    public const string TruncatedMarker = "…[benzene.mesh.dispatch: response truncated]";

    private const string DefaultInvokePath = "/benzene-message";
    private const int ReadBufferSize = 8_192;

    private readonly HttpClient _httpClient;

    /// <summary>Initializes a new instance of the <see cref="HttpMeshServiceDispatcher"/> class.</summary>
    /// <param name="httpClient">The client used to POST the envelope. Its lifetime is the caller's responsibility.</param>
    /// <param name="maxResponseBytes">
    /// The largest target response body accepted, in bytes; see <see cref="MaxResponseBytes"/>.
    /// </param>
    public HttpMeshServiceDispatcher(HttpClient httpClient, int maxResponseBytes = DefaultMaxResponseBytes)
    {
        _httpClient = httpClient;
        MaxResponseBytes = maxResponseBytes;
    }

    /// <inheritdoc />
    public string Key => MeshServiceSource.Http;

    /// <summary>
    /// The largest target response body accepted, in bytes. Enforced while reading the response
    /// stream (noted gap, promoted into WP-1): the request side has always bounded what a caller can
    /// send (<see cref="MeshDispatchGuardOptions.MaxRequestBytes"/>), but nothing bounded what a
    /// dispatched-to service could send back - a compromised or misbehaving target could otherwise
    /// have this buffer an unbounded response into memory. A response that exceeds the cap is
    /// truncated with <see cref="TruncatedMarker"/> rather than the dispatch throwing, because the
    /// target DID respond and that response is still the record of what happened - the same "leaves a
    /// record" principle the audit trail is built on.
    /// </summary>
    public int MaxResponseBytes { get; }

    /// <inheritdoc />
    public async Task<MeshDispatchResult> DispatchAsync(MeshServiceRegistryEntry entry, MeshDispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        var url = ResolveInvokeUrl(entry);
        var payload = JsonSerializer.Serialize(new
        {
            topic = envelope.Topic,
            headers = envelope.Headers,
            body = envelope.Body,
        });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await ReadCappedAsync(response.Content, MaxResponseBytes, cancellationToken);

        // Pass back exactly what the service returned. A non-2xx HTTP status still carries a body.
        return new MeshDispatchResult(((int)response.StatusCode).ToString(), responseBody);
    }

    /// <summary>
    /// Reads <paramref name="content"/> as UTF-8 text, stopping once <paramref name="maxBytes"/> raw
    /// bytes have been read and appending <see cref="TruncatedMarker"/> when that happened - see
    /// <see cref="MaxResponseBytes"/>.
    /// </summary>
    private static async Task<string> ReadCappedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[ReadBufferSize];
        var truncated = false;

        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken)) > 0)
        {
            var remaining = maxBytes - (int)buffer.Length;
            if (remaining <= 0)
            {
                truncated = true;
                break;
            }

            var toKeep = Math.Min(remaining, read);
            buffer.Write(chunk, 0, toKeep);
            if (toKeep < read)
            {
                truncated = true;
                break;
            }
        }

        var text = Encoding.UTF8.GetString(buffer.ToArray());
        return truncated ? text + TruncatedMarker : text;
    }

    private static string ResolveInvokeUrl(MeshServiceRegistryEntry entry)
    {
        if (entry.SourceOptions != null
            && entry.SourceOptions.TryGetValue(InvokeUrlOption, out var explicitUrl)
            && !string.IsNullOrWhiteSpace(explicitUrl))
        {
            return explicitUrl;
        }

        if (string.IsNullOrWhiteSpace(entry.SpecUrl))
        {
            throw new InvalidOperationException(
                $"Mesh service \"{entry.Name}\" has no \"{InvokeUrlOption}\" in SourceOptions and no SpecUrl to derive an invoke URL from.");
        }

        var origin = new Uri(entry.SpecUrl).GetLeftPart(UriPartial.Authority);
        return origin + DefaultInvokePath;
    }
}
