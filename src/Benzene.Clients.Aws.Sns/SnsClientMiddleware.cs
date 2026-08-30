using System;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SimpleNotificationService;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;

namespace Benzene.Clients.Aws.Sns;

/// <summary>
/// Publishes the pipeline context's request to SNS and records the response.
/// </summary>
public class SnsClientMiddleware : IMiddleware<SnsSendMessageContext>, ITerminalMiddleware
{
    private readonly IAmazonSimpleNotificationService _amazonSns;
    private readonly ICancellationTokenAccessor? _cancellation;

    /// <summary>
    /// Initializes a new instance of the <see cref="SnsClientMiddleware"/> class with no
    /// cancellation-token accessor.
    /// </summary>
    /// <param name="amazonSns">The SNS client used to publish the message.</param>
    public SnsClientMiddleware(IAmazonSimpleNotificationService amazonSns)
        : this(amazonSns, null)
    {
    }

    /// <summary>
    /// Initializes the middleware, additionally resolving the ambient cancellation token so an
    /// upstream cancel/timeout aborts the outbound publish instead of running it to completion.
    /// </summary>
    /// <param name="amazonSns">The SNS client used to publish the message.</param>
    /// <param name="cancellation">Supplies the ambient cancellation token; null observes no cancellation.</param>
    public SnsClientMiddleware(IAmazonSimpleNotificationService amazonSns, ICancellationTokenAccessor? cancellation)
    {
        _amazonSns = amazonSns;
        _cancellation = cancellation;
    }

    /// <summary>
    /// Gets the name of this middleware component.
    /// </summary>
    public string Name => nameof(SnsClientMiddleware);

    /// <summary>
    /// Publishes the request to SNS and stores the response on the context. Does not call <paramref name="next"/>.
    /// </summary>
    /// <param name="context">The SNS send message context.</param>
    /// <param name="next">The next middleware in the pipeline (not invoked).</param>
    public async Task HandleAsync(SnsSendMessageContext context, Func<Task> next)
    {
        var cancellationToken = _cancellation?.CancellationToken ?? CancellationToken.None;
        context.Response = await _amazonSns.PublishAsync(context.Request, cancellationToken);
    }
}
