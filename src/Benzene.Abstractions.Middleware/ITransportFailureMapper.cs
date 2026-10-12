namespace Benzene.Abstractions.Middleware;

/// <summary>
/// Implemented by an <see cref="IContextConverter{TContextIn,TContextOut}"/> whose inner pipeline is a
/// transport send, to turn a send that fails in transport into a result on <typeparamref name="TContextIn"/>
/// instead of an exception (transport-bindings.md §1, spec 1.1: a send that fails in transport - no
/// connection, a refused or reset request, the client's own deadline, before any reply arrives or, for a
/// one-way client, before the broker accepts the message - SHOULD yield <c>timeout</c> or
/// <c>service-unavailable</c> rather than throw).
/// </summary>
/// <typeparam name="TContextIn">The outer context the failure is recorded on.</typeparam>
/// <remarks>
/// <c>ContextConverterMiddleware</c> calls <see cref="MapTransportFailure"/> when the inner pipeline throws,
/// in place of <see cref="IContextConverter{TContextIn,TContextOut}.MapResponseAsync"/>. Only the inner
/// pipeline - the send itself - is covered: a failure building the request (serialization) or reading a
/// received reply still propagates, since neither is a transport failure. So does the caller's own
/// cancellation (an <see cref="OperationCanceledException"/> while the ambient cancellation token is
/// cancelled), which belongs to whoever owns that token - e.g. a route's <c>UseTimeout()</c>, whose
/// deadline the outbound sender then reports as a <c>timeout</c> result.
/// </remarks>
public interface ITransportFailureMapper<in TContextIn>
{
    /// <summary>Records the failed send's result on <paramref name="contextIn"/>.</summary>
    /// <param name="contextIn">The outer context.</param>
    /// <param name="exception">What the transport send threw.</param>
    void MapTransportFailure(TContextIn contextIn, Exception exception);
}
