using System.Text.Json;
using Benzene.Mesh.Contracts;
using Benzene.Schema.OpenApi;

namespace Benzene.CodeGen.Cli.Core.Commands.Spec;

/// <summary>
/// Resolves a spec document from a mesh manifest: fetches <c>manifest.json</c>, finds the named
/// service's entry, then fetches its <c>services/{name}.json</c> snapshot
/// (<see cref="MeshServiceSnapshot"/>) and returns its cached <see cref="MeshServiceSnapshot.SpecJson"/>
/// verbatim - the same artifact the mesh itself stores without re-serializing. The snapshot path is
/// resolved relative to the manifest URL exactly as the Mesh UI resolves relative-path artifacts
/// (<c>new URL(relativePath, manifestUrl)</c> - <see cref="Uri"/>'s two-argument constructor is the
/// .NET equivalent), matching how <c>Benzene.Mesh.Aggregator</c> names the artifact it wrote
/// (<c>$"services/{entry.Name}.json"</c>).
/// </summary>
public class MeshSpecSource : ISpecSource, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _manifestUrl;
    private readonly string _serviceName;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    /// <summary>Creates a source that owns (and disposes) its own <see cref="HttpClient"/>.</summary>
    public MeshSpecSource(string manifestUrl, string serviceName)
        : this(manifestUrl, serviceName, new HttpClient(), ownsClient: true)
    {
    }

    /// <summary>Creates a source over a caller-supplied <see cref="HttpClient"/> (e.g. a fake handler in tests).</summary>
    public MeshSpecSource(string manifestUrl, string serviceName, HttpClient httpClient, bool ownsClient = false)
    {
        _manifestUrl = manifestUrl;
        _serviceName = serviceName;
        _httpClient = httpClient;
        _ownsClient = ownsClient;
    }

    public async Task<string> GetSpecJsonAsync(SpecRequest request)
    {
        var manifest = await FetchJsonAsync<MeshManifest>(_manifestUrl, "manifest.json");

        var entry = manifest.Services.FirstOrDefault(x =>
            string.Equals(x.Name, _serviceName, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            var known = string.Join(", ", manifest.Services.Select(x => x.Name));
            throw new InvalidOperationException(
                $"--service '{_serviceName}' was not found in the mesh manifest at {_manifestUrl}. " +
                $"Known services: {(string.IsNullOrEmpty(known) ? "(none)" : known)}");
        }

        // Resolve relative to the manifest URL - mirrors the Mesh UI's `new URL(r, manifestUrl)` and
        // the aggregator's own artifact naming ($"services/{entry.Name}.json"), not the raw
        // --service argument's casing.
        var snapshotUrl = new Uri(new Uri(_manifestUrl), $"services/{entry.Name}.json").ToString();
        var snapshot = await FetchJsonAsync<MeshServiceSnapshot>(snapshotUrl, $"'{entry.Name}' snapshot");

        if (string.IsNullOrWhiteSpace(snapshot.SpecJson))
        {
            throw new InvalidOperationException(
                $"The mesh snapshot for '{entry.Name}' at {snapshotUrl} has no cached spec (specJson is empty) " +
                "- the aggregator's last run could not fetch this service's spec.");
        }

        return snapshot.SpecJson;
    }

    private async Task<T> FetchJsonAsync<T>(string url, string label)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(url);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"GET {url} ({label}) did not reach the mesh artifact store: {ex.Message}", ex);
        }

        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GET {url} ({label}) returned {(int)response.StatusCode} {response.ReasonPhrase}, expected 200");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions)
                   ?? throw new InvalidOperationException($"GET {url} ({label}) returned an empty document");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"GET {url} ({label}) did not parse as JSON: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
