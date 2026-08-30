using System;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SQS;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;

namespace Benzene.Clients.Aws.Sqs;

/// <summary>
/// Middleware that sends the <see cref="SqsSendMessageContext"/>'s request to SQS and records the
/// response on the context.
/// </summary>
public class SqsClientMiddleware : IMiddleware<SqsSendMessageContext>, ITerminalMiddleware
{
    private readonly IAmazonSQS _amazonSqs;
    private readonly ICancellationTokenAccessor? _cancellation;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqsClientMiddleware"/> class with no
    /// cancellation-token accessor.
    /// </summary>
    /// <param name="amazonSqs">The SQS client used to send the message.</param>
    public SqsClientMiddleware(IAmazonSQS amazonSqs)
        : this(amazonSqs, null)
    {
    }

    /// <summary>
    /// Initializes the middleware, additionally resolving the ambient cancellation token so an
    /// upstream cancel/timeout aborts the outbound send instead of running it to completion.
    /// </summary>
    /// <param name="amazonSqs">The SQS client used to send the message.</param>
    /// <param name="cancellation">Supplies the ambient cancellation token; null observes no cancellation.</param>
    public SqsClientMiddleware(IAmazonSQS amazonSqs, ICancellationTokenAccessor? cancellation)
    {
        _amazonSqs = amazonSqs;
        _cancellation = cancellation;
    }

    /// <summary>
    /// Gets the name of this middleware.
    /// </summary>
    public string Name => nameof(SqsClientMiddleware);

    /// <summary>
    /// Sends the context's request to SQS and sets the response. This is a terminal middleware; it does
    /// not call <paramref name="next"/>.
    /// </summary>
    /// <param name="context">The context carrying the request to send and to receive the response.</param>
    /// <param name="next">Unused; this middleware does not delegate further down the pipeline.</param>
    public async Task HandleAsync(SqsSendMessageContext context, Func<Task> next)
    {
        var cancellationToken = _cancellation?.CancellationToken ?? CancellationToken.None;
        context.Response = await _amazonSqs.SendMessageAsync(context.Request, cancellationToken);
    }
}
