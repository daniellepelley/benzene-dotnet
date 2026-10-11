using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Results;
using FluentValidation;

namespace Benzene.Conformance.Test.Handlers;

public class ValidateRequest
{
    public string? Name { get; set; }
    public List<ValidateItem>? Items { get; set; }
}

public class ValidateItem
{
    public string? Sku { get; set; }
}

/// <summary>
/// The canonical <c>conformance:validate</c> rules (docs/specification/conformance/README.md), written
/// in this port's own validation integration - FluentValidation, wired by <c>UseFluentValidation</c> -
/// rather than by hand in the handler: <c>name</c> non-empty, and every <c>items[].sku</c> present and
/// non-empty. Its failures reach the wire with <c>field</c> as JSON Pointers (wire-contracts.md §1.3).
/// </summary>
public class ValidateConformanceValidator : AbstractValidator<ValidateRequest>
{
    public ValidateConformanceValidator()
    {
        RuleFor(x => x.Name).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item => item.RuleFor(x => x.Sku).NotEmpty());
    }
}

/// <summary>
/// Canonical conformance handler (see docs/specification/conformance/README.md): returns <c>ok</c> when
/// the request is valid. Validation is entirely <see cref="ValidateConformanceValidator"/>'s - the
/// handler itself never inspects the request.
/// </summary>
[Message("conformance:validate")]
public class ValidateConformanceHandler : IMessageHandler<ValidateRequest, Benzene.Abstractions.Results.Void>
{
    public Task<IBenzeneResult<Benzene.Abstractions.Results.Void>> HandleAsync(ValidateRequest request)
    {
        return Task.FromResult(BenzeneResult.Ok<Benzene.Abstractions.Results.Void>());
    }
}
