using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Mesh.Discovery.Kubernetes;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Moq;
using Xunit;

namespace Benzene.Mesh.Test.Discovery;

/// <summary>
/// <see cref="KubernetesApiServiceLister"/> - the real Kubernetes-SDK-backed
/// <see cref="IKubernetesServiceLister"/>. Had zero test coverage before this file (every other
/// discovery test exercises the port, not this adapter) and is exactly where #155 (pagination
/// ignored) lived: <c>limit</c>/<c>continueParameter</c> were never set on the outgoing request and
/// <c>ContinueProperty</c> on the response was never read, so a cluster whose API server ever
/// returned a continuation token would have every Service beyond the first page silently dropped.
/// </summary>
public class KubernetesApiServiceListerTest
{
    private static V1Service Svc(string name) =>
        new() { Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = "default" } };

    private static HttpOperationResponse<V1ServiceList> Page(string? continueToken, params V1Service[] items) =>
        new()
        {
            Body = new V1ServiceList
            {
                Items = items.ToList(),
                Metadata = new V1ListMeta { ContinueProperty = continueToken },
            },
        };

    [Fact]
    public async Task ListServicesAsync_AllNamespaces_SetsAnExplicitLimit()
    {
        var coreV1 = new Mock<ICoreV1Operations>();
        coreV1.Setup(x => x.ListServiceForAllNamespacesWithHttpMessagesAsync(
                null, null, null, "benzene", It.Is<int?>(l => l != null), null, null, null, null, null, null,
                null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(null, Svc("orders")));
        var client = new Mock<IKubernetes>();
        client.SetupGet(x => x.CoreV1).Returns(coreV1.Object);

        var lister = new KubernetesApiServiceLister(client.Object);
        var services = await lister.ListServicesAsync(@namespace: null, labelSelector: "benzene");

        Assert.Equal("orders", Assert.Single(services).Name);
        coreV1.VerifyAll();
    }

    [Fact]
    public async Task ListServicesAsync_AllNamespaces_FollowsContinueTokenUntilExhausted()
    {
        // #155: two pages - the first response's ContinueProperty must be fed back as the SECOND
        // request's continueParameter, and the loop must stop once a response's ContinueProperty is
        // empty. Before the fix, only ONE page was ever read no matter what the server returned.
        var coreV1 = new Mock<ICoreV1Operations>();
        coreV1.Setup(x => x.ListServiceForAllNamespacesWithHttpMessagesAsync(
                null, null, null, "benzene", It.IsAny<int?>(), null, null, null, null, null, null,
                null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page("page-2-token", Svc("orders")));
        coreV1.Setup(x => x.ListServiceForAllNamespacesWithHttpMessagesAsync(
                null, "page-2-token", null, "benzene", It.IsAny<int?>(), null, null, null, null, null, null,
                null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(null, Svc("billing")));

        var client = new Mock<IKubernetes>();
        client.SetupGet(x => x.CoreV1).Returns(coreV1.Object);

        var lister = new KubernetesApiServiceLister(client.Object);
        var services = await lister.ListServicesAsync(@namespace: null, labelSelector: "benzene");

        Assert.Equal(new[] { "billing", "orders" }, services.Select(s => s.Name).OrderBy(n => n));
        coreV1.Verify(x => x.ListServiceForAllNamespacesWithHttpMessagesAsync(
            null, null, null, "benzene", It.IsAny<int?>(), null, null, null, null, null, null,
            null, It.IsAny<CancellationToken>()), Times.Once);
        coreV1.Verify(x => x.ListServiceForAllNamespacesWithHttpMessagesAsync(
            null, "page-2-token", null, "benzene", It.IsAny<int?>(), null, null, null, null, null, null,
            null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListServicesAsync_SingleNamespace_FollowsContinueTokenUntilExhausted()
    {
        var coreV1 = new Mock<ICoreV1Operations>();
        coreV1.Setup(x => x.ListNamespacedServiceWithHttpMessagesAsync(
                "orders-ns", null, null, null, "benzene", It.IsAny<int?>(), null, null, null, null, null,
                null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page("page-2-token", Svc("orders")));
        coreV1.Setup(x => x.ListNamespacedServiceWithHttpMessagesAsync(
                "orders-ns", null, "page-2-token", null, "benzene", It.IsAny<int?>(), null, null, null, null, null,
                null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(null, Svc("billing")));

        var client = new Mock<IKubernetes>();
        client.SetupGet(x => x.CoreV1).Returns(coreV1.Object);

        var lister = new KubernetesApiServiceLister(client.Object);
        var services = await lister.ListServicesAsync(@namespace: "orders-ns", labelSelector: "benzene");

        Assert.Equal(new[] { "billing", "orders" }, services.Select(s => s.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task ListServicesAsync_EmptyContinueToken_StopsAfterOnePage()
    {
        var coreV1 = new Mock<ICoreV1Operations>();
        coreV1.Setup(x => x.ListServiceForAllNamespacesWithHttpMessagesAsync(
                null, null, null, "benzene", It.IsAny<int?>(), null, null, null, null, null, null,
                null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(string.Empty, Svc("orders"))); // empty (not null) continue token
        var client = new Mock<IKubernetes>();
        client.SetupGet(x => x.CoreV1).Returns(coreV1.Object);

        var lister = new KubernetesApiServiceLister(client.Object);
        var services = await lister.ListServicesAsync(@namespace: null, labelSelector: "benzene");

        Assert.Equal("orders", Assert.Single(services).Name);
        coreV1.Verify(x => x.ListServiceForAllNamespacesWithHttpMessagesAsync(
            It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(),
            It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<int?>(),
            It.IsAny<bool?>(), It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
