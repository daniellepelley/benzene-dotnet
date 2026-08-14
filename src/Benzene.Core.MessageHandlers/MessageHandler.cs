using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Results;
using Microsoft.Extensions.Logging;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Core.MessageHandlers;

/// <summary>
/// Adapts a strongly-typed <see cref="IMessageHandler{TRequest,TResponse}"/> to the
/// non-generic <see cref="IMessageHandler"/> the router works with, deserializing the request via
/// an <see cref="IDeferredRequestMapper"/> and translating unhandled exceptions into
/// <see cref="IBenzeneResult"/> error responses instead of letting them propagate out of the pipeline.
/// </summary>
/// <typeparam name="TRequest">The strongly-typed request the inner handler expects.</typeparam>
/// <typeparam name="TResponse">The strongly-typed response the inner handler returns.</typeparam>
/// <remarks>
/// This is the last piece of adaptation before a handler is invoked by <see cref="MessageRouter{TContext}"/>:
/// deserialization failures become a <see cref="IDefaultStatuses.BadRequest"/> result,
/// <see cref="ArgumentException"/>s thrown by the handler become a <see cref="IDefaultStatuses.ValidationError"/>
/// result, a <see cref="TimeoutException"/> (as thrown by <c>Benzene.Resilience.TimeoutMiddleware</c>
/// when a configured deadline elapses) becomes a precise <c>BenzeneResultStatus.Timeout</c> result,
/// and any other exception becomes a service-unavailable result. A <c>null</c> result from the
/// inner handler is treated as an accepted no-content response. A genuine host cancellation (an
/// <see cref="OperationCanceledException"/> whose token has actually fired) is deliberately NOT
/// converted into any of these - it propagates so a settle/ack/checkpoint transport redelivers the
/// interrupted work instead of treating it as done or failed.
/// </remarks>
internal class MessageHandler<TRequest, TResponse> : IMessageHandler where TRequest : class
{
    private readonly IMessageHandler<TRequest, TResponse> _inner;
    private readonly ILogger _logger;
    private readonly IDefaultStatuses _defaultStatuses;
    private readonly MessageErrorState? _errorState;

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageHandler{TRequest,TResponse}"/> class.
    /// </summary>
    /// <param name="inner">The strongly-typed handler to invoke.</param>
    /// <param name="logger">Logger used to record mapping failures and handler exceptions.</param>
    /// <param name="defaultStatuses">Supplies the status codes used for the error results produced here.</param>
    /// <param name="errorState">Optional scoped record of a converted exception's type (for readers that
    /// outlive the span — the mesh feeds); null in direct-construction/test shapes.</param>
    public MessageHandler(IMessageHandler<TRequest, TResponse> inner, ILogger logger, IDefaultStatuses defaultStatuses,
        MessageErrorState? errorState = null)
    {
        _defaultStatuses = defaultStatuses;
        _logger = logger;
        _inner = inner;
        _errorState = errorState;
    }

    /// <summary>
    /// Maps the request out of <paramref name="deferredRequestMapper"/> and invokes the inner handler,
    /// converting mapping failures and handler exceptions into an <see cref="IBenzeneResult"/> rather
    /// than throwing.
    /// </summary>
    /// <param name="deferredRequestMapper">Supplies the deserialized <typeparamref name="TRequest"/> for the current message.</param>
    /// <returns>
    /// The handler's result; a bad-request result if the message could not be deserialized; a
    /// validation-error result if the handler threw an <see cref="ArgumentException"/>; or a
    /// service-unavailable result for any other unhandled exception.
    /// </returns>
    public async Task<IBenzeneResult> HandleAsync(IDeferredRequestMapper deferredRequestMapper)
    {
        TRequest? messageObject;
        try
        {
            messageObject = deferredRequestMapper.GetRequest<TRequest>();
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken.IsCancellationRequested)
        {
            // A genuine cancellation (host shutdown / drain fired the seeded token) is not a
            // "message is not valid" outcome to convert into a BadRequest - propagate so the one
            // place that reasons about it, ExceptionHandlerMiddleware, lets a settle/ack/checkpoint
            // transport redeliver the interrupted work. See ExceptionHandlerMiddleware's remarks.
            throw;
        }
        catch(Exception ex)
        {
            ActivityExceptionTag.TryStamp(ex);
            _errorState?.TrySet(ex, hint: "deserialization"); // spec §4.1 registered remediation-hint key
            _logger.LogWarning(ex, "Message is not valid");
            return BenzeneResult.Set(_defaultStatuses.BadRequest, "Message is not valid", ex.Message);
        }

        try
        {
            var result = await _inner.HandleAsync(messageObject);
            if (result == null)
            {
                return BenzeneResult.Accepted<Void>();
            }

            return result;
        }
        catch(ArgumentException ex)
        {
            ActivityExceptionTag.TryStamp(ex);
            _errorState?.TrySet(ex);
            _logger.LogError(ex, "Message handler threw argument exception");
            return BenzeneResult.Set(_defaultStatuses.ValidationError, ex.Message);
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken.IsCancellationRequested)
        {
            // A genuine cancellation is host shutdown / drain, not a handler failure. Propagate it
            // (rather than reporting ServiceUnavailable and logging a scary "handler threw" error for
            // every in-flight message on every deploy) so ExceptionHandlerMiddleware can let the
            // transport redeliver the interrupted work. Mirrors ExceptionHandlerMiddleware's guard.
            throw;
        }
        catch (TimeoutException ex)
        {
            // Distinct from the OperationCanceledException case above: TimeoutMiddleware
            // (Benzene.Resilience) already told the two apart by checking whether the *original*
            // token (pre-dating its own linked CTS) had itself fired. A TimeoutException here means
            // it hadn't - this is an operator-configured deadline, a service-side failure, not host
            // cancellation - so it becomes a precise `timeout` status result instead of the generic
            // ServiceUnavailable below (and instead of propagating and forcing redelivery the way a
            // genuine cancellation must).
            ActivityExceptionTag.TryStamp(ex);
            // No hint: "deserialization" is the one spec-§4.1-registered remediation-hint key in use
            // today (see the catch above) - inventing a new key here isn't this phase's call to make,
            // so this follows the same no-hint default as the ArgumentException/generic catches below.
            _errorState?.TrySet(ex);
            _logger.LogWarning(ex, "Message handler exceeded its configured timeout");
            return BenzeneResult.Timeout(ex.Message);
        }
        catch(Exception ex)
        {
            ActivityExceptionTag.TryStamp(ex);
            _errorState?.TrySet(ex);
            _logger.LogError(ex, "Message handler threw an exception");
            return BenzeneResult.ServiceUnavailable("Message handler threw an exception", ex.Message);
        }
    }
}
