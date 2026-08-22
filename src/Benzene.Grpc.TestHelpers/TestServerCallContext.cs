using Grpc.Core;

namespace Benzene.Grpc.TestHelpers;

/// <summary>
/// A minimal, hand-rolled <see cref="ServerCallContext"/> for unit tests that don't need a real in-process
/// host (see <see cref="GrpcTestHost"/> for those). Only the members Benzene.Grpc actually reads
/// (<see cref="Method"/>, <see cref="Deadline"/>, <see cref="RequestHeaders"/>, <see cref="CancellationToken"/>,
/// <see cref="ResponseTrailers"/>, <see cref="WriteResponseHeadersAsync"/>) are meaningfully implemented;
/// anything else throws if touched. Grpc.Core.Testing is deliberately not a dependency of Benzene.Grpc.
/// </summary>
public class TestServerCallContext : ServerCallContext
{
    private readonly string _method;
    private readonly Metadata _requestHeaders;
    private readonly CancellationToken _cancellationToken;
    private readonly DateTime _deadline;
    private readonly Metadata _responseTrailers = new();

    /// <summary>Creates a <see cref="TestServerCallContext"/> with the given method, headers, cancellation token, and deadline.</summary>
    /// <param name="method">The gRPC method path. Defaults to a placeholder test method.</param>
    /// <param name="requestHeaders">The inbound request metadata; defaults to empty.</param>
    /// <param name="cancellationToken">The call's cancellation token; defaults to <see cref="System.Threading.CancellationToken.None"/>.</param>
    /// <param name="deadline">The call's deadline; defaults to <see cref="DateTime.MaxValue"/> (no deadline).</param>
    public static TestServerCallContext Create(
        string method = "/benzene.test.TestService/Echo",
        Metadata? requestHeaders = null,
        CancellationToken cancellationToken = default,
        DateTime? deadline = null)
    {
        return new TestServerCallContext(method, requestHeaders ?? new Metadata(), cancellationToken, deadline ?? DateTime.MaxValue);
    }

    private TestServerCallContext(string method, Metadata requestHeaders, CancellationToken cancellationToken, DateTime deadline)
    {
        _method = method;
        _requestHeaders = requestHeaders;
        _cancellationToken = cancellationToken;
        _deadline = deadline;
    }

    /// <summary>Gets the response headers written via <see cref="ServerCallContext.WriteResponseHeadersAsync"/>, for test assertions.</summary>
    public Metadata WrittenResponseHeaders { get; private set; } = new();

    /// <inheritdoc />
    protected override string MethodCore => _method;

    /// <inheritdoc />
    protected override string HostCore => "test-host";

    /// <inheritdoc />
    protected override string PeerCore => "test-peer";

    /// <inheritdoc />
    protected override DateTime DeadlineCore => _deadline;

    /// <inheritdoc />
    protected override Metadata RequestHeadersCore => _requestHeaders;

    /// <inheritdoc />
    protected override CancellationToken CancellationTokenCore => _cancellationToken;

    /// <inheritdoc />
    protected override Metadata ResponseTrailersCore => _responseTrailers;

    /// <inheritdoc />
    protected override Status StatusCore { get; set; }

    /// <inheritdoc />
    protected override WriteOptions? WriteOptionsCore { get; set; }

    /// <inheritdoc />
    protected override AuthContext AuthContextCore => throw new NotImplementedException();

    /// <inheritdoc />
    protected override IDictionary<object, object> UserStateCore => throw new NotImplementedException();

    /// <inheritdoc />
    protected override ContextPropagationToken? CreatePropagationTokenCore(ContextPropagationOptions? options)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
    {
        WrittenResponseHeaders = responseHeaders;
        return Task.CompletedTask;
    }
}
