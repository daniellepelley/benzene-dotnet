using Benzene.Clients.Aws.Lambda;
using Benzene.CodeGen.Cli.Core.Commands.Build;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace Benzene.CodeGen.Cli.Core.Commands.HealthCheck;

/// <summary>
/// Invokes the target's health check topic and prints the response body. Unlike its already-fixed
/// <c>diff</c>/<c>profile-check</c> siblings (<see cref="Diff.DiffCommand"/>,
/// <see cref="CloudServiceProfile.CloudServiceProfileCheckCommand"/>), this used to print the body
/// and return unconditionally - never inspecting <c>isHealthy</c> - so it never failed CI regardless
/// of the target's reported health. It now parses <c>isHealthy</c> and throws
/// <see cref="HealthCheckFailedException"/> when false, gated behind the same <c>--fail-on</c>
/// convention those siblings use.
/// </summary>
public class HealthCheckCommand : CommandBase<HealthCheckPayload>
{
    private static readonly string[] ValidFailOnValues = { "unhealthy", "none" };

    private readonly HealthCheckClient? _healthCheckClient;

    public HealthCheckCommand()
        : this(null)
    { }

    /// <summary>
    /// Test/advanced seam: drives <see cref="ExecuteAsync"/>'s <c>isHealthy</c>/<c>--fail-on</c>
    /// logic against a caller-supplied <see cref="HealthCheckClient"/> (e.g. one wrapping a fake
    /// <c>IAwsLambdaClient</c>) instead of the real AWS Lambda invocation the parameterless
    /// constructor builds from <c>--profile</c>/<c>--lambda-name</c>.
    /// </summary>
    /// <param name="healthCheckClient">
    /// The client to use, or <c>null</c> to build the real AWS Lambda-backed client from the payload
    /// at execution time (the normal CLI path).
    /// </param>
    public HealthCheckCommand(HealthCheckClient? healthCheckClient)
        : base("healthcheck", "Runs a health check on a Benzene service")
    {
        _healthCheckClient = healthCheckClient;
    }

    public override async Task ExecuteAsync(HealthCheckPayload payload)
    {
        var failOn = ResolveFailOn(payload);

        var client = _healthCheckClient ?? CreateClient(payload);
        var json = await client.GetHealthCheckAsync();
        Console.Out.WriteJson(json);

        if (Trips(json, failOn))
        {
            throw new HealthCheckFailedException(json,
                $"benzene healthcheck: target reported unhealthy - failing on --fail-on {failOn}");
        }
    }

    private static HealthCheckClient CreateClient(HealthCheckPayload payload)
    {
        var client = AmazonLambdaClientFactory.CreateClient(payload.Profile);
        return new HealthCheckClient(payload.LambdaName, new AwsLambdaClient(client), NullLogger.Instance);
    }

    private static string ResolveFailOn(HealthCheckPayload payload)
    {
        var failOn = string.IsNullOrWhiteSpace(payload.FailOn) ? Constants.HealthCheckFailOnDefault : payload.FailOn;
        if (!ValidFailOnValues.Contains(failOn))
        {
            throw new ArgumentException(
                $"--fail-on must be one of 'unhealthy' or 'none' (got '{failOn}')");
        }

        return failOn;
    }

    private static bool Trips(string json, string failOn) => failOn switch
    {
        "unhealthy" => !IsHealthy(json),
        _ => false, // "none": report only, never fail.
    };

    private static bool IsHealthy(string json)
    {
        var jObject = JObject.Parse(json);
        var isHealthyToken = jObject["isHealthy"];

        // Absent/unparseable `isHealthy`: don't fail-loud on a response shape this tool doesn't
        // recognize - only trip on an explicit `false`.
        return isHealthyToken == null || isHealthyToken.Value<bool>();
    }
}

