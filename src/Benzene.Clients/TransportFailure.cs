using Benzene.Abstractions.Results;
using Benzene.Results;

namespace Benzene.Clients;

/// <summary>
/// The result of a send that failed in transport (transport-bindings.md §1, spec 1.1): no connection, a
/// refused or reset request, the client's own deadline - before any reply arrived or, for a one-way
/// client, before the broker accepted the message. Such a send yields a result rather than an exception,
/// so a caller handles a dead peer as it handles every other failure: <c>timeout</c> when the deadline
/// elapsed, otherwise <c>service-unavailable</c>.
/// </summary>
/// <remarks>
/// <para>
/// An outbound route's transport converter (an <c>ITransportFailureMapper&lt;OutboundContext&gt;</c>) sets
/// one on <see cref="OutboundContext.Response"/>; the route's caller, which alone knows the response type,
/// turns it into its <see cref="IBenzeneResult{T}"/> (<c>IBenzeneMessageSender.SendAsync</c> does). It is an
/// <see cref="IBenzeneResult"/> itself, so route middleware that inspects the response (a retry predicate,
/// <c>UseParallel</c>'s aggregate) sees an ordinary failure.
/// </para>
/// <para>
/// <b>Classification.</b> <c>timeout</c> when a <see cref="TimeoutException"/> is the exception or any of
/// its inner exceptions - how <c>HttpClient.Timeout</c>, a RabbitMQ publish-confirm wait and the cloud SDKs'
/// own request timeouts surface; everything else, the caller's ambient cancellation included, is
/// <c>service-unavailable</c> (the classification the gRPC and HTTP clients already gave an ambient cancel).
/// </para>
/// </remarks>
public sealed class TransportFailure : IBenzeneResult
{
    private TransportFailure(string status, Exception exception)
    {
        Status = status;
        Exception = exception;
        Errors = new[] { new BenzeneError(exception.Message) };
    }

    /// <summary>Creates the failure for what a transport send threw.</summary>
    /// <param name="exception">What the send threw.</param>
    public static TransportFailure From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new TransportFailure(StatusOf(exception), exception);
    }

    /// <summary>
    /// The failed <typeparamref name="T"/> result for what a transport send threw - <see cref="From"/>,
    /// as the caller's response type.
    /// </summary>
    /// <typeparam name="T">The caller's response type.</typeparam>
    /// <param name="exception">What the send threw.</param>
    public static IBenzeneResult<T> ToResult<T>(Exception exception) => From(exception).ToResult<T>();

    /// <summary>
    /// <c>timeout</c> when the deadline elapsed (a <see cref="TimeoutException"/> anywhere in the exception's
    /// chain), otherwise <c>service-unavailable</c>.
    /// </summary>
    /// <param name="exception">What the send threw.</param>
    public static string StatusOf(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is TimeoutException)
            {
                return BenzeneResultStatus.Timeout;
            }

            if (current is AggregateException aggregate && aggregate.InnerExceptions.Any(x => StatusOf(x) == BenzeneResultStatus.Timeout))
            {
                return BenzeneResultStatus.Timeout;
            }
        }

        return BenzeneResultStatus.ServiceUnavailable;
    }

    /// <summary>This failure as the caller's <typeparamref name="T"/> result: same status and errors, no payload.</summary>
    /// <typeparam name="T">The caller's response type.</typeparam>
    public IBenzeneResult<T> ToResult<T>() => BenzeneResult.Set<T>(Status, Errors);

    /// <summary>What the transport send threw.</summary>
    public Exception Exception { get; }

    /// <inheritdoc />
    public string Status { get; }

    /// <inheritdoc />
    public bool IsSuccessful => false;

    /// <inheritdoc />
    public object? PayloadAsObject => null;

    /// <inheritdoc />
    public IReadOnlyList<BenzeneError> Errors { get; }
}
