using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Benzene.HealthChecks.Core;

namespace Benzene.Clients.Aws.Sqs;

/// <summary>
/// Verifies connectivity to an SQS queue. In the default <see cref="HealthCheckMode.Reachability"/> mode
/// this is a <b>non-destructive</b> read-only <c>GetQueueAttributes</c> call; in
/// <see cref="HealthCheckMode.Active"/> mode it sends a real <c>ping</c> message (side-effecting — the
/// queue's consumer must recognise and drop it).
/// </summary>
/// <remarks>
/// The reachability check proves the queue exists, is reachable, and the credentials can read it
/// (<c>sqs:GetQueueAttributes</c>) — it does <b>not</b> prove a send would succeed (<c>sqs:SendMessage</c>
/// is a different permission). Use <see cref="HealthCheckMode.Active"/> only when you need to exercise the
/// send path, and keep it off a frequent poll and off liveness/readiness probes.
/// <para>
/// No internal timeout guard here (unlike an earlier version of this type): the check forwards the
/// <see cref="CancellationToken"/> it is given straight into the SDK call and relies purely on the
/// processor's uniform per-check timeout wrap (<c>HealthCheckProcessor</c> via <c>TimeOutHealthCheck</c>),
/// which - now that <see cref="ExecuteAsync"/> actually forwards its token - genuinely cancels the
/// in-flight SQS call on timeout rather than merely abandoning the awaited task. Same shape as
/// <c>SnsHealthCheck</c>/<c>EventBridgeHealthCheck</c>.
/// </para>
/// </remarks>
public class SqsHealthCheck : IHealthCheck
{
    private readonly IAmazonSQS _amazonSqs;
    private readonly string _queueUrl;
    private readonly HealthCheckMode _mode;
    private readonly string _topicAttributeKey;

    /// <summary>Initializes a new instance of the <see cref="SqsHealthCheck"/> class.</summary>
    /// <param name="queueUrl">The URL of the queue to check.</param>
    /// <param name="amazonSqs">The SQS client used to run the check.</param>
    /// <param name="mode">Reachability (default, read-only) or Active (sends a ping — side-effecting).</param>
    /// <param name="topicAttributeKey">
    /// Active mode only: the message attribute the ping topic is written to. Defaults to
    /// <see cref="OutboundSqsContextConverter.DefaultTopicAttribute"/> (<c>"topic"</c>) — pass the same
    /// key the queue's consumer routes on so the ping is routable there too.
    /// </param>
    public SqsHealthCheck(string queueUrl, IAmazonSQS amazonSqs,
        HealthCheckMode mode = HealthCheckMode.Reachability,
        string topicAttributeKey = OutboundSqsContextConverter.DefaultTopicAttribute)
    {
        _queueUrl = queueUrl;
        _amazonSqs = amazonSqs;
        _mode = mode;
        _topicAttributeKey = topicAttributeKey;
    }

    /// <summary>Runs the check and reports the outcome.</summary>
    public async Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var dependencies = new[] { new HealthCheckDependency("Queue", _queueUrl) };

        try
        {
            var statusCode = _mode == HealthCheckMode.Active
                ? (await SendPingAsync(cancellationToken)).HttpStatusCode
                : (await _amazonSqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
                {
                    QueueUrl = _queueUrl,
                    AttributeNames = new List<string> { "QueueArn" }
                }, cancellationToken)).HttpStatusCode;

            if (statusCode == HttpStatusCode.OK)
            {
                return HealthCheckResult.CreateInstance(true, Type,
                    new Dictionary<string, object> { { "QueueUrl", _queueUrl } }, dependencies);
            }

            return HealthCheckResult.CreateInstance(false, Type,
                new Dictionary<string, object> { { "QueueUrl", _queueUrl }, { "Error", $"Returned a status of {statusCode}" } }, dependencies);
        }
        catch (Exception ex)
        {
            // Expected failures (queue missing, no connectivity, no permission) are a classified result,
            // not a throw. HealthCheckError applies the shared policy: an authorization failure (401/403,
            // or a known auth error code) is a persistent Failed, anything else a transient Failed,
            // enriched with the SDK error code + status, never the exception message.
            var (errorCode, faultStatus) = AwsErrorDetails(ex);
            return HealthCheckError.Classify(Type, ex, dependencies, errorCode, faultStatus,
                new Dictionary<string, object> { { "QueueUrl", _queueUrl } },
                requiredPermission: _mode == HealthCheckMode.Active ? "sqs:SendMessage" : "sqs:GetQueueAttributes");
        }
    }

    private Task<SendMessageResponse> SendPingAsync(CancellationToken cancellationToken)
        => _amazonSqs.SendMessageAsync(new SendMessageRequest(_queueUrl, "{}")
        {
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                { _topicAttributeKey, new MessageAttributeValue { DataType = "String", StringValue = Benzene.Abstractions.BenzeneTopic.Ping } }
            }
        }, cancellationToken);

    // Pulls the non-sensitive discriminators AWS already returns off an SDK exception; null for a
    // non-AWS exception (e.g. a raw connectivity failure).
    private static (string? ErrorCode, int? StatusCode) AwsErrorDetails(Exception ex)
        => ex is AmazonServiceException ase ? (ase.ErrorCode, (int)ase.StatusCode) : (null, null);

    /// <summary>The check's identifier: <c>"Sqs"</c> in reachability mode, <c>"Sqs.Active"</c> in active mode.</summary>
    public string Type => _mode == HealthCheckMode.Active ? "Sqs.Active" : "Sqs";
}
