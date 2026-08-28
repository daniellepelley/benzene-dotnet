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

        var bufferBytes = buffer.ToArray();
        var length = bufferBytes.Length;
        if (truncated)
        {
            // #246: back the truncation point off to the end of the last COMPLETE UTF-8 sequence at
            // or before the byte cap. Without this, a response cut mid-multi-byte-character (a
            // realistic case: the cap can land anywhere in a UTF-8 body) leaves a dangling lead or
            // continuation byte at the end of the buffer, and Encoding.UTF8.GetString silently
            // substitutes a U+FFFD replacement glyph for it - right before TruncatedMarker, in what
            // this package calls the audit-visible record of what happened.
            length = LastCompleteUtf8SequenceEnd(bufferBytes, length);
        }

        var text = Encoding.UTF8.GetString(bufferBytes, 0, length);
        return truncated ? text + TruncatedMarker : text;
    }

    /// <summary>
    /// Given <paramref name="length"/> raw bytes of (possibly cut-off) UTF-8 in
    /// <paramref name="bytes"/>, returns the largest prefix length &lt;= <paramref name="length"/>
    /// that ends on a complete UTF-8 sequence boundary - i.e. never inside a multi-byte character.
    /// Scans backward from the cap for at most 3 bytes (the longest UTF-8 sequence is 4 bytes, so a
    /// sequence start can be at most 3 bytes before the cut) looking for a lead byte whose declared
    /// sequence length would run past <paramref name="length"/>; if found, the cut lands before that
    /// lead byte. A cap that lands cleanly on a boundary (the common case for ASCII-heavy bodies, and
    /// always true when the cap wasn't actually reached mid-character) returns <paramref name="length"/>
    /// unchanged.
    /// </summary>
    private static int LastCompleteUtf8SequenceEnd(byte[] bytes, int length)
    {
        var scanFloor = Math.Max(0, length - 3);
        for (var i = length - 1; i >= scanFloor; i--)
        {
            var b = bytes[i];
            if ((b & 0b1100_0000) == 0b1000_0000)
            {
                // A UTF-8 continuation byte (10xxxxxx) - not a sequence start, keep scanning backward.
                continue;
            }

            // b is either a single-byte ASCII character (0xxxxxxx) or the lead byte of a multi-byte
            // sequence (11xxxxxx) - this is where the last sequence in the buffer starts.
            var sequenceLength = b switch
            {
                <= 0x7F => 1,
                >= 0xC0 and <= 0xDF => 2,
                >= 0xE0 and <= 0xEF => 3,
                >= 0xF0 and <= 0xF7 => 4,
                // 0xF8-0xFF is not a valid UTF-8 lead byte at all (0x80-0xBF, the continuation-byte
                // range, never reaches here - the loop above already skips past those) - there is no
                // well-formed sequence to back off to; leave the cut where it was rather than guessing.
                _ => 0,
            };

            return sequenceLength > 0 && i + sequenceLength > length ? i : length;
        }

        // No sequence start found within the last 3 bytes (an implausibly long continuation-byte
        // run) - leave the cut where it was; this isn't the mid-character-cut case this guards.
        return length;
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
