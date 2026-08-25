using System.Threading.Tasks;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Benzene.Abstractions.Messages.BenzeneClient;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Results;
using Benzene.Abstractions.Serialization;
using Benzene.Results;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Clients.Aws.Lambda;

/// <summary>
/// Converts between a generic Benzene client context and a <see cref="LambdaSendMessageContext"/>, so
/// that a Benzene client pipeline can invoke messages via AWS Lambda.
/// </summary>
/// <typeparam name="T">The type of the outgoing message.</typeparam>
public class LambdaContextConverter<T> : IContextConverter<IBenzeneClientContext<T, Void>, LambdaSendMessageContext>
{
    private readonly ISerializer _serializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="LambdaContextConverter{T}"/> class using a
    /// <see cref="JsonSerializer"/> to serialize the outgoing message.
    /// </summary>
    public LambdaContextConverter()
        :this(new JsonSerializer())
    { }

    /// <summary>
    /// Initializes a new instance of the <see cref="LambdaContextConverter{T}"/> class.
    /// </summary>
    /// <param name="serializer">The serializer used to serialize the outgoing message.</param>
    public LambdaContextConverter(ISerializer serializer)
    {
        _serializer = serializer;
    }

    /// <summary>
    /// Builds a Lambda invoke request context, serializing the outgoing message as the invocation payload.
    /// </summary>
    /// <param name="contextIn">The incoming Benzene client context.</param>
    /// <returns>A task that resolves to the built <see cref="LambdaSendMessageContext"/>.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="IBenzeneClientRequest{T}.Headers"/> is not forwarded here: a raw <see cref="InvokeRequest"/>
    /// has no header-like concept comparable to HTTP/SQS/SNS/Kafka (AWS's own <c>ClientContext</c> feature
    /// is a distinct, base64-encoded JSON blob with different semantics). A decorator like
    /// <c>WithW3CTraceContext()</c> has no effect on a pipeline built via <c>UseAwsLambda()</c>/this
    /// converter specifically. This does not affect the separate, more commonly used
    /// <see cref="Benzene.Clients.Aws.Lambda.AwsLambdaBenzeneMessageClient"/> (wired via
    /// <c>CreateAwsLambdaBenzeneMessageClient()</c>), which already embeds
    /// <see cref="IBenzeneClientRequest{T}.Headers"/> into the <c>BenzeneMessageClientRequest</c> envelope
    /// it invokes with.
    /// </para>
    /// <para>
    /// This shape (<c>IBenzeneClientContext&lt;T, Void&gt;</c>) is Benzene's fire-and-forget client
    /// contract: the built <see cref="InvokeRequest"/> is invoked with
    /// <see cref="InvocationType.Event"/> (async invoke) so the call returns as soon as Lambda has
    /// accepted the invocation, without waiting for the target function to run. A shape that needs to
    /// wait for a result is a request/response invoke (<see cref="InvocationType.RequestResponse"/>),
    /// not this one.
    /// </para>
    /// </remarks>
    public Task<LambdaSendMessageContext> CreateRequestAsync(IBenzeneClientContext<T, Void> contextIn)
    {
        return Task.FromResult(new LambdaSendMessageContext(new InvokeRequest
        {
            InvocationType = InvocationType.Event,
            Payload = _serializer.Serialize(contextIn.Request.Message)
        }));
    }

    /// <summary>
    /// Maps the completed <see cref="LambdaSendMessageContext"/> onto the incoming Benzene client
    /// context, classifying the invocation instead of unconditionally reporting success.
    /// </summary>
    /// <param name="contextIn">The incoming Benzene client context to set the response on.</param>
    /// <param name="contextOut">The completed <see cref="LambdaSendMessageContext"/>.</param>
    /// <returns>A completed task.</returns>
    /// <remarks>
    /// Two invocation shapes are handled, keyed off <c>contextOut.Request.InvocationType</c> (this
    /// converter itself always sends <see cref="InvocationType.Event"/> - see
    /// <see cref="CreateRequestAsync"/> - but the mapping below is written against both so it stays
    /// correct if a decorator ever overrides the request's invocation type):
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="InvocationType.Event"/> (async invoke): Lambda's response carries no function
    /// output, only an HTTP-style <see cref="InvokeResponse.StatusCode"/> confirming the invocation
    /// was accepted. A 2xx status maps to <see cref="BenzeneResultStatus.Accepted"/>; anything else
    /// (e.g. a throttling or validation error surfaced synchronously by the Invoke API itself) maps
    /// to a failure result carrying the status code.
    /// </description></item>
    /// <item><description>
    /// <see cref="InvocationType.RequestResponse"/> (sync invoke): AWS returns HTTP 200 even when the
    /// target function threw, signalling the failure only via a non-null
    /// <see cref="InvokeResponse.FunctionError"/> ("Handled"/"Unhandled") on an otherwise-successful
    /// HTTP response. That case maps to a failure result carrying the error details - it must never
    /// be reported as <see cref="BenzeneResultStatus.Accepted"/>. A null/empty
    /// <see cref="InvokeResponse.FunctionError"/> maps to <see cref="BenzeneResultStatus.Accepted"/>.
    /// </description></item>
    /// </list>
    /// </remarks>
    public Task MapResponseAsync(IBenzeneClientContext<T, Void> contextIn, LambdaSendMessageContext contextOut)
    {
        var response = contextOut.Response;

        if (contextOut.Request.InvocationType == InvocationType.Event)
        {
            contextIn.Response = response.StatusCode is >= 200 and < 300
                ? BenzeneResult.Accepted<Void>()
                : BenzeneResult.ServiceUnavailable<Void>(
                    $"AWS Lambda Event invoke of '{contextOut.Request.FunctionName}' returned status code {response.StatusCode}.");
        }
        else if (!string.IsNullOrEmpty(response.FunctionError))
        {
            contextIn.Response = BenzeneResult.ServiceUnavailable<Void>(
                $"AWS Lambda function '{contextOut.Request.FunctionName}' returned FunctionError '{response.FunctionError}'.");
        }
        else
        {
            contextIn.Response = BenzeneResult.Accepted<Void>();
        }

        return Task.CompletedTask;
    }
}
