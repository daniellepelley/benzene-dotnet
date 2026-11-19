using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Results;

namespace Benzene.Test.Examples;

public class ProblemBodyRequest
{
    public string Name { get; set; }
}

public class ProblemBodyResponse
{
    public string Id { get; set; }
}

// FR-9: a validation failure with a field-level error.
[Message(Defaults.ProblemValidationTopic)]
[HttpEndpoint("POST", Defaults.ProblemValidationPath)]
public class ProblemValidationMessageHandler : IMessageHandler<ProblemBodyRequest, ProblemBodyResponse>
{
    public Task<IBenzeneResult<ProblemBodyResponse>> HandleAsync(ProblemBodyRequest request)
    {
        return Task.FromResult(BenzeneResult.ValidationError<ProblemBodyResponse>(
            new[] { new BenzeneError("Name must not be empty", "/name", "NotEmpty") }));
    }
}

public class OutOfStockProblem : ProblemDetails
{
    public const string TypeUri = "https://example.com/problems/out-of-stock";

    [JsonPropertyName("sku")]
    public string Sku { get; set; }
}

// FR-9: a handler that authors its own problem document, with an extension member.
[Message(Defaults.ProblemAuthoredTopic)]
[HttpEndpoint("POST", Defaults.ProblemAuthoredPath)]
public class ProblemAuthoredMessageHandler : IMessageHandler<ProblemBodyRequest, ProblemBodyResponse>
{
    public Task<IBenzeneResult<ProblemBodyResponse>> HandleAsync(ProblemBodyRequest request)
    {
        return Task.FromResult(BenzeneResult.Problem<ProblemBodyResponse>(new OutOfStockProblem
        {
            Type = OutOfStockProblem.TypeUri,
            Title = "Out of stock",
            Detail = "SKU-1 is out of stock",
            BenzeneStatus = BenzeneResultStatus.Conflict,
            Sku = "SKU-1",
        }));
    }
}
