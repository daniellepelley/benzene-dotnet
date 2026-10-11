using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Results;

namespace Benzene.Http;

/// <summary>
/// Sets the HTTP status code on the response based on the message handler result.
/// </summary>
/// <typeparam name="TContext">The context type for the HTTP request/response.</typeparam>
/// <remarks>
/// This response handler translates Benzene result statuses to HTTP status codes using
/// an <see cref="IHttpStatusCodeMapper"/> and applies them to the HTTP response through
/// a response adapter. It should be registered in the response handler pipeline to ensure
/// that HTTP responses have appropriate status codes based on the handler's result.
/// <para>
/// One exception maps a payload field rather than the status (transport-bindings.md §1): a successful
/// result whose payload is a health verdict (<see cref="IHealthVerdict"/>, the health response of the
/// reserved health topics) answers <b>503</b> when <see cref="IHealthVerdict.IsHealthy"/> is false and
/// the mapped status (200) otherwise - so any route mapped to a health topic tells a load balancer or
/// orchestrator the verdict on the status line, while the health query itself stays <c>ok</c>.
/// </para>
/// </remarks>
public class HttpStatusCodeResponseHandler<TContext> : IResponseHandler<TContext> where TContext : class
{
    private readonly IHttpStatusCodeMapper _httpStatusCodeMapper;
    private readonly IBenzeneResponseAdapter<TContext> _benzeneResponseAdapter;

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpStatusCodeResponseHandler{TContext}"/> class.
    /// </summary>
    /// <param name="benzeneResponseAdapter">The adapter used to set the status code on the HTTP response.</param>
    /// <param name="httpStatusCodeMapper">The mapper that translates Benzene result statuses to HTTP status codes.</param>
    public HttpStatusCodeResponseHandler(IBenzeneResponseAdapter<TContext> benzeneResponseAdapter, IHttpStatusCodeMapper httpStatusCodeMapper)
    {
        _benzeneResponseAdapter = benzeneResponseAdapter;
        _httpStatusCodeMapper = httpStatusCodeMapper;
    }

    /// <summary>
    /// Handles the response by setting the appropriate HTTP status code based on the message handler result.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="messageHandlerResult">The result from the message handler execution.</param>
    public ValueTask HandleAsync(TContext context, IMessageHandlerResult messageHandlerResult)
    {
        _benzeneResponseAdapter.SetStatusCode(context, MapStatusCode(messageHandlerResult.BenzeneResult));
        return default;
    }

    /// <summary>The HTTP status code for <paramref name="result"/>; see the class remarks for the health rule.</summary>
    /// <param name="result">The handler's result.</param>
    /// <returns>The HTTP status code as a string.</returns>
    protected virtual string MapStatusCode(IBenzeneResult result)
    {
        if (result.IsSuccessful && result.PayloadAsObject is IHealthVerdict { IsHealthy: false })
        {
            return UnhealthyStatusCode;
        }

        return _httpStatusCodeMapper.Map(result.Status, result.IsSuccessful);
    }

    /// <summary>The HTTP status a route mapped to a health topic answers with when the service is unhealthy.</summary>
    public const string UnhealthyStatusCode = "503";
}
