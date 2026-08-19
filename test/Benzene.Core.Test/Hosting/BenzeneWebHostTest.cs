using System.IO;
using System.Text;
using System.Threading.Tasks;
using Benzene.AspNet.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Hosting;

/// <summary>
/// Covers <see cref="BenzeneWebHost"/> - the one-line entry point for the embedded ASP.NET shape.
/// It composes the public explicit form (<c>WebApplicationBuilder.UseBenzene&lt;TStartUp&gt;()</c> +
/// <c>app.UseBenzene()</c>), which <see cref="AspNetUnifiedStartUpTest"/> covers written out, so the
/// tests here pin that composing it changes nothing and that both hooks land where the docs say.
/// </summary>
public class BenzeneWebHostTest
{
    private static async Task<string> PostPingAsync(WebApplication app, string path = "/ping")
    {
        var requestDelegate = ((IApplicationBuilder)app).Build();

        var httpContext = new DefaultHttpContext
        {
            Request =
            {
                Method = "POST",
                Path = path,
                ContentType = "application/json",
                Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"name\":\"world\"}"))
            },
            Response = { Body = new MemoryStream() }
        };

        await requestDelegate(httpContext);

        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        return await new StreamReader(httpContext.Response.Body, Encoding.UTF8).ReadToEndAsync();
    }

    [Fact]
    public async Task Build_RunsTheSharedStartUpOverHttp_JustLikeTheExplicitTriangle()
    {
        var app = BenzeneWebHost.Build<AspNetSharedStartUp>();

        Assert.Contains("pong-world", await PostPingAsync(app));
    }

    [Fact]
    public void Build_AppliesConfigureBuilder_BeforeTheStartUpRuns()
    {
        // The documented ordering: configureBuilder lands first, so a caller substituting one of
        // Benzene's TryAdd baseline services wins over the startup's own registration pass.
        var order = new System.Collections.Generic.List<string>();

        var app = BenzeneWebHost.Build<AspNetSharedStartUp>(
            configureBuilder: builder =>
            {
                order.Add("configureBuilder");
                builder.Services.AddSingleton(new WebHostMarker());
            },
            configureApp: _ => order.Add("configureApp"));

        Assert.Equal(new[] { "configureBuilder", "configureApp" }, order);
        Assert.NotNull(app.Services.GetService(typeof(WebHostMarker)));
    }

    [Fact]
    public async Task Build_RunsConfigureApp_BeforeBenzenesTerminalWiring()
    {
        // The app's own ASP.NET middleware has to sit in FRONT of Benzene, or it never sees a request
        // Benzene answers. Pinning it: a middleware added in configureApp short-circuits the ping.
        var app = BenzeneWebHost.Build<AspNetSharedStartUp>(
            configureApp: a => a.Use((System.Func<HttpContext, RequestDelegate, Task>)(async (context, _) =>
            {
                await context.Response.WriteAsync("intercepted");
            })));

        var body = await PostPingAsync(app);

        Assert.Equal("intercepted", body);
        Assert.DoesNotContain("pong-world", body);
    }

    private sealed class WebHostMarker;
}
