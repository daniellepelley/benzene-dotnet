using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Core;
using Benzene.HealthChecks.Core;
using Benzene.HealthChecks.Tcp;
using Xunit;

namespace Benzene.Test.HealthChecks.Tcp;

public class TcpHealthCheckTest
{
    [Fact]
    public async Task ExecuteAsync_PortAccepting_ReturnsHealthy()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var result = await new TcpHealthCheck("127.0.0.1", port).ExecuteAsync(CancellationToken.None);

            Assert.Equal(HealthCheckStatus.Ok, result.Status);
            var dependency = Assert.Single(result.Dependencies);
            Assert.Equal("Tcp", dependency.Kind);
            Assert.Equal($"127.0.0.1:{port}", dependency.Name);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ExecuteAsync_ConnectionRefused_ReturnsUnhealthy_WithTheDependency()
    {
        // Bind to grab a free port, then release it so nothing is listening -> connection refused.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await new TcpHealthCheck("127.0.0.1", port).ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Failed, result.Status);
        Assert.True(result.Data.ContainsKey("Error"));
        Assert.Equal($"127.0.0.1:{port}", Assert.Single(result.Dependencies).Name);
    }

    [Fact]
    public async Task ExecuteAsync_AmbientTokenAlreadyCancelled_PropagatesCancellation_InsteadOfReportingAnOrdinaryFailure()
    {
        // TcpHealthCheck is the only backend check that both accepts an ambient CancellationToken and
        // passes it into a cancelable call - so it is the only one where swallowing OperationCanceledException
        // into a generic Failed result (indistinguishable from a real dead dependency) is actually reachable.
        // ExceptionHandlingHealthCheck (which every check runs under via HealthCheckProcessor) exists
        // specifically to classify a propagated cancellation as a distinct "Cancelled" outcome - so this
        // check must let it propagate, the same way HttpPingHealthCheck already does, rather than catching
        // it itself.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var accessor = new CancellationTokenAccessor { CancellationToken = cts.Token };

        var check = new TcpHealthCheck("127.0.0.1", 1, accessor);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.ExecuteAsync(CancellationToken.None));
    }
}
