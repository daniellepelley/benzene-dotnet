using System;
using System.IO;
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

    // #282: an empty or non-JSON response body used to crash with a raw, unhandled
    // Newtonsoft.Json.JsonReaderException out of WriteJson/IsHealthy - one line before the
    // above ExecuteAsync_ResponseMissingIsHealthy_DoesNotThrow tolerance was ever reached. A
    // response shape this tool doesn't recognise at the JSON-syntax level should get the same
    // "don't fail loud" treatment as one it doesn't recognise at the JSON-object level: print the
    // raw body verbatim and treat it as not-tripped (only an explicit isHealthy: false trips).

    [Fact]
    public async Task ExecuteAsync_EmptyBody_DoesNotThrow_AndWritesRawBody()
    {
        var command = new HealthCheckCommand(FakeClient(""));

        var originalOut = Console.Out;
        using var capturedOut = new StringWriter();
        Console.SetOut(capturedOut);
        try
        {
            await command.ExecuteAsync(new HealthCheckPayload { LambdaName = "orders-fn" });
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal("", capturedOut.ToString().Trim());
    }

    [Fact]
    public async Task ExecuteAsync_NonJsonBody_DoesNotThrow_AndWritesRawBodyVerbatim()
    {
        var command = new HealthCheckCommand(FakeClient("Internal Server Error"));

        var originalOut = Console.Out;
        using var capturedOut = new StringWriter();
        Console.SetOut(capturedOut);
        try
        {
            await command.ExecuteAsync(new HealthCheckPayload { LambdaName = "orders-fn" });
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal("Internal Server Error", capturedOut.ToString().Trim());
    }

    [Fact]
    public async Task ExecuteAsync_HtmlErrorBody_FailOnUnhealthy_DoesNotThrow()
    {
        // Non-JSON is "a response shape this tool doesn't recognise" - it must not be treated as
        // an explicit isHealthy: false, even under the default --fail-on unhealthy.
        var command = new HealthCheckCommand(FakeClient("<html><body>502 Bad Gateway</body></html>"));

        await command.ExecuteAsync(new HealthCheckPayload { LambdaName = "orders-fn" });
    }
}
