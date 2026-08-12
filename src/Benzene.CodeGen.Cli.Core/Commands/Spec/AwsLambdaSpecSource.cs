using Benzene.Clients.Aws.Lambda;
using Benzene.CodeGen.Cli.Core.Commands.Build;
using Benzene.Schema.OpenApi;
using Microsoft.Extensions.Logging.Abstractions;

namespace Benzene.CodeGen.Cli.Core.Commands.Spec;

/// <summary>
/// The original spec source: invokes a deployed AWS Lambda's spec topic
/// (<see cref="AwsLambdaSpecClient"/>) and returns its response body verbatim. Requires AWS
/// credentials (via <c>--profile</c>) and a reachable, running function - the source the other
/// <see cref="ISpecSource"/> implementations exist to make optional.
/// </summary>
public class AwsLambdaSpecSource : ISpecSource
{
    private readonly string _lambdaName;
    private readonly string _profile;

    public AwsLambdaSpecSource(string lambdaName, string profile)
    {
        _lambdaName = lambdaName;
        _profile = profile;
    }

    public async Task<string> GetSpecJsonAsync(SpecRequest request)
    {
        var client = AmazonLambdaClientFactory.CreateClient(_profile);
        var awsLambdaClient = new AwsLambdaSpecClient(_lambdaName, new AwsLambdaClient(client), NullLogger.Instance);
        return await awsLambdaClient.GetSpecAsync(request);
    }
}
