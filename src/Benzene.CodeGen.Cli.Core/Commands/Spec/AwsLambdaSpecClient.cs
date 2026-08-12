using Amazon.Lambda;
using Benzene.Clients;
using Benzene.Clients.Aws.Lambda;
using Benzene.Schema.OpenApi;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Benzene.CodeGen.Cli.Core.Commands.Spec;

public class AwsLambdaSpecClient
{
    private readonly IAwsLambdaClient _lambdaClient;
    private readonly ILogger _logger;
    private readonly string _lambdaName;

    public AwsLambdaSpecClient(string lambdaName, IAwsLambdaClient lambdaClient, ILogger logger)
    {
        _lambdaName = lambdaName;
        _logger = logger;
        _lambdaClient = lambdaClient;
    }

    public async Task<string> GetSpecAsync(SpecRequest specRequest)
    {
        try
        {
            var lambdaRequest = new BenzeneMessageClientRequest(Benzene.Abstractions.BenzeneTopic.Spec, new Dictionary<string, string>(), JsonConvert.SerializeObject(specRequest));
            var response = await _lambdaClient.SendMessageAsync<BenzeneMessageClientRequest, BenzeneMessageClientResponse>(lambdaRequest, _lambdaName, InvocationType.RequestResponse);
            return response.Body;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sending message {receiverTopic} to {receiver} failed", Benzene.Abstractions.BenzeneTopic.Spec, _lambdaName);
            // Returning null here used to leave callers to NRE on the result (e.g.
            // EventServiceDocumentDeserializer.Deserialize(null)) with no indication of the real
            // cause. Throw with a diagnosable message instead - Phase 2's CLI commands are fail-loud.
            throw new InvalidOperationException(
                $"Lambda '{_lambdaName}' did not answer the spec topic — is UseSpec() registered and the function name/profile correct?",
                ex);
        }
    }
    public void Dispose()
    {
        // Method intentionally left empty.
    }
}
