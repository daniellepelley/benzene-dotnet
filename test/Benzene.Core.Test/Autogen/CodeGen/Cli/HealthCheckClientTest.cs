using System;
using System.Threading.Tasks;
using Amazon.Lambda;
using Benzene.Clients;
using Benzene.Clients.Aws.Lambda;
using Benzene.CodeGen.Cli.Core.Commands.HealthCheck;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Cli;

// HealthCheckClient invokes the "benzene:healthcheck" topic on a target Lambda function
// (`benzene healthcheck --lambda-name ...`) and returns the response body verbatim. These tests
// pin the request shape and, mirroring AwsLambdaSpecClient's fail-loud convention, that a failed
// invoke surfaces as a diagnosable InvalidOperationException rather than a swallowed null.
public class HealthCheckClientTest
{
    [Fact]
    public async Task GetHealthCheckAsync_InvokesTheHealthCheckTopic_ReturnsResponseBody()
    {
        BenzeneMessageClientRequest? sent = null;
        var lambdaClient = new Mock<IAwsLambdaClient>();
        lambdaClient
            .Setup(x => x.SendMessageAsync<BenzeneMessageClientRequest, BenzeneMessageClientResponse>(
                It.IsAny<BenzeneMessageClientRequest>(), "orders-fn", InvocationType.RequestResponse))
            .Callback<BenzeneMessageClientRequest, string, InvocationType>((request, _, _) => sent = request)
            .ReturnsAsync(new BenzeneMessageClientResponse("ok", "{\"status\":\"healthy\"}"));
        var client = new HealthCheckClient("orders-fn", lambdaClient.Object, NullLogger.Instance);

        var result = await client.GetHealthCheckAsync();

        Assert.Equal("{\"status\":\"healthy\"}", result);
        Assert.Equal(Benzene.Abstractions.BenzeneTopic.HealthCheck, sent!.Topic);
    }

    [Fact]
    public async Task GetHealthCheckAsync_UnderlyingClientThrows_ThrowsInvalidOperationExceptionWithDiagnosableMessage()
    {
        var lambdaClient = new Mock<IAwsLambdaClient>();
        var inner = new InvalidOperationException("no route to host");
        lambdaClient
            .Setup(x => x.SendMessageAsync<BenzeneMessageClientRequest, BenzeneMessageClientResponse>(
                It.IsAny<BenzeneMessageClientRequest>(), "orders-fn", InvocationType.RequestResponse))
            .ThrowsAsync(inner);
        var client = new HealthCheckClient("orders-fn", lambdaClient.Object, NullLogger.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetHealthCheckAsync());

        Assert.Same(inner, exception.InnerException);
        Assert.Contains("orders-fn", exception.Message);
        Assert.Contains("did not answer the health check topic", exception.Message);
    }
}
