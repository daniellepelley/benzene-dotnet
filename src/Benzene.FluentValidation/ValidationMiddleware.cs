using System;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Results;
using Benzene.Abstractions.Validation;
using Benzene.Results;

namespace Benzene.FluentValidation;

public class ValidationMiddleware<TRequest, TResponse> : IMiddleware<IMessageHandlerContext<TRequest, TResponse>> 
    where TRequest : class
{
    private readonly IServiceResolver _serviceResolver;
    private readonly IValidationStatusMapper _validationStatusMapper;

    public ValidationMiddleware(IServiceResolver serviceResolver, IValidationStatusMapper validationStatusMapper)
    {
        _serviceResolver = serviceResolver;
        _validationStatusMapper = validationStatusMapper;
    }

    public string Name => "FluentValidation";

    public async Task HandleAsync(IMessageHandlerContext<TRequest, TResponse> context, Func<Task> next)
    {
        var validator = _serviceResolver.TryGetService<IValidator<TRequest>>();
        if (validator != null)
        {
            if (context.Request == default)
            {
                var status = _validationStatusMapper.GetStatus(context.HandlerType, typeof(TRequest), null);
                context.Response = BenzeneResult.SetFailed<TResponse>(status, "Request is null");
                return;
            }
                
            var validationResult = await validator.ValidateAsync(context.Request);
            if (!validationResult.IsValid)
            {
                var status = _validationStatusMapper.GetStatus(context.HandlerType, typeof(TRequest), validationResult);
                // FluentValidation's PropertyName/ErrorCode are emitted verbatim as Field/Code
                // (work/benzene-result-errors-ruling.md §5.1) - ErrorCode defaults to the validator
                // type name (e.g. "NotEmptyValidator"), not stripped or normalized.
                var errors = validationResult.Errors
                    .Select(x => new BenzeneError(
                        x.ErrorMessage,
                        string.IsNullOrEmpty(x.PropertyName) ? null : x.PropertyName,
                        string.IsNullOrEmpty(x.ErrorCode) ? null : x.ErrorCode))
                    .ToArray();
                // Set<TResponse>(status, errors), not SetFailed<TResponse> - the arbitrary-status
                // (IValidationStatusMapper-driven) structured overload, so status stays whatever the
                // mapper resolved while errors keep their Field/Code.
                context.Response = BenzeneResult.Set<TResponse>(status, errors);
                return;
            }
        }
        await next();
    }
}