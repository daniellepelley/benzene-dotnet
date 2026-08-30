using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Results;
using Benzene.Abstractions.Serialization;
using Benzene.Results;
using Microsoft.Extensions.Logging;

namespace Benzene.Clients.Aws.StepFunctions;

/// <summary>
/// Starts executions of an AWS Step Functions state machine.
/// </summary>
public class StepFunctionsClient : IStepFunctionsClient
{
    private readonly ILogger<StepFunctionsClient> _logger;
    private readonly string _stateMachineArn;
    private readonly IAmazonStepFunctions _amazonStepFunctionsClient;
    private readonly ISerializer _serializer;
    private readonly ICancellationTokenAccessor? _cancellation;

    /// <summary>
    /// Initializes a new instance of the <see cref="StepFunctionsClient"/> class with no
    /// cancellation-token accessor.
    /// </summary>
    /// <param name="stateMachineArn">The ARN of the state machine to start executions on.</param>
    /// <param name="amazonStepFunctionsClient">The Step Functions client used to start executions.</param>
    /// <param name="logger">The logger used to record send failures.</param>
    public StepFunctionsClient(string stateMachineArn, IAmazonStepFunctions amazonStepFunctionsClient, ILogger<StepFunctionsClient> logger)
        : this(stateMachineArn, amazonStepFunctionsClient, logger, null)
    {
    }

    /// <summary>
    /// Initializes the client, additionally resolving the ambient cancellation token so an upstream
    /// cancel/timeout aborts an in-flight <c>StartExecution</c>/<c>DescribeExecution</c> call instead of
    /// running it to completion.
    /// </summary>
    /// <param name="stateMachineArn">The ARN of the state machine to start executions on.</param>
    /// <param name="amazonStepFunctionsClient">The Step Functions client used to start executions.</param>
    /// <param name="logger">The logger used to record send failures.</param>
    /// <param name="cancellation">Supplies the ambient cancellation token; null observes no cancellation.</param>
    public StepFunctionsClient(string stateMachineArn, IAmazonStepFunctions amazonStepFunctionsClient, ILogger<StepFunctionsClient> logger,
        ICancellationTokenAccessor? cancellation)
    {
        _amazonStepFunctionsClient = amazonStepFunctionsClient;
        _logger = logger;
        _stateMachineArn = stateMachineArn;
        _serializer = new JsonSerializer();
        _cancellation = cancellation;
    }

