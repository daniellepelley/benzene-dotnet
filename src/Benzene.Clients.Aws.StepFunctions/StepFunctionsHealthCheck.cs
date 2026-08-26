using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Benzene.HealthChecks.Core;

namespace Benzene.Clients.Aws.StepFunctions;

/// <summary>
/// Verifies a Step Functions state machine. In the default <see cref="HealthCheckMode.Reachability"/>
/// mode this is a <b>non-destructive</b> read-only <c>DescribeStateMachine</c> call; in
/// <see cref="HealthCheckMode.Active"/> mode it starts a real execution (side-effecting — a continuous
/// stream of real executions at probe cadence: cost, noise, history-retention pressure).
/// </summary>
/// <remarks>
/// The reachability check proves the state machine exists, is reachable, and the credentials can read it
/// (<c>states:DescribeStateMachine</c>) — not that a start would succeed (<c>states:StartExecution</c> is
/// a different permission). Use <see cref="HealthCheckMode.Active"/> only when you must exercise the
/// start path (point it at a cheap no-op state machine), and keep it off a frequent poll and off probes.
/// <para>
/// No internal timeout guard here (unlike an earlier version of this type): both the reachability and
/// active SDK calls are passed the real ambient <see cref="CancellationToken"/> directly, and the check
/// relies purely on the processor's uniform per-check timeout wrap (<c>HealthCheckProcessor</c> via
/// <c>TimeOutHealthCheck</c>), which genuinely cancels the in-flight call on timeout rather than merely
/// abandoning the awaited task. Same shape as <c>SqsHealthCheck</c>/<c>SnsHealthCheck</c>/
/// <c>EventBridgeHealthCheck</c>.
/// </para>
/// </remarks>
public class StepFunctionsHealthCheck : IHealthCheck
{
    private readonly IAmazonStepFunctions _amazonStepFunctions;
    private readonly string _stateMachineArn;
    private readonly HealthCheckMode _mode;

    /// <summary>Initializes a new instance of the <see cref="StepFunctionsHealthCheck"/> class.</summary>
    /// <param name="stateMachineArn">The ARN of the state machine to check.</param>
    /// <param name="amazonStepFunctions">The Step Functions client used to run the check.</param>
    /// <param name="mode">Reachability (default, read-only) or Active (starts an execution — side-effecting).</param>
    public StepFunctionsHealthCheck(string stateMachineArn, IAmazonStepFunctions amazonStepFunctions,
        HealthCheckMode mode = HealthCheckMode.Reachability)
    {
        _stateMachineArn = stateMachineArn;
        _amazonStepFunctions = amazonStepFunctions;
        _mode = mode;
    }

    /// <summary>Runs the check and reports the outcome.</summary>
    public Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var dependencies = new[] { new HealthCheckDependency("StateMachine", _stateMachineArn) };

        var call = _mode == HealthCheckMode.Active
            ? MapStatus(_amazonStepFunctions.StartExecutionAsync(new StartExecutionRequest
            {
                StateMachineArn = _stateMachineArn,
                Input = "{}"
            }, cancellationToken))
            : MapStatus(_amazonStepFunctions.DescribeStateMachineAsync(new DescribeStateMachineRequest
            {
                StateMachineArn = _stateMachineArn
            }, cancellationToken));

        return RunAsync(call, dependencies);
    }

    // Project any AWS response to its HttpStatusCode without losing the task's faulted-ness.
    private static async Task<HttpStatusCode> MapStatus<TResponse>(Task<TResponse> call) where TResponse : AmazonWebServiceResponse
        => (await call).HttpStatusCode;

    // No internal timeout guard here (unlike an earlier version of this type): both SDK calls above are
    // passed the real ambient token directly, so the check relies purely on the processor's uniform
    // per-check timeout wrap (HealthCheckProcessor via TimeOutHealthCheck), which genuinely cancels the
    // in-flight call on timeout rather than merely abandoning the awaited task. Same shape as
    // SqsHealthCheck/SnsHealthCheck/EventBridgeHealthCheck.
    private async Task<IHealthCheckResult> RunAsync(Task<HttpStatusCode> call, HealthCheckDependency[] dependencies)
    {
        try
        {
            var statusCode = await call;
            if (statusCode == HttpStatusCode.OK)
            {
                return HealthCheckResult.CreateInstance(true, Type,
                    new Dictionary<string, object> { { "StateMachineArn", _stateMachineArn } }, dependencies);
            }

            return HealthCheckResult.CreateInstance(false, Type,
                new Dictionary<string, object> { { "StateMachineArn", _stateMachineArn }, { "Error", $"Returned a status of {statusCode}" } }, dependencies);
        }
        catch (Exception ex)
        {
            // Expected failures (state machine missing, no connectivity, no permission) are a classified
            // result, not a throw. Classify via the shared policy: an authorization failure (401/403, or
            // a known auth error code) is a persistent Failed, anything else a transient Failed, enriched
            // with the SDK error code + status, never the message.
            var (errorCode, faultStatus) = AwsErrorDetails(ex);
            return HealthCheckError.Classify(Type, ex, dependencies, errorCode, faultStatus,
                new Dictionary<string, object> { { "StateMachineArn", _stateMachineArn } },
                requiredPermission: _mode == HealthCheckMode.Active ? "states:StartExecution" : "states:DescribeStateMachine");
        }
    }

    // Pulls the non-sensitive discriminators AWS already returns off an SDK exception; null for a
    // non-AWS exception (e.g. a raw connectivity failure).
    private static (string? ErrorCode, int? StatusCode) AwsErrorDetails(Exception ex)
        => ex is AmazonServiceException ase ? (ase.ErrorCode, (int)ase.StatusCode) : (null, null);

    /// <summary>The check's identifier: <c>"StepFunctions"</c> in reachability mode, <c>"StepFunctions.Active"</c> in active mode.</summary>
    public string Type => _mode == HealthCheckMode.Active ? "StepFunctions.Active" : "StepFunctions";
}
