using Benzene.Abstractions.Results;

namespace Benzene.Abstractions.MessageHandlers;

/// <summary>
/// The interface application code implements to handle a message and produce a typed response.
/// </summary>
/// <typeparam name="TRequest">The strongly-typed request this handler accepts.</typeparam>
/// <typeparam name="TResponse">The strongly-typed response this handler returns.</typeparam>
public interface IMessageHandler<TRequest, TResponse>
{
    /// <summary>Handles the given request and returns a typed result.</summary>
    /// <param name="request">The strongly-typed request to handle.</param>
    /// <returns>The result of handling the request, including its response payload.</returns>
    Task<IBenzeneResult<TResponse>> HandleAsync(TRequest request);
}

/// <summary>
/// The interface application code implements to handle a message that produces no response payload
/// (fire-and-forget style, e.g. an event handler). Handler discovery wraps implementations of this
/// interface so they can still flow through the same <see cref="IMessageHandler{TRequest, TResponse}"/>
/// based pipeline as request/response handlers (see <c>IMessageHandlerWrapper</c>).
/// </summary>
/// <typeparam name="TRequest">The strongly-typed request this handler accepts.</typeparam>
public interface IMessageHandler<TRequest>
{
    /// <summary>Handles the given request. No response payload is produced.</summary>
    /// <param name="request">The strongly-typed request to handle.</param>
    Task HandleAsync(TRequest request);
}

/// <summary>
/// The non-generic, transport-facing entry point for invoking a resolved message handler. This is
/// what <see cref="IMessageHandlerFactory"/> returns and what a router/dispatcher (e.g.
/// <c>MessageRouter&lt;TContext&gt;</c>) calls: it hides the handler's concrete request/response types
/// behind <see cref="IDeferredRequestMapper"/>, since the router only knows the topic being handled, not
/// the handler's generic type arguments, until it resolves the handler.
/// </summary>
public interface IMessageHandler
{
    /// <summary>
    /// Maps the incoming message to the handler's request type via <paramref name="deferredRequestMapper"/>
    /// and invokes the handler, returning its result as an untyped <see cref="IBenzeneResult"/>.
    /// </summary>
    /// <param name="deferredRequestMapper">
    /// Deferred request mapper that can produce the handler's specific request type on demand.
    /// </param>
    /// <returns>The outcome of handling the message.</returns>
    Task<IBenzeneResult> HandleAsync(IDeferredRequestMapper deferredRequestMapper);
}