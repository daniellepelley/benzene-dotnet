using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Middleware;

namespace Benzene.DataAnnotations;

public class ValidationMiddlewareBuilder : IHandlerMiddlewareBuilder
{
    public IMiddleware<IMessageHandlerContext<TRequest, TResponse>> Create<TRequest, TResponse>(IServiceResolver serviceResolver, IMessageHandler<TRequest, TResponse> messageHandler)
        where TRequest : class
    {
        // TryGetService: a replaced IDefaultStatuses customizes the validation status here exactly
        // as it does in the core pipeline; absent (unusual, AddBenzene registers it), the middleware
        // falls back to the built-in validation-error status.
        return new ValidationMiddleware<TRequest, TResponse>(
            serviceResolver.TryGetService<Benzene.Core.MessageHandlers.IDefaultStatuses>());
    }
}
