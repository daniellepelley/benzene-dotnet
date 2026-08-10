using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;

namespace Benzene.Clients.InProcess;

/// <summary>
/// Provides extension methods for wiring <see cref="InProcessClientMiddleware"/> into outbound routing.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Converts an outbound route pipeline (<c>OutboundRoutingBuilder.Route</c>) to dispatch straight
    /// to an in-process handler registered via <c>AddInProcessMessaging</c>, without leaving the process.
    /// </summary>
    /// <param name="app">The outbound pipeline builder to convert.</param>
    /// <returns>The pipeline builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<OutboundContext> UseInProcess(this IMiddlewarePipelineBuilder<OutboundContext> app)
    {
        return app.Convert(new InProcessContextConverter(), builder => builder.Use(resolver =>
            new InProcessClientMiddleware(
                resolver.GetService<IMiddlewareApplication<IBenzeneMessageRequest, IBenzeneMessageResponse>>(),
                resolver.GetService<IServiceResolverFactory>())));
    }
}
