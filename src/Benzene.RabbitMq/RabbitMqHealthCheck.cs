using Benzene.HealthChecks.Core;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Benzene.RabbitMq;

/// <summary>
/// Verifies a RabbitMQ consumer can reach its broker and that the consumed queue exists, with a
/// <b>passive</b> queue declare — the RabbitMQ analogue of the other reachability checks, non-destructive:
/// a passive declare neither creates nor mutates the queue (it returns the queue's message/consumer
/// counts, or a channel-level <c>404</c> if the queue is gone). Reported on the <b>dependency</b> category
/// (deep <c>healthcheck</c> layer only — a broker being unreachable is shared-fate; see
/// <see cref="IDependencyHealthCheck"/>). A permission failure (AMQP <c>403 access-refused</c>) is a
/// <b>persistent</b> <see cref="HealthCheckStatus.Failed"/> — it surfaces as unhealthy even for the
/// auto-wired dependency check rather than being softened to a Warning (§3.9, reversed), since a missing
/// permission is a deterministic misconfiguration that won't self-heal; the exception message is never included.
/// </summary>
public class RabbitMqHealthCheck : IHealthCheck
{
    /// <summary>The default timeout for the connect + passive-declare round-trip.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly IRabbitMqConnectionProvider _connectionProvider;
    private readonly string _queueName;
    private readonly TimeSpan _timeout;

    /// <summary>Initializes a new instance of the <see cref="RabbitMqHealthCheck"/> class.</summary>
    /// <param name="connectionProvider">Supplies the reused connection the check opens a channel on.</param>
    /// <param name="queueName">The queue whose existence to verify (reported as the dependency).</param>
    /// <param name="timeout">The connect + declare timeout. Defaults to <see cref="DefaultTimeout"/>.</param>
    public RabbitMqHealthCheck(IRabbitMqConnectionProvider connectionProvider, string queueName, TimeSpan? timeout = null)
    {
        _connectionProvider = connectionProvider;
        _queueName = queueName;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>The check's identifier: <c>"RabbitMq"</c>.</summary>
    public string Type => "RabbitMq";

    /// <summary>Runs the check and reports the outcome.</summary>
    public async Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var dependencies = new[] { new HealthCheckDependency("Queue", _queueName) };
        // Bound the broker round-trip so a half-open connection can't hang past the check's budget.
        // Linked with the caller's token so the processor's own timeout can also cancel the round-trip.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        IChannel? channel = null;

        try
        {
            var connection = await _connectionProvider.GetConnectionAsync(cts.Token);
            channel = await connection.CreateChannelAsync(cancellationToken: cts.Token);
            // Passive declare: read-only existence + reachability, no create/mutate.
            await channel.QueueDeclarePassiveAsync(_queueName, cts.Token);
            return HealthCheckResult.CreateInstance(true, Type,
                new Dictionary<string, object> { { "Queue", _queueName } }, dependencies);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's own token fired (ambient shutdown, or the processor's per-check timeout via
            // TimeOutHealthCheck) - not this check's own connect+declare budget (_timeout). Propagate
            // rather than reporting it as an ordinary connectivity failure, so ExceptionHandlingHealthCheck
            // (which every check runs under via HealthCheckProcessor) classifies it as the distinct
            // "Cancelled" outcome (WP-K), the same way TcpHealthCheck's own catch/rethrow does.
            throw;
        }
        catch (Exception ex)
        {
            // A half-open connection hangs past this check's own budget -> _timeout elapses, cancelling
            // cts.Token independently of the caller's cancellationToken -> a genuine transient Failed, not
            // "Cancelled": HealthCheckError.Classify now re-throws any OperationCanceledException it is
            // given (WP-K), which would be wrong here since this cancellation is this check's own SLA, not
            // caller-driven - so build the failed result directly instead of routing it through Classify.
            // Every other expected failure (broker unreachable, queue missing, no permission) is still a
            // classified result via the shared policy: an authorization failure (401/403, or a known auth
            // error code) is a persistent Failed, anything else a transient Failed, enriched with the AMQP
            // reply code, never the exception message.
            if (ex is OperationCanceledException)
            {
                return HealthCheckResult.CreateInstance(false, Type,
                    new Dictionary<string, object> { { "Queue", _queueName }, { "Error", "Timed Out" } },
                    dependencies);
            }

            var (errorCode, statusCode) = RabbitMqErrorDetails(ex);
            return HealthCheckError.Classify(Type, ex, dependencies, errorCode, statusCode,
                new Dictionary<string, object> { { "Queue", _queueName } });
        }
        finally
        {
            if (channel is not null)
            {
                // A channel-level exception (e.g. 404) has already closed the channel; dispose best-effort.
                try { await channel.DisposeAsync(); } catch { /* already closed */ }
            }
        }
    }

    // RabbitMQ is not HTTP, but AMQP reply codes are 3-digit and align with the shared policy: 403
    // (access-refused) -> persistent Failed, 404 (not-found) -> transient Failed. Report the reply code
    // as both the error code and the status; null for a non-AMQP exception (e.g. a raw socket failure or
    // a timeout).
    private static (string? ErrorCode, int? StatusCode) RabbitMqErrorDetails(Exception ex)
    {
        if (ex is OperationInterruptedException oie && oie.ShutdownReason is not null)
        {
            int replyCode = oie.ShutdownReason.ReplyCode;
            return (replyCode.ToString(), replyCode);
        }

        return (null, null);
    }
}
