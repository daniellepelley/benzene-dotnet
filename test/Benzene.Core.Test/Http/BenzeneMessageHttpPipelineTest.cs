using System.Collections.Generic;
using System.Threading.Tasks;
using Benzene.Aws.Lambda.ApiGateway;
using Benzene.Aws.Lambda.ApiGateway.TestHelpers;
using Benzene.Aws.Lambda.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.Serialization;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Http.BenzeneMessage;
using Benzene.Results;
using Benzene.Test.Examples;
using Benzene.Testing;
using Benzene.Aws.Lambda.Core.TestHelpers;
using Xunit;

namespace Benzene.Test.Http;

public class BenzeneMessageHttpPipelineTest
{
    private static AwsLambdaBenzeneTestHost CreateHost(BenzeneMessageHttpOptions? options = null)
    {
        return new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app
                .UseApiGateway(apiGateway => apiGateway
                    .UseBenzeneMessage(options ?? new BenzeneMessageHttpOptions(),
                        messageApp => messageApp.UseMessageHandlers())
                    .UseMessageHandlers()
                )
            )
            .BuildHost();
    }

    private static object CreateEnvelope(string topic)
    {
        return new
        {
            topic,
            headers = new Dictionary<string, string>(),
            body = Defaults.Message
        };
    }

    [Fact]
    public async Task PostEnvelope_DispatchesThroughMessagePipeline()
    {
        var host = CreateHost();

        var response = await host.SendApiGatewayAsync(
            HttpBuilder.Create("POST", "/benzene-message", CreateEnvelope(Defaults.Topic)));

        Assert.NotNull(response);
        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"statusCode\":\"ok\"", response.Body);
    }

    [Fact]
    public async Task PostEnvelope_UnknownTopic_MapsEnvelopeStatusToHttpStatus()
    {
        var host = CreateHost();

        var response = await host.SendApiGatewayAsync(
            HttpBuilder.Create("POST", "/benzene-message", CreateEnvelope("no-such-topic")));

        Assert.NotNull(response);
        Assert.NotEqual(200, response.StatusCode);
    }

    [Fact]
    public async Task PostEnvelope_UnknownTopic_InnerProblemBodyHasNoNumericStatus_OuterContentTypeStaysApplicationJson()
    {
        // work/problem-details-plan.md §2.3: the envelope's inner problem body is transport-neutral
        // (no numeric HTTP `status` member, even though this envelope traveled over HTTP), and the
        // OUTER transport content-type is always application/json - the outer body is the envelope,
        // not the problem document. The outer transport is ApiGatewayContext here, which Phase 4
        // registers HttpProblemDetailsResponsePayloadMapper for, but BenzeneMessageHttpMiddleware
        // never resolves IResponsePayloadMapper<ApiGatewayContext> for its own (envelope) response,
        // so that registration has no effect on this path either.
        var host = CreateHost();

        var response = await host.SendApiGatewayAsync(
            HttpBuilder.Create("POST", "/benzene-message", CreateEnvelope("no-such-topic")));

        Assert.NotNull(response);
        Assert.NotEqual(200, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Headers["content-type"]);

        var envelope = new JsonSerializer().Deserialize<BenzeneMessageResponse>(response.Body);
        Assert.False(envelope.IsSuccessful);
        var problem = new JsonSerializer().Deserialize<ProblemDetails>(envelope.Body);
        Assert.Null(problem.Status);
    }

    [Fact]
    public async Task PostEnvelope_TopicRejectedByFilter_RespondsNotFound()
    {
        var host = CreateHost(new BenzeneMessageHttpOptions { TopicFilter = topic => topic != Defaults.Topic });

        var response = await host.SendApiGatewayAsync(
            HttpBuilder.Create("POST", "/benzene-message", CreateEnvelope(Defaults.Topic)));

        Assert.NotNull(response);
        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task OtherRequests_FallThroughToHttpMessageHandlers()
    {
        var host = CreateHost();

        var response = await host.SendApiGatewayAsync(
            HttpBuilder.Create("GET", Defaults.Path, Defaults.MessageAsObject));

        Assert.NotNull(response);
        Assert.Equal(200, response.StatusCode);
    }
}
