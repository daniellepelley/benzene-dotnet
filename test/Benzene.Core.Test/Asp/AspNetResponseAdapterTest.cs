using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Benzene.AspNet.Core;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Benzene.Test.Asp;

public class AspNetResponseAdapterTest
{
    private static (AspNetResponseAdapter Adapter, AspNetContext Context, DefaultHttpContext Http) Create()
    {
        var http = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        return (new AspNetResponseAdapter(), new AspNetContext(http), http);
    }

    [Fact]
    public void SetResponseHeader_SameKeyTwice_AppendsBothValues_WithoutThrowing()
    {
        var (adapter, context, http) = Create();

        adapter.SetResponseHeader(context, "Set-Cookie", "a=1");
        adapter.SetResponseHeader(context, "Set-Cookie", "b=2");

        var values = http.Response.Headers["Set-Cookie"].ToArray();
        Assert.Contains("a=1", values);
        Assert.Contains("b=2", values);
    }

    [Fact]
    public async Task FinalizeAsync_204_DoesNotWriteABody()
    {
        var (adapter, context, http) = Create();
        adapter.SetStatusCode(context, "204");
        adapter.SetBody(context, "should-not-be-written");

        await adapter.FinalizeAsync(context);

        Assert.Equal(204, http.Response.StatusCode);
        http.Response.Body.Position = 0;
        Assert.Equal("", await new StreamReader(http.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task FinalizeAsync_200_WritesTheBody()
    {
        var (adapter, context, http) = Create();
        adapter.SetStatusCode(context, "200");
        adapter.SetBody(context, "hello");

        await adapter.FinalizeAsync(context);

        http.Response.Body.Position = 0;
        Assert.Equal("hello", await new StreamReader(http.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task FinalizeAsync_NullBody_WritesAnEmptyBody_RatherThanThrowing()
    {
        // DefaultResponsePayloadMapper.Map legitimately returns null for a handler with no response
        // payload (a fire-and-forget IMessageHandler<T>, e.g. MeshReportMessageHandler) - SetBody(null)
        // must not leave a null body for FinalizeAsync to hand to HttpResponse.WriteAsync, which throws
        // ArgumentNullException on a null argument (found by running such a handler through Kestrel; the
        // in-memory BenzeneMessage transport's own adapter never surfaces this, since it just stores the
        // property rather than writing it anywhere).
        var (adapter, context, http) = Create();
        adapter.SetStatusCode(context, "200");
        adapter.SetBody(context, null);

        await adapter.FinalizeAsync(context);

        Assert.Equal(200, http.Response.StatusCode);
        http.Response.Body.Position = 0;
        Assert.Equal("", await new StreamReader(http.Response.Body).ReadToEndAsync());
    }
}
