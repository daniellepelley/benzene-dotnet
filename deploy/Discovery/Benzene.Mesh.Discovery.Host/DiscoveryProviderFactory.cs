using Amazon.Lambda;
using Azure.Identity;
using Azure.ResourceManager;
using Benzene.Mesh.Contracts;
using Benzene.Mesh.Discovery.Aws;
using Benzene.Mesh.Discovery.Azure;
using Benzene.Mesh.Discovery.Kubernetes;
using k8s;

namespace Benzene.Mesh.Discovery.Host;

/// <summary>
/// Maps a <c>discovery.json</c> <c>providers[]</c> name to the matching
/// <c>Benzene.Mesh.Discovery.*</c> package's <see cref="IMeshDiscoveryProvider"/> - the one place
/// this job's provider names are known, mirroring
/// <c>Benzene.Mesh.Host.MeshSourceRegistrar</c>'s "plain switch over lowercased names, fail loudly on
/// an unknown one" convention. Every client is built off the ambient credential chain (an IAM role,
/// a managed identity, the in-cluster service account) - never a secret in config.
/// </summary>
public static class DiscoveryProviderFactory
{
    /// <summary>Valid <see cref="DiscoveryHostConfig.Providers"/> values, in the case <c>discovery.json</c> should use.</summary>
    public static readonly string[] ValidProviderNames = { "awsLambda", "azureAppService", "kubernetes" };

    /// <summary>Builds the configured providers, in the order they were named.</summary>
    /// <exception cref="InvalidOperationException">An unknown provider name.</exception>
    public static IReadOnlyList<IMeshDiscoveryProvider> Build(string[] providers)
    {
        var result = new List<IMeshDiscoveryProvider>(providers.Length);
        foreach (var name in providers)
        {
            result.Add(Build(name));
        }
        return result;
    }

    private static IMeshDiscoveryProvider Build(string name) => name.ToLowerInvariant() switch
    {
        "awslambda" => new AwsLambdaDiscoveryProvider(new AmazonLambdaClient()),
        "azureappservice" => new AzureAppServiceDiscoveryProvider(
            new AzureArmResourceLister(new ArmClient(new DefaultAzureCredential()))),
        "kubernetes" => new KubernetesServiceDiscoveryProvider(
            new KubernetesApiServiceLister(new k8s.Kubernetes(KubernetesClientConfiguration.InClusterConfig()))),
        _ => throw new InvalidOperationException(
            $"Unknown discovery provider '{name}'. Valid values: {string.Join(", ", ValidProviderNames)}."),
    };
}
