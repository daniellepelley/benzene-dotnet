using Amazon.StepFunctions;
using Benzene.Abstractions.DI;
using Microsoft.Extensions.Logging;

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
    private readonly ICancellationTokenAccessor? _cancellation;

    /// <summary>
    /// Initializes a new instance of the <see cref="StepFunctionsClientFactory"/> class with no
    /// cancellation-token accessor.
    /// </summary>
    /// <param name="stateMachineArn">The ARN of the state machine clients created by this factory will target.</param>
    /// <param name="amazonStepFunctionsClient">The Step Functions client used by created clients.</param>
    /// <param name="logger">The logger used by created clients.</param>
    public StepFunctionsClientFactory(string stateMachineArn, IAmazonStepFunctions amazonStepFunctionsClient, ILogger<StepFunctionsClient> logger)
        : this(stateMachineArn, amazonStepFunctionsClient, logger, null)
    {
    }

    /// <summary>
    /// Initializes the factory, additionally resolving the ambient cancellation token so clients it
    /// creates thread it into their own SDK calls.
    /// </summary>
    /// <param name="stateMachineArn">The ARN of the state machine clients created by this factory will target.</param>
    /// <param name="amazonStepFunctionsClient">The Step Functions client used by created clients.</param>
    /// <param name="logger">The logger used by created clients.</param>
    /// <param name="cancellation">Supplies the ambient cancellation token; null observes no cancellation.</param>
    public StepFunctionsClientFactory(string stateMachineArn, IAmazonStepFunctions amazonStepFunctionsClient, ILogger<StepFunctionsClient> logger,
        ICancellationTokenAccessor? cancellation)
    {
        _amazonStepFunctionsClient = amazonStepFunctionsClient;
        _stateMachineArn = stateMachineArn;
        _logger = logger;
        _cancellation = cancellation;
    }

    /// <summary>
    /// Creates a new <see cref="StepFunctionsClient"/> for the configured state machine.
    /// </summary>
    /// <returns>The created client.</returns>
    public virtual IStepFunctionsClient Create()
    {
        return new StepFunctionsClient(_stateMachineArn, _amazonStepFunctionsClient, _logger, _cancellation);
    }
}
