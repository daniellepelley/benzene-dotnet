using System.Net.Sockets;
using System.Threading;
using Benzene.Abstractions.DI;
using Benzene.HealthChecks.Core;

namespace Benzene.HealthChecks.Tcp;

/// <summary>
/// Verifies a dependency is reachable at the L4 (TCP) level by opening a connection to a host and port -
/// the lowest-common-denominator check for anything without a first-class client (a database port, an
/// SMTP server, a custom service). Healthy if the connection is accepted; unhealthy on any socket error.
/// A cancellation of the ambient <see cref="ICancellationTokenAccessor.CancellationToken"/> (e.g.
/// graceful shutdown) is not treated as a socket error - it propagates uncaught, the same way
/// <c>HttpPingHealthCheck</c> lets any failure propagate, so <c>ExceptionHandlingHealthCheck</c> (which
/// every check runs under via <c>HealthCheckProcessor</c>) can classify it as its own distinct
/// "Cancelled" outcome instead of an opaque, alarming-looking connectivity failure.
/// </summary>
public class TcpHealthCheck : IHealthCheck
{
    private readonly string _host;
    private readonly int _port;
    private readonly ICancellationTokenAccessor? _cancellation;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="host">The host to connect to.</param>
    /// <param name="port">The TCP port to connect to.</param>
    /// <param name="cancellation">Supplies the ambient cancellation token for the connect; null observes no cancellation.</param>
    public TcpHealthCheck(string host, int port, ICancellationTokenAccessor? cancellation = null)
    {
        _host = host;
        _port = port;
        _cancellation = cancellation;
    }

    /// <inheritdoc />
    public string Type => "Tcp";

    /// <inheritdoc />
    public async Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var dependencies = new[] { new HealthCheckDependency("Tcp", $"{_host}:{_port}") };

        // Link the token ExecuteAsync was called with (the processor's per-check timeout) with the
        // ambient accessor's token (e.g. application shutdown), if one was supplied - either source
        // cancels the connect.
        using var cts = _cancellation != null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation.CancellationToken)
            : null;
        var token = cts?.Token ?? cancellationToken;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_host, _port, token);

            return HealthCheckResult.CreateInstance(true, Type,
                new Dictionary<string, object> { { "Host", _host }, { "Port", _port } }, dependencies);
        }
        catch (OperationCanceledException)
        {
            // Let cancellation (ambient token / shutdown) propagate rather than reporting it as an
            // ordinary connectivity failure - ExceptionHandlingHealthCheck (which every check runs
            // under via HealthCheckProcessor) classifies it as a distinct "Cancelled" outcome, the
            // same way it does for every other check that doesn't observe cancellation itself (e.g.
            // HttpPingHealthCheck). Swallowing it here into a generic {"Error": "TaskCanceledException"}
            // Failed result - indistinguishable from a real dead dependency - was this check's own
            // outlier: it was the only backend check that both accepted an ambient CancellationToken
            // and passed it into a cancelable call, so it was the only one where the inconsistency was
            // actually reachable.
            throw;
        }
        catch (Exception ex)
        {
            // Report the failure type, not the message (a message can carry infra detail); an expected
            // "connection refused" is a failed result, not a thrown exception.
            return HealthCheckResult.CreateInstance(false, Type,
                new Dictionary<string, object> { { "Host", _host }, { "Port", _port }, { "Error", ex.GetType().Name } },
                dependencies);
        }
    }
}
