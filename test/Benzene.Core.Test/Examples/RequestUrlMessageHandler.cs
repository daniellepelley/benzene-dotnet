using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Http.RequestUrl;
using Benzene.Results;

namespace Benzene.Test.Examples;

public class RequestUrlResponse
{
    public string Absolute { get; set; }
    public string BasePath { get; set; }
    public string Path { get; set; }
}

// FR-2: a handler building an absolute link from the injected IRequestUrl.
[Message(Defaults.RequestUrlTopic)]
[HttpEndpoint("GET", Defaults.RequestUrlPath)]
public class RequestUrlMessageHandler : IMessageHandler<HeaderEchoRequest, RequestUrlResponse>
{
    private readonly IRequestUrl _requestUrl;

    public RequestUrlMessageHandler(IRequestUrl requestUrl)
    {
        _requestUrl = requestUrl;
    }

    public Task<IBenzeneResult<RequestUrlResponse>> HandleAsync(HeaderEchoRequest request)
    {
        return Task.FromResult(BenzeneResult.Ok(new RequestUrlResponse
        {
            Absolute = _requestUrl.Absolute("/123").ToString(),
            BasePath = _requestUrl.BasePath,
            Path = _requestUrl.Path
        }));
    }
}
