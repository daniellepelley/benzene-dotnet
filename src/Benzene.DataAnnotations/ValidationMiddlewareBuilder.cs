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
        // falls back to the built-in validation-error status. IValidationStatusMapper is optional
        // too - typically only registered when Benzene.FluentValidation's AddFluentValidation also
        // ran - and, when present, wins so [ValidationStatus] on the handler is honoured here too.
        // The field-pointer naming (wire-contracts §1.3) follows the service's JSON serializer - see
        // FieldPointer.ResolveNames.
        return new ValidationMiddleware<TRequest, TResponse>(
            serviceResolver.TryGetService<Benzene.Core.MessageHandlers.IDefaultStatuses>(),
            serviceResolver.TryGetService<Benzene.Abstractions.Validation.IValidationStatusMapper>(),
            Benzene.Abstractions.Validation.FieldPointer.ResolveNames(serviceResolver));
    }
}
