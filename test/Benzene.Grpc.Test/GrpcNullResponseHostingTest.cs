using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Grpc.AspNet;
using Benzene.Grpc.Test.Handlers;
using Benzene.Grpc.Test.Protos;
using Benzene.Microsoft.Dependencies;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Benzene.Grpc.Test;

/// <summary>
/// End-to-end regression coverage for WP-4 (tasks #8, #23): a fire-and-forget handler that produces
/// no response payload must complete the call with <see cref="StatusCode.OK"/> and an empty response
/// message, not throw. Driven through a real <see cref="TestServer"/> + generated <see cref="GrpcChannel"/>
/// client (the technique that originally found the bug) rather than unit-testing
/// <c>ProtobufJsonGrpcMessageAdapter.ConvertResponse</c> in isolation, so the whole pipeline - interceptor,
/// status mapping, and wire (de)serialization - is exercised. Covers both call shapes the fix applies to;
/// server-streaming/duplex are deliberately untouched by WP-4 and are not covered here.
/// </summary>
public class GrpcNullResponseHostingTest
{
    [Fact]
    public async Task EchoFireAndForget_WhenHandlerProducesNoPayload_CompletesOkWithEmptyMessage()
    {
        using var host = await BuildHostAsync(typeof(EchoFireAndForgetMessageHandler));
        var client = new TestService.TestServiceClient(CreateChannel(host));

        using var call = client.EchoFireAndForgetAsync(new EchoRequest { Name = "world" });
        var reply = await call.ResponseAsync;

        Assert.Equal(StatusCode.OK, call.GetStatus().StatusCode);
        Assert.Equal(new EchoReply(), reply);
    }

    [Fact]
    public async Task UploadFireAndForget_WhenHandlerProducesNoPayload_CompletesOkWithEmptyMessage()
    {
        using var host = await BuildHostAsync(typeof(UploadFireAndForgetMessageHandler));
        var client = new TestService.TestServiceClient(CreateChannel(host));

        using var call = client.UploadFireAndForget();
        await call.RequestStream.WriteAsync(new UploadItem { Value = 1 });
        await call.RequestStream.WriteAsync(new UploadItem { Value = 2 });
        await call.RequestStream.CompleteAsync();

        var summary = await call.ResponseAsync;

        Assert.Equal(StatusCode.OK, call.GetStatus().StatusCode);
        Assert.Equal(new UploadSummary(), summary);
    }

    private static async Task<IHost> BuildHostAsync(Type handlerType)
    {
        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddBenzeneGrpc();
                    services.UsingBenzene(x => x.AddBenzene().AddBenzeneMessage().AddMessageHandlers(new[] { handlerType }).AddGrpcMessageHandlers());
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapGrpcService<TestGrpcService>());
                    app.UseBenzene(x => x.UseGrpc(grpc => grpc.UseMessageHandlers(handlerType)));
                });
            });

        return await hostBuilder.StartAsync();
    }

    private static GrpcChannel CreateChannel(IHost host)
    {
        var testServer = host.GetTestServer();
        return GrpcChannel.ForAddress(testServer.BaseAddress ?? new Uri("http://localhost"), new GrpcChannelOptions
        {
            HttpHandler = testServer.CreateHandler()
        });
    }
}
