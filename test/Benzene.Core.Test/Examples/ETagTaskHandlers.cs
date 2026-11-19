using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Http.Conditional;
using Benzene.Results;

namespace Benzene.Test.Examples;

// FR-6: a resource read with an ETag (304 on a matching If-None-Match) and updated under If-Match.
public class ETagTaskRequest
{
    public string Title { get; set; }
    public string IfMatch { get; set; }
}

public class ETagTask
{
    public string Title { get; set; }
}

public static class ETagTaskStore
{
    public static readonly string CurrentETag = EntityTags.Strong("v2");
}

[Message("etag:get")]
[HttpEndpoint("GET", Defaults.ETagTaskPath)]
public class GetETagTaskHandler : IMessageHandler<ETagTaskRequest, ETagTask>
{
    public Task<IBenzeneResult<ETagTask>> HandleAsync(ETagTaskRequest request)
    {
        return Task.FromResult(BenzeneResult.Ok(new ETagTask { Title = "write the docs" })
            .WithHeader("ETag", ETagTaskStore.CurrentETag));
    }
}

[Message("etag:put")]
[HttpEndpoint("PUT", Defaults.ETagTaskPath)]
public class PutETagTaskHandler : IMessageHandler<ETagTaskRequest, ETagTask>
{
    public Task<IBenzeneResult<ETagTask>> HandleAsync(ETagTaskRequest request)
    {
        var failed = Preconditions.CheckIfMatch<ETagTask>(request.IfMatch, ETagTaskStore.CurrentETag);
        if (failed != null)
        {
            return Task.FromResult(failed);
        }

        return Task.FromResult(BenzeneResult.Ok(new ETagTask { Title = request.Title })
            .WithHeader("ETag", EntityTags.Strong("v3")));
    }
}
