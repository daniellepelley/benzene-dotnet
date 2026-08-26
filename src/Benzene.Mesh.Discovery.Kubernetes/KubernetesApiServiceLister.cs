using k8s;
using k8s.Models;

namespace Benzene.Mesh.Discovery.Kubernetes;

/// <summary>
/// The real <see cref="IKubernetesServiceLister"/> over the Kubernetes client SDK's
/// <see cref="IKubernetes"/>. Kept thin (no discovery logic) so the SDK coupling lives in one place
/// and <see cref="KubernetesServiceDiscoveryProvider"/> stays unit-testable against the port.
/// </summary>
public class KubernetesApiServiceLister : IKubernetesServiceLister
{
    // The API server returns all matching Services in one page unless a limit is set - so without an
    // explicit one, this lister could silently drop every Service beyond whatever page size the server
    // (or a proxy in front of it) happens to choose to enforce on its own. Set our own bound instead of
    // depending on that, and follow `continue` until the server reports none left.
    private const int PageSize = 500;

    private readonly IKubernetes _client;

    /// <summary>Initializes the lister over a Kubernetes client.</summary>
    /// <param name="client">The Kubernetes API client.</param>
    public KubernetesApiServiceLister(IKubernetes client)
    {
        _client = client;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KubernetesServiceInfo>> ListServicesAsync(
        string? @namespace, string labelSelector, CancellationToken cancellationToken = default)
    {
        var services = new List<KubernetesServiceInfo>();
        string? continueToken = null;

        do
        {
            var list = @namespace == null
                ? await _client.CoreV1.ListServiceForAllNamespacesAsync(
                    labelSelector: labelSelector, limit: PageSize, continueParameter: continueToken,
                    cancellationToken: cancellationToken)
                : await _client.CoreV1.ListNamespacedServiceAsync(
                    @namespace, labelSelector: labelSelector, limit: PageSize, continueParameter: continueToken,
                    cancellationToken: cancellationToken);

            foreach (var service in list.Items ?? new List<V1Service>())
            {
                var name = service.Metadata?.Name;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                services.Add(new KubernetesServiceInfo(
                    name,
                    service.Metadata?.NamespaceProperty ?? "default",
                    service.Spec?.Ports?.FirstOrDefault()?.Port ?? 80,
                    service.Metadata?.Labels is { } labels ? new Dictionary<string, string>(labels) : new Dictionary<string, string>()));
            }

            // An empty continue token means "no more pages" per the API's own contract - a non-empty
            // one must be fed back into the next call with the SAME query parameters to get the rest.
            continueToken = string.IsNullOrEmpty(list.Metadata?.ContinueProperty) ? null : list.Metadata.ContinueProperty;
        }
        while (continueToken != null);

        return services;
    }
}
