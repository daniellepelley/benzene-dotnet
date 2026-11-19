using System.Collections.Generic;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Results;

namespace Benzene.Test.Examples;

public enum QueryEchoStatus
{
    Open,
    Blocked
}

// FR-1: binds query values onto scalar and collection properties, and echoes what it bound.
public class QueryEchoRequest
{
    public string Id { get; set; }
    public string[] Ids { get; set; }
    public List<int> Numbers { get; set; }
    public IReadOnlyList<QueryEchoStatus> Statuses { get; set; }
}

[Message(Defaults.QueryEchoTopic)]
[HttpEndpoint("GET", Defaults.QueryEchoPath)]
public class QueryEchoMessageHandler : IMessageHandler<QueryEchoRequest, QueryEchoRequest>
{
    public Task<IBenzeneResult<QueryEchoRequest>> HandleAsync(QueryEchoRequest request)
    {
        return Task.FromResult(BenzeneResult.Ok(request));
    }
}
