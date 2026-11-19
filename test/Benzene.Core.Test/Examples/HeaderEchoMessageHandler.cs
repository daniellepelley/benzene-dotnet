using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Results;

namespace Benzene.Test.Examples;

public class HeaderEchoRequest
{
    public string Name { get; set; }
}

public class HeaderEchoResponse
{
    public string Id { get; set; }
}

// FR-3: a handler attaching response headers to its result. It also tries to set a CORS header and the
// content type, which the pipeline owns and must keep.
[Message(Defaults.HeaderEchoTopic)]
[HttpEndpoint("GET", Defaults.HeaderEchoPath)]
public class HeaderEchoMessageHandler : IMessageHandler<HeaderEchoRequest, HeaderEchoResponse>
{
    public const string Location = "/things/1";
    public const string ForeignOrigin = "https://not-allowed.example";

    public Task<IBenzeneResult<HeaderEchoResponse>> HandleAsync(HeaderEchoRequest request)
    {
        return Task.FromResult(BenzeneResult.Created(new HeaderEchoResponse { Id = "1" })
            .WithHeader("Location", Location)
            .WithHeader("X-Thing", "1")
            .WithHeader("Access-Control-Allow-Origin", ForeignOrigin)
            .WithHeader("Content-Type", "text/plain"));
    }
}

[Message(Defaults.HeaderConflictTopic)]
[HttpEndpoint("GET", Defaults.HeaderConflictPath)]
public class HeaderConflictMessageHandler : IMessageHandler<HeaderEchoRequest, HeaderEchoResponse>
{
    public Task<IBenzeneResult<HeaderEchoResponse>> HandleAsync(HeaderEchoRequest request)
    {
        return Task.FromResult(BenzeneResult.Conflict<HeaderEchoResponse>("already exists")
            .WithHeader("X-Existing", "/things/1"));
    }
}
