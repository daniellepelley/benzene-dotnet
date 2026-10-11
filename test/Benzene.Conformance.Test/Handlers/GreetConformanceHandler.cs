using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Schema;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Results;

namespace Benzene.Conformance.Test.Handlers;

public class GreetRequest
{
    public string Name { get; set; } = string.Empty;
}

// The canonical payload type is named GreetResponse (conformance/README.md); this runner keeps its
// CLR name and declares the schema name instead - contract-document.md §4.1 rule 5's mechanism.
[SchemaName("GreetResponse")]
public class GreetReply
{
    public string Greeting { get; set; } = string.Empty;
}

/// <summary>
/// Canonical conformance handler (see docs/specification/conformance/README.md): every Benzene
/// implementation registers this exact topic and behavior natively before running the envelope fixtures.
/// </summary>
[Message("conformance:greet")]
public class GreetConformanceHandler : IMessageHandler<GreetRequest, GreetReply>
{
    public Task<IBenzeneResult<GreetReply>> HandleAsync(GreetRequest request)
    {
        return Task.FromResult(BenzeneResult.Ok(new GreetReply { Greeting = $"Hello {request.Name}" }));
    }
}
