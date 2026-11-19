using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Results;

namespace Benzene.Core.MessageHandlers.Response;

/// <summary>
/// Writes the response headers a handler attached to its result
/// (<see cref="BenzeneResultExtensions.WithHeader{T}"/>) onto the transport's response. Registered after
/// the renderer on every transport with response headers, so it runs once the status, body and content
/// type are set.
/// </summary>
/// <remarks>
/// It never replaces a header the pipeline already set: CORS headers (written before the handler runs)
/// and the rendered <c>content-type</c> win, and a result header with the same name is skipped. Checked through <see cref="IBenzeneResponseAdapter{TContext}.HasResponseHeader"/>,
/// which also keeps the Azure Functions adapter (whose header write throws on a duplicate name) safe.
/// </remarks>
/// <typeparam name="TContext">The transport-specific context type.</typeparam>
public class ResultHeadersResponseHandler<TContext> : IResponseHandler<TContext> where TContext : class
{
    private readonly IBenzeneResponseAdapter<TContext> _benzeneResponseAdapter;

    public ResultHeadersResponseHandler(IBenzeneResponseAdapter<TContext> benzeneResponseAdapter)
    {
        _benzeneResponseAdapter = benzeneResponseAdapter;
    }

    public ValueTask HandleAsync(TContext context, IMessageHandlerResult messageHandlerResult)
    {
        var headers = messageHandlerResult.BenzeneResult.GetResponseHeaders();
        foreach (var (name, value) in headers)
        {
            // The pipeline's own header wins (see remarks).
            if (!_benzeneResponseAdapter.HasResponseHeader(context, name))
            {
                _benzeneResponseAdapter.SetResponseHeader(context, name, value);
            }
        }

        return ValueTask.CompletedTask;
    }
}
