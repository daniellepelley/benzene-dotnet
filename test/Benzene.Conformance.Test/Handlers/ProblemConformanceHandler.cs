using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Results;

namespace Benzene.Conformance.Test.Handlers;

public class ProblemRequest
{
    public string Message { get; set; } = string.Empty;
    public string? Field { get; set; }
    public string? Code { get; set; }
    public string? AppType { get; set; }
}

/// <summary>
/// Canonical conformance handler (see docs/specification/conformance/README.md and
/// problem-details-cases.json's <c>envelopeCases</c> group): always returns a <c>validation-error</c>
/// result carrying exactly one structured error built from the request's <see cref="ProblemRequest.Message"/>/
/// <see cref="ProblemRequest.Field"/>/<see cref="ProblemRequest.Code"/>. When <see cref="ProblemRequest.AppType"/>
/// is given, the emitted problem document's <c>type</c> member is that value verbatim instead of the
/// registry URI - the application-authored-problem case (wire-contracts.md §1.3) - via
/// <see cref="BenzeneResult.Problem{T}"/>; <c>benzeneStatus</c> is still <c>validation-error</c> and
/// <c>errors</c> still carries the one structured error either way.
/// </summary>
[Message("conformance:problem")]
public class ProblemConformanceHandler : IMessageHandler<ProblemRequest, Benzene.Abstractions.Results.Void>
{
    public Task<IBenzeneResult<Benzene.Abstractions.Results.Void>> HandleAsync(ProblemRequest request)
    {
        var error = new BenzeneError(request.Message, request.Field, request.Code);

        if (!string.IsNullOrEmpty(request.AppType))
        {
            var problem = new ProblemDetails
            {
                Type = request.AppType,
                BenzeneStatus = BenzeneResultStatus.ValidationError,
                Errors = new[] { error },
            };

            return Task.FromResult(BenzeneResult.Problem<Benzene.Abstractions.Results.Void>(problem));
        }

        return Task.FromResult(BenzeneResult.ValidationError<Benzene.Abstractions.Results.Void>(new[] { error }));
    }
}
