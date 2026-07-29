using System.IO;
using Amazon.Lambda.Core;

namespace Benzene.Aws.Lambda.Core.AwsEventStream;

/// <summary>
/// Provides the raw stream-based context for an AWS Lambda invocation, before the event has been
/// identified as a specific event source type (API Gateway, SQS, SNS, etc.).
/// </summary>
/// <remarks>
/// This is the context type used by the outermost <see cref="Benzene.Core.Middleware.MiddlewarePipelineBuilder{TContext}"/>
/// configured in a Lambda <c>StartUp</c> class. Event-source-specific middleware (API Gateway, SQS, SNS, ...)
/// reads <see cref="Stream"/>, attempts to deserialize it into its own request type, and if successful
/// writes the response to <see cref="Response"/>.
/// </remarks>
public class AwsEventStreamContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AwsEventStreamContext"/> class.
    /// </summary>
    /// <param name="stream">The raw Lambda invocation payload stream.</param>
    /// <param name="lambdaContext">The AWS Lambda execution context for this invocation.</param>
    public AwsEventStreamContext(Stream stream, ILambdaContext lambdaContext)
    {
        Stream = stream;
        Response = new MemoryStream();
        LambdaContext = lambdaContext;
    }

    /// <summary>
    /// Gets the raw Lambda invocation payload stream.
    /// </summary>
    public Stream Stream { get; }

    /// <summary>
    /// Gets whether some middleware claimed this invocation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A plain null check on <see cref="Response"/> cannot answer this: it is assigned in the
    /// constructor and every binding writes into that same stream, so it is never null — which is why
    /// <see cref="AwsLambdaEntryPoint"/>'s "event type has not been recognized" error was unreachable
    /// and an unroutable event returned an empty body with no error at all.
    /// </para>
    /// <para>
    /// Two things count as a claim. Every built-in binding calls <see cref="MarkHandled"/> from
    /// <c>AwsLambdaMiddlewareRouter.MapResponse</c>, so a binding that legitimately produced a
    /// zero-byte response is still recognised. Custom middleware that simply writes bytes into
    /// <see cref="Response"/> counts too, without having to know about this flag — a response body
    /// that exists is a claim by definition. What is left over is the case the error is actually
    /// about: nothing wrote anything, and nothing said it had handled the event.
    /// </para>
    /// </remarks>
    public bool Handled => _markedHandled || Response is { CanSeek: true, Length: > 0 };

    private bool _markedHandled;

    /// <summary>
    /// Marks this invocation as claimed, even if the response body is empty.
    /// </summary>
    /// <remarks>
    /// Middleware only needs this when it handles an event without producing a response body —
    /// writing bytes to <see cref="Response"/> already counts as a claim.
    /// </remarks>
    public void MarkHandled() => _markedHandled = true;

    /// <summary>
    /// Gets the AWS Lambda execution context for this invocation.
    /// </summary>
    public ILambdaContext LambdaContext { get; }

    /// <summary>
    /// Gets or sets the response stream to be returned from the Lambda invocation.
    /// </summary>
    /// <remarks>
    /// Initialized to an empty <see cref="MemoryStream"/>. Middleware that handles the event writes
    /// its response here; if no middleware recognizes the event, this remains unset by any handler
    /// and <see cref="AwsLambdaEntryPoint"/> raises an error.
    /// </remarks>
    public Stream Response { get; set; }
}
