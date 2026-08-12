using Benzene.CloudService.Probe;
using Benzene.Schema.OpenApi;

namespace Benzene.CodeGen.Cli.Core.Commands.Spec;

/// <summary>
/// Fetches a spec document over plain HTTP from a running Benzene Cloud Service's spec endpoint
/// (profile R5) - <c>{baseUrl}{CloudServiceProbePaths.Spec}?type={type}&amp;format={format}</c>.
/// Mirrors how <c>profile-check</c>'s <see cref="CloudServiceProbe"/> reaches the same endpoint
/// (<see cref="CloudServiceProbePaths.Spec"/>, a plain <see cref="HttpClient"/> GET with
/// <see cref="HttpClient.BaseAddress"/> set to the service's base URL) rather than introducing new
/// HTTP plumbing.
/// </summary>
public class HttpSpecSource : ISpecSource, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    /// <summary>Creates a source that owns (and disposes) its own <see cref="HttpClient"/>.</summary>
    /// <param name="baseUrl">The base URL of the Benzene Cloud Service, e.g. <c>https://orders.example.com</c>.</param>
    public HttpSpecSource(string baseUrl)
        : this(new HttpClient { BaseAddress = new Uri(baseUrl) }, ownsClient: true)
    {
    }

    /// <summary>Creates a source over a caller-supplied <see cref="HttpClient"/> (e.g. a fake handler in tests).</summary>
    public HttpSpecSource(HttpClient httpClient, bool ownsClient = false)
    {
        _httpClient = httpClient;
        _ownsClient = ownsClient;
    }

    public async Task<string> GetSpecJsonAsync(SpecRequest request)
    {
        var type = string.IsNullOrWhiteSpace(request?.Type) ? "benzene" : request.Type;
        var format = string.IsNullOrWhiteSpace(request?.Format) ? "json" : request.Format;
        var path = $"{CloudServiceProbePaths.Spec}?type={Uri.EscapeDataString(type)}&format={Uri.EscapeDataString(format)}";
        var displayUrl = new Uri(_httpClient.BaseAddress!, path);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(path);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"GET {displayUrl} did not reach the service: {ex.Message}", ex);
        }

        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GET {displayUrl} returned {(int)response.StatusCode} {response.ReasonPhrase}, expected 200");
        }

        return body;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
