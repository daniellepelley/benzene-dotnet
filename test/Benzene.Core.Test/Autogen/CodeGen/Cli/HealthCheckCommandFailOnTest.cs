using System.Threading.Tasks;
using Amazon.Lambda;
using Benzene.Clients;
using Benzene.Clients.Aws.Lambda;
using Benzene.CodeGen.Cli.Core.Commands.HealthCheck;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Cli;

// `benzene healthcheck` used to print the health check response body and return unconditionally -
// never inspecting isHealthy - so it never failed CI regardless of the target's reported health,
// unlike its already-fixed `diff`/`profile-check` siblings. It now parses isHealthy and throws
// HealthCheckFailedException when false, gated behind --fail-on (default "unhealthy").
//
// The command's parameterless constructor invokes AWS Lambda directly (no --file/--url escape
// hatch like SpecCommand has), so this drives it through the HealthCheckClient(IAwsLambdaClient)
// test seam instead: a fake IAwsLambdaClient stands in for the real AWS invoke, and the command's
// HealthCheckClient? constructor overload lets a test wire that fake in directly.
public class HealthCheckCommandFailOnTest
{
    private class FakeAwsLambdaClient : IAwsLambdaClient
    {
        private readonly string _body;

        public FakeAwsLambdaClient(string body)
        {
            _body = body;
        }

        public Task<TResponse> SendMessageAsync<TRequest, TResponse>(TRequest request, string functionName,
            InvocationType invocationType)
        {
            var response = new BenzeneMessageClientResponse("ok", _body);
            return Task.FromResult((TResponse)(object)response);
        }
    }

    private static HealthCheckClient FakeClient(string body) =>
        new("orders-fn", new FakeAwsLambdaClient(body), NullLogger.Instance);

    [Fact]
    public async Task ExecuteAsync_Unhealthy_DefaultFailOn_Throws()
    {
        var command = new HealthCheckCommand(FakeClient(@"{""isHealthy"": false, ""healthChecks"": {}}"));

        var exception = await Assert.ThrowsAsync<HealthCheckFailedException>(
            () => command.ExecuteAsync(new HealthCheckPayload { LambdaName = "orders-fn" }));

        Assert.Contains("unhealthy", exception.Message);
        Assert.Contains("isHealthy", exception.ResponseJson);
    }

    [Fact]
    public async Task ExecuteAsync_Healthy_DoesNotThrow()
    {
        var command = new HealthCheckCommand(FakeClient(@"{""isHealthy"": true, ""healthChecks"": {}}"));

        await command.ExecuteAsync(new HealthCheckPayload { LambdaName = "orders-fn" });
    }

    [Fact]
    public async Task ExecuteAsync_Unhealthy_FailOnNone_DoesNotThrow()
    {
        var command = new HealthCheckCommand(FakeClient(@"{""isHealthy"": false, ""healthChecks"": {}}"));

        await command.ExecuteAsync(new HealthCheckPayload { LambdaName = "orders-fn", FailOn = "none" });
    }

    [Fact]
    public async Task ExecuteAsync_InvalidFailOn_Throws()
    {
        var command = new HealthCheckCommand(FakeClient(@"{""isHealthy"": true, ""healthChecks"": {}}"));

        await Assert.ThrowsAsync<System.ArgumentException>(() =>
            command.ExecuteAsync(new HealthCheckPayload { LambdaName = "orders-fn", FailOn = "bogus" }));
    }

    [Fact]
    public async Task ExecuteAsync_ResponseMissingIsHealthy_DoesNotThrow()
    {
        // A response shape this tool doesn't recognise shouldn't fail-loud on an explicit false it
        // never saw - only trip on isHealthy: false.
        var command = new HealthCheckCommand(FakeClient(@"{""healthChecks"": {}}"));

        await command.ExecuteAsync(new HealthCheckPayload { LambdaName = "orders-fn" });
    }
}
