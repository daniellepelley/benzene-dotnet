using Amazon.StepFunctions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Benzene.Clients.Aws.StepFunctions;

/// <summary>
/// Creates <see cref="IStepFunctionsClient"/> instances for a specific state machine.
/// </summary>
public interface IStepFunctionsClientFactory
{
    /// <summary>
    /// Creates a new Step Functions client.
    /// </summary>
    /// <returns>The created client.</returns>
    IStepFunctionsClient Create();
}

/// <summary>
/// Creates <see cref="StepFunctionsClient"/> instances for a specific state machine.
/// </summary>
public class StepFunctionsClientFactory : IStepFunctionsClientFactory
{
    private readonly ILogger<StepFunctionsClient> _logger;
    private readonly string _stateMachineArn;
    private readonly IAmazonStepFunctions _amazonStepFunctionsClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="StepFunctionsClientFactory"/> class.
    /// </summary>
    /// <param name="stateMachineArn">The ARN of the state machine clients created by this factory will target.</param>
    /// <param name="amazonStepFunctionsClient">The Step Functions client used by created clients.</param>
    /// <param name="logger">The logger used by created clients.</param>
    public StepFunctionsClientFactory(string stateMachineArn, IAmazonStepFunctions amazonStepFunctionsClient, ILogger<StepFunctionsClient> logger)
    {
        _amazonStepFunctionsClient = amazonStepFunctionsClient;
        _stateMachineArn = stateMachineArn;
        // #267: normalize here too, not just in StepFunctionsClient's own constructor - this factory
        // stores and forwards the logger to every client it creates, so a null coming through DI is
        // guarded at the same seam it enters the package.
        _logger = logger ?? NullLogger<StepFunctionsClient>.Instance;
    }

    /// <summary>
    /// Creates a new <see cref="StepFunctionsClient"/> for the configured state machine.
    /// </summary>
    /// <returns>The created client.</returns>
    public virtual IStepFunctionsClient Create()
    {
        return new StepFunctionsClient(_stateMachineArn, _amazonStepFunctionsClient, _logger);
    }
}
