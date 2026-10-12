using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;

namespace Benzene.Core.Middleware;

/// <summary>
/// Provides middleware that converts from one context type to another, executes a pipeline with the converted context,
/// and maps the response back to the original context.
/// </summary>
/// <typeparam name="TContext">The input context type to convert from.</typeparam>
/// <typeparam name="TContextOut">The output context type to convert to.</typeparam>
/// <remarks>
/// This middleware enables context transformation within a middleware pipeline, allowing different stages
/// of processing to operate on different context types. The converter handles both the transformation
/// to the new context and the mapping of results back to the original context.
/// </remarks>
public class ContextConverterMiddleware<TContext, TContextOut>(
    IContextConverter<TContext, TContextOut> converter,
    IMiddlewarePipeline<TContextOut> middlewarePipeline,
    IServiceResolver serviceResolver)
    : IMiddleware<TContext>, ITerminalMiddleware
{
    /// <summary>
    /// Gets the name of this middleware component.
    /// </summary>
    public string Name => "Convert";

    /// <summary>
    /// Handles the middleware execution by converting the context, executing the pipeline with the converted context,
    /// and mapping the response back.
    /// </summary>
    /// <param name="context">The input context to convert.</param>
    /// <param name="next">The next middleware in the pipeline (not invoked by this middleware).</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        var contextOut = await converter.CreateRequestAsync(context);
        if (converter is ITransportFailureMapper<TContext> failureMapper)
        {
            // A converter whose inner pipeline is a transport send turns a send that fails in transport
            // into a result rather than an exception (transport-bindings §1, spec 1.1). Only the send is
            // covered: building the request above and reading a reply below still propagate.
            try
            {
                await middlewarePipeline.HandleAsync(contextOut, serviceResolver);
            }
            catch (Exception exception) when (!IsAmbientCancellation(exception))
            {
                failureMapper.MapTransportFailure(context, exception);
                return;
            }
        }
        else
        {
            await middlewarePipeline.HandleAsync(contextOut, serviceResolver);
        }

        await converter.MapResponseAsync(context, contextOut);
    }

    // The caller's own cancellation - the ambient token (an upstream cancel, or a UseTimeout() deadline
    // around the send) - is not a transport failure: it propagates to whoever owns the token, as before.
    private bool IsAmbientCancellation(Exception exception)
    {
        return exception is OperationCanceledException
            && serviceResolver.TryGetService<ICancellationTokenAccessor>()?.CancellationToken.IsCancellationRequested == true;
    }
}