    /// <summary>
    /// Starts a new execution of the state machine with the given message as its input.
    /// </summary>
    /// <typeparam name="TMessage">The type of the input message.</typeparam>
    /// <typeparam name="TResponse">The expected response payload type.</typeparam>
    /// <param name="message">The message to serialize as the execution input.</param>
    /// <returns>
    /// A task that resolves to an accepted result if the execution started successfully, or a
    /// service-unavailable result if starting it threw.
    /// </returns>
    public Task<IBenzeneResult<TResponse>> StartExecutionAsync<TMessage, TResponse>(TMessage message)
    {
        return StartExecutionAsync<TMessage, TResponse>(message, executionName: null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Callers relying on the idempotency name (<paramref name="executionName"/>) must serialize
    /// <paramref name="message"/> deterministically - the same logical input must always produce the
    /// exact same serialized string. On an <see cref="ExecutionAlreadyExistsException"/>, the already-
    /// started execution's input is compared to this call's serialized input as an exact string, so a
    /// non-deterministic serializer (e.g. one that doesn't sort dictionary keys, or embeds a
    /// timestamp) would make an actually-idempotent retry look like a conflicting input and get
    /// rejected. See <see cref="BenzeneResultStatus.Conflict"/> below for what happens on a genuine
    /// mismatch.
    /// </remarks>
    public async Task<IBenzeneResult<TResponse>> StartExecutionAsync<TMessage, TResponse>(TMessage message, string? executionName)
    {
        var name = SanitizeExecutionName(executionName);
        var input = _serializer.Serialize(message);

        var cancellationToken = _cancellation?.CancellationToken ?? CancellationToken.None;

        try
        {
            await _amazonStepFunctionsClient.StartExecutionAsync(new StartExecutionRequest
            {
                StateMachineArn = _stateMachineArn,
                Input = input,
                // Null Name lets AWS generate a UUID (the original behavior); a supplied name makes the
                // start idempotent for the same (state machine, name, input).
                Name = name
            }, cancellationToken);

            return BenzeneResult.Accepted<TResponse>();
        }
        catch (ExecutionAlreadyExistsException)
        {
            return await HandleExecutionAlreadyExistsAsync<TResponse>(name, input, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sending message to {receiver} failed", _stateMachineArn);
            return BenzeneResult.ServiceUnavailable<TResponse>(ex.Message);
        }
    }

    /// <summary>
    /// Resolves an <see cref="ExecutionAlreadyExistsException"/> caught starting an execution named
    /// <paramref name="name"/> with the given serialized <paramref name="input"/>. The idempotency
    /// name was already used - but a name collision alone doesn't prove this call's payload is what
    /// actually started, so the existing execution's input is fetched and compared byte-for-byte
    /// before deciding.
    /// </summary>
    /// <remarks>
    /// <b>Rejected alternative (recorded, do not reintroduce):</b> a distinct "already started,
    /// unverified" success status was considered and rejected - it would recreate exactly the silent
    /// wrong-input hazard this method exists to close (a caller seeing any flavor of success has no
    /// reason to suspect its payload never ran). A verified match is <see cref="BenzeneResultStatus.Accepted"/>;
    /// anything else is an explicit failure. See <c>work/bug-fix-designs-2026-08.md</c> WP-6b.
    /// </remarks>
    private async Task<IBenzeneResult<TResponse>> HandleExecutionAlreadyExistsAsync<TResponse>(string? name, string input, CancellationToken cancellationToken)
    {
        if (name is null)
        {
            // Only reachable if AWS's own randomly-generated UUID execution name collided - not
            // practically possible (there is no caller-supplied name to have collided on, and this
            // call therefore can't be a genuine idempotent retry of a previous call). Preserve the
            // historical treat-as-success behavior for this vanishingly unlikely case.
            return BenzeneResult.Accepted<TResponse>();
        }

        DescribeExecutionResponse existingExecution;
        try
        {
            existingExecution = await _amazonStepFunctionsClient.DescribeExecutionAsync(new DescribeExecutionRequest
            {
                ExecutionArn = BuildExecutionArn(name)
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Execution name {name} on {receiver} already exists, but DescribeExecution to verify its input failed",
                name, _stateMachineArn);
            return BenzeneResult.ServiceUnavailable<TResponse>(ex.Message);
        }

        if (existingExecution.Input == input)
        {
            // A true idempotent duplicate: the same (state machine, name, input) as a prior attempt
            // (e.g. before a lost response). Treat the retry as success rather than a failure.
            return BenzeneResult.Accepted<TResponse>();
        }

        // The name collided but the input differs - this call's payload was NOT started. Reporting
        // Accepted here would be a false positive the caller has no way to detect; report Conflict
        // instead so it can decide (retry with a fresh name, alert, etc.). The original execution
        // (whatever input it actually holds) is left completely untouched by this path.
        _logger.LogWarning(
            "Execution name {name} on {receiver} already exists with different input - this call's payload was not started",
            name, _stateMachineArn);
        return BenzeneResult.Conflict<TResponse>(
            $"An execution named '{name}' already exists on '{_stateMachineArn}' with a different input. This call's payload was not started.");
    }

    /// <summary>
    /// Builds the execution ARN for <paramref name="executionName"/> under this client's state machine,
    /// by substituting the state machine ARN's <c>stateMachine</c> resource segment for <c>execution</c>
    /// and appending the execution name (the AWS-documented execution ARN shape:
    /// <c>arn:&lt;partition&gt;:states:&lt;region&gt;:&lt;account&gt;:execution:&lt;state-machine-name&gt;:&lt;execution-name&gt;</c>).
    /// </summary>
    private string BuildExecutionArn(string executionName)
    {
        const string stateMachineMarker = ":stateMachine:";
        var markerIndex = _stateMachineArn.IndexOf(stateMachineMarker, StringComparison.Ordinal);

        if (markerIndex < 0)
        {
            // Not a recognizable state machine ARN shape (e.g. a test double) - fall back to a
            // deterministic derived value rather than throwing.
            return $"{_stateMachineArn}:execution:{executionName}";
        }

        var prefix = _stateMachineArn.Substring(0, markerIndex);
        var stateMachineName = _stateMachineArn.Substring(markerIndex + stateMachineMarker.Length);
        return $"{prefix}:execution:{stateMachineName}:{executionName}";
    }

    // 71 (truncated sanitized prefix) + 1 ('-' separator) + 8 (hex hash) = 80, Step Functions' name
    // length cap.
    private const int TruncatedNamePrefixLength = 71;
    private const int HashSuffixLength = 8;

    /// <summary>
    /// Sanitizes an idempotency token into a valid Step Functions execution name: Step Functions
    /// rejects whitespace, control characters, and the set <c>&lt; &gt; { } [ ] ? * " # % \ ^ | ~ ` $ &amp; , ; : /</c>,
    /// and caps the name at 80 characters. Returns <c>null</c> for a null/empty token so AWS generates
    /// a UUID name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An already-clean name (no disallowed character, no more than 80 characters) is returned
    /// unchanged. Otherwise, disallowed characters are first replaced with <c>-</c>, and then - because
    /// EITHER that replacement OR the 80-character cap makes the result no longer uniquely identify the
    /// original input - the result is the replaced name truncated to <see cref="TruncatedNamePrefixLength"/>
    /// characters, a <c>-</c>, and the first <see cref="HashSuffixLength"/> lowercase hex characters of
    /// SHA-256(original name, UTF-8).
    /// </para>
    /// <para>
    /// Both cases this guards against are otherwise silent: two distinct names that replace to the
    /// SAME string (e.g. <c>"a/b"</c> and <c>"a.b"</c>, if both <c>/</c> and <c>.</c> mapped to the same
    /// replacement) or that are identical for the first 80 characters would, without the hash suffix,
    /// collide onto the same Step Functions execution name. That collision would then defeat (b)'s
    /// <see cref="ExecutionAlreadyExistsException"/>/<c>DescribeExecution</c> idempotency check - two
    /// callers with genuinely different inputs would appear to be retrying the SAME logical call. The
    /// hash is computed from the ORIGINAL (pre-replacement, pre-truncation) name specifically so it
    /// stays deterministic (same input always produces the same execution name, which (b)'s idempotent
    /// start relies on) while being collision-resistant across distinct originals that happen to
    /// sanitize or truncate alike.
    /// </para>
    /// </remarks>
    private static string? SanitizeExecutionName(string? executionName)
    {
        if (string.IsNullOrEmpty(executionName))
        {
            return null;
        }

        var builder = new StringBuilder(executionName.Length);
        foreach (var c in executionName)
        {
            var allowed = !char.IsWhiteSpace(c) && !char.IsControl(c) &&
                          "<>{}[]?*\"#%\\^|~`$&,;:/".IndexOf(c) < 0;
            builder.Append(allowed ? c : '-');
        }

        var replaced = builder.ToString();

        if (replaced == executionName && executionName.Length <= TruncatedNamePrefixLength + 1 + HashSuffixLength)
        {
            // Nothing was replaced, and the name is already within Step Functions' 80-character cap:
            // leave it exactly as given.
            return executionName;
        }

        var truncated = replaced.Length > TruncatedNamePrefixLength ? replaced.Substring(0, TruncatedNamePrefixLength) : replaced;
        return $"{truncated}-{HashOriginalName(executionName)}";
    }

    private static string HashOriginalName(string executionName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(executionName));
        return Convert.ToHexStringLower(hash)[..HashSuffixLength];
    }

    /// <summary>
    /// Disposes the client. No-op; the client holds no disposable resources of its own.
    /// </summary>
    public void Dispose()
    {
        // Method intentionally left empty.
    }
}
