using System.ComponentModel.DataAnnotations;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Results;

namespace Benzene.DataAnnotations;

public class ValidationMiddleware<TRequest, TResponse> : IMiddleware<IMessageHandlerContext<TRequest, TResponse>>
    where TRequest : class
{
    private readonly IDefaultStatuses? _defaultStatuses;

    /// <summary>Initializes a new instance using the default <c>validation-error</c> status.</summary>
    public ValidationMiddleware()
    {
    }

    /// <summary>
    /// Initializes a new instance whose validation-failure status comes from
    /// <paramref name="defaultStatuses"/> - so a replaced <see cref="IDefaultStatuses"/> registration
    /// customizes this middleware's status the same way it customizes the core pipeline's, instead of
    /// this package hard-coding <c>validation-error</c>.
    /// </summary>
    /// <param name="defaultStatuses">The status vocabulary to draw the validation status from.</param>
    public ValidationMiddleware(IDefaultStatuses? defaultStatuses)
    {
        _defaultStatuses = defaultStatuses;
    }

    public string Name => "DataAnnotationValidation";

    private string ValidationStatus => _defaultStatuses?.ValidationError ?? BenzeneResultStatus.ValidationError;

    public async Task HandleAsync(IMessageHandlerContext<TRequest, TResponse> context, Func<Task> next)
    {
        if (context.Request == default)
        {
            context.Response = BenzeneResult.SetFailed<TResponse>(ValidationStatus, "Request is null");
            return;
        }

        var validationResults = new List<ValidationResult>();
        var ctx = new ValidationContext(context.Request, null, null);
        Validator.TryValidateObject(context.Request, ctx, validationResults, true);
        if (validationResults.Any())
        {
            // One BenzeneError per member name (Code stays null - DataAnnotations' ValidationResult
            // doesn't expose the attribute that produced it, work/benzene-result-errors-ruling.md
            // §5.1); a result with no member names still yields one field-less error, not zero.
            var errors = validationResults
                .Where(x => x.ErrorMessage != null)
                .SelectMany(x =>
                {
                    var members = x.MemberNames?.Where(m => !string.IsNullOrEmpty(m)).ToArray()
                        ?? Array.Empty<string>();
                    return members.Length > 0
                        ? members.Select(member => new BenzeneError(x.ErrorMessage!, member))
                        : new[] { new BenzeneError(x.ErrorMessage!) };
                })
                .ToArray();
            context.Response = BenzeneResult.Set<TResponse>(ValidationStatus, errors);
            return;
        }
        await next();
    }
}
