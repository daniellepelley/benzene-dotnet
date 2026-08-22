using Amazon.Lambda;
using Benzene.Clients;
using Benzene.Clients.Aws.Lambda;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Benzene.CodeGen.Cli.Core.Commands.HealthCheck;

public class HealthCheckClient
{
    private readonly IAwsLambdaClient _lambdaClient;
    private readonly ILogger _logger;
    private readonly string _lambdaName;

    public HealthCheckClient(string lambdaName, IAwsLambdaClient lambdaClient, ILogger logger)
    {
        _lambdaName = lambdaName;
        _logger = logger;
        _lambdaClient = lambdaClient;
    }

    public async Task<string> GetHealthCheckAsync()
    {
        try
        {
            var lambdaRequest = new BenzeneMessageClientRequest(Benzene.Abstractions.BenzeneTopic.HealthCheck, new Dictionary<string, string>(), JsonConvert.SerializeObject(new object()));
            var response = await _lambdaClient.SendMessageAsync<BenzeneMessageClientRequest, BenzeneMessageClientResponse>(lambdaRequest, _lambdaName, InvocationType.RequestResponse);
            return response.Body;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sending message {receiverTopic} to {receiver} failed", Benzene.Abstractions.BenzeneTopic.HealthCheck, _lambdaName);
            // Returning null here used to leave callers to NRE on the result with no indication of
            // the real cause. Throw with a diagnosable message instead - Phase 2's CLI commands are
            // fail-loud (mirrors AwsLambdaSpecClient.GetSpecAsync).
            throw new InvalidOperationException(
                $"Lambda '{_lambdaName}' did not answer the health check topic — is UseHealthCheck() registered and the function name/profile correct?",
                ex);
        }
    }
}
