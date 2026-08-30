using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda;
using Amazon.Runtime;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Results;
using Benzene.HealthChecks.Core;
using Benzene.Results;
using Microsoft.Extensions.Logging;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Clients.Aws.Lambda;

/// <summary>
/// Verifies connectivity to a Lambda function. In the default <see cref="HealthCheckMode.Reachability"/>
/// mode this is a <b>non-destructive</b> read-only <c>GetFunctionConfiguration</c> call; in
/// <see cref="HealthCheckMode.Active"/> mode it really invokes the function with a <c>ping</c> message
/// (side-effecting — cost + cold-start noise at probe cadence, and the function must no-op it).
/// </summary>
/// <remarks>
/// The reachability check proves the function exists, is reachable, and the credentials can read it
/// (<c>lambda:GetFunctionConfiguration</c>) — not that an invoke would succeed
/// (<c>lambda:InvokeFunction</c> is a different permission). Use <see cref="HealthCheckMode.Active"/> only
/// when you need to exercise the invoke path, and keep it off a frequent poll and off liveness/readiness.
/// <para>
/// No internal timeout guard here (unlike an earlier version of this type): the reachability path
/// forwards the <see cref="CancellationToken"/> it is given straight into the SDK call and relies purely
/// on the processor's uniform per-check timeout wrap (<c>HealthCheckProcessor</c> via
/// <c>TimeOutHealthCheck</c>), which genuinely cancels the in-flight call on timeout rather than merely
/// abandoning the awaited task. Same shape as <c>SqsHealthCheck</c>/<c>SnsHealthCheck</c>/
/// <c>EventBridgeHealthCheck</c>. The <see cref="HealthCheckMode.Active"/> ping (via
/// <see cref="AwsLambdaBenzeneMessageClient"/>) resolves the ambient
/// <see cref="Benzene.Abstractions.DI.ICancellationTokenAccessor"/> (#261) and threads it into the
/// underlying invoke's SDK call the same way the reachability path does, so a wrapping
/// <c>UseTimeout(...)</c>/graceful-drain cancel aborts an in-flight active-mode ping too.
/// </para>
/// </remarks>
public class AwsLambdaHealthCheck : IHealthCheck
{
    private readonly IAmazonLambda _amazonLambda;
    private readonly AwsLambdaBenzeneMessageClient _awsLambdaBenzeneMessageClient;
    private readonly string _lambdaName;
    private readonly HealthCheckMode _mode;

    /// <summary>Initializes a new instance of the <see cref="AwsLambdaHealthCheck"/> class with no cancellation-token accessor.</summary>
    /// <param name="lambdaName">The name of the Lambda function to check.</param>
    /// <param name="amazonLambda">The Lambda client used to run the check.</param>
    /// <param name="logger">The logger used by the active-mode invoke path.</param>
    /// <param name="mode">Reachability (default, read-only) or Active (invokes the function — side-effecting).</param>
    public AwsLambdaHealthCheck(string lambdaName, IAmazonLambda amazonLambda, ILogger<AwsLambdaHealthCheck> logger,
        HealthCheckMode mode = HealthCheckMode.Reachability)
        : this(lambdaName, amazonLambda, logger, mode, null)
    {
    }

    /// <summary>
    /// Initializes the check, additionally resolving the ambient cancellation token so the Active-mode
    /// ping's outbound invoke can be aborted the same way the Reachability path already is.
    /// </summary>
    /// <param name="lambdaName">The name of the Lambda function to check.</param>
    /// <param name="amazonLambda">The Lambda client used to run the check.</param>
    /// <param name="logger">The logger used by the active-mode invoke path.</param>
    /// <param name="mode">Reachability (default, read-only) or Active (invokes the function — side-effecting).</param>
    /// <param name="cancellation">Supplies the ambient cancellation token; null observes no cancellation.</param>
    public AwsLambdaHealthCheck(string lambdaName, IAmazonLambda amazonLambda, ILogger<AwsLambdaHealthCheck> logger,
        HealthCheckMode mode, ICancellationTokenAccessor? cancellation)
    {
        _lambdaName = lambdaName;
        _amazonLambda = amazonLambda;
        _mode = mode;
        _awsLambdaBenzeneMessageClient = new AwsLambdaBenzeneMessageClient(lambdaName, amazonLambda, logger, cancellation);
    }

    /// <summary>Runs the check and reports the outcome.</summary>
    public Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var dependencies = new[] { new HealthCheckDependency("Lambda", _lambdaName) };

        // GetFunctionConfigurationAsync (Reachability) forwards THIS call's token directly. The
        // Active-mode ping instead forwards the ambient ICancellationTokenAccessor token resolved at
        // construction (#261) into AwsLambdaBenzeneMessageClient's own invoke - a different token
        // source, but both paths now reach their SDK call. No internal timeout guard here (unlike an
        // earlier version of this type): both paths rely on the processor's uniform per-check timeout
        // wrap (HealthCheckProcessor via TimeOutHealthCheck).
        return _mode == HealthCheckMode.Active
            ? RunAsync(_awsLambdaBenzeneMessageClient.SendMessageAsync<Void, Void>(Benzene.Abstractions.BenzeneTopic.Ping, null),
                r => r.Status == BenzeneResultStatus.Accepted, r => ("Status", (object)r.Status), dependencies)
            : RunAsync(_amazonLambda.GetFunctionConfigurationAsync(_lambdaName, cancellationToken),
                r => r.HttpStatusCode == HttpStatusCode.OK, r => ("Status", (object)r.HttpStatusCode), dependencies);
    }

    private async Task<IHealthCheckResult> RunAsync<T>(Task<T> call, Func<T, bool> isHealthy,
        Func<T, (string Key, object Value)> failInfo, HealthCheckDependency[] dependencies)
    {
        try
        {
            var result = await call;
            if (isHealthy(result))
            {
                return HealthCheckResult.CreateInstance(true, Type, new Dictionary<string, object>(), dependencies);
            }

            var (key, value) = failInfo(result);
            return HealthCheckResult.CreateInstance(false, Type,
                new Dictionary<string, object> { { key, value } }, dependencies);
        }
        catch (Exception ex)
        {
            // Expected failures (function missing, no connectivity, no permission) are a classified
            // result, not a throw. Classify via the shared policy: an authorization failure (401/403, or
            // a known auth error code) is a persistent Failed, anything else a transient Failed, enriched
            // with the SDK error code + status, never the exception message.
            var (errorCode, statusCode) = AwsErrorDetails(ex);
            return HealthCheckError.Classify(Type, ex, dependencies, errorCode, statusCode,
                requiredPermission: _mode == HealthCheckMode.Active ? "lambda:InvokeFunction" : "lambda:GetFunctionConfiguration");
        }
    }

    // Pulls the non-sensitive discriminators AWS already returns off an SDK exception; null for a
    // non-AWS exception (e.g. a raw connectivity failure).
    private static (string? ErrorCode, int? StatusCode) AwsErrorDetails(Exception ex)
        => ex is AmazonServiceException ase ? (ase.ErrorCode, (int)ase.StatusCode) : (null, null);

    /// <summary>The check's identifier: <c>"Lambda"</c> in reachability mode, <c>"Lambda.Active"</c> in active mode.</summary>
    public string Type => _mode == HealthCheckMode.Active ? "Lambda.Active" : "Lambda";
}
