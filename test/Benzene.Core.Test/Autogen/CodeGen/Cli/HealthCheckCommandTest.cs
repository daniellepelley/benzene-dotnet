using System;
using System.Threading.Tasks;
using Benzene.CodeGen.Cli.Core.Commands.HealthCheck;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Cli;

// `benzene healthcheck` wires HealthCheckClient up to a real AWS Lambda client
// (AmazonLambdaClientFactory.CreateClient(payload.Profile)), so unlike DiffCommand/
// CloudServiceProfileCheckCommand there is no local fixture to exercise a successful round trip
// without a real deployed function. What's stable to pin here, without depending on network
// reachability or real AWS credentials, is that a failed invoke always surfaces as
// HealthCheckClient's fail-loud InvalidOperationException (never a swallowed null) once it gets as
// far as GetHealthCheckAsync().
public class HealthCheckCommandTest
{
    [Fact]
    public void Command_HasTheExpectedNameAndDescription()
    {
        var command = new HealthCheckCommand();

        Assert.Equal("healthcheck", command.Name);
        Assert.Equal("Runs a health check on a Benzene service", command.Description);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownProfile_ThrowsInvalidOperationException_NotNullOrUnwrapped()
    {
        // A --profile naming no locally-configured AWS profile makes
        // AmazonLambdaClientFactory.CreateClient return null (this test environment has no
        // ~/.aws/credentials profiles at all), so the underlying invoke fails inside
        // HealthCheckClient's try block (a NullReferenceException from the null Lambda client) -
        // this pins that HealthCheckClient still wraps that failure as a diagnosable
        // InvalidOperationException rather than letting a raw/unrelated exception escape or
        // (as before this fix) swallowing it and returning null.
        var payload = new HealthCheckPayload { LambdaName = "orders-fn", Profile = "no-such-profile-xyz" };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new HealthCheckCommand().ExecuteAsync(payload));

        Assert.Contains("orders-fn", exception.Message);
        Assert.Contains("did not answer the health check topic", exception.Message);
        Assert.NotNull(exception.InnerException);
    }
}
