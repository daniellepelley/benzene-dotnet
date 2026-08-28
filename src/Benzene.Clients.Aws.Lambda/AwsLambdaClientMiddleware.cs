using System;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;

namespace Benzene.Clients.Aws.Lambda;

/// <summary>
/// Middleware that invokes the <see cref="LambdaSendMessageContext"/>'s request against AWS Lambda and
/// records the response on the context.
/// </summary>
public class AwsLambdaClientMiddleware : IMiddleware<LambdaSendMessageContext>, ITerminalMiddleware
{
    private readonly IAmazonLambda _amazonLambda;
    private readonly ICancellationTokenAccessor? _cancellation;

    /// <summary>
    /// Initializes a new instance of the <see cref="AwsLambdaClientMiddleware"/> class.
    /// </summary>
    /// <param name="amazonLambda">The Lambda client used to invoke the function.</param>
    /// <param name="cancellation">
    /// Supplies the ambient cancellation token to pass into the invoke call (the
    /// <c>HttpBenzeneMessageClient</c> constructor-optional accessor idiom); null observes no
    /// cancellation. Resolved automatically from the container on the DI-registered
    /// <c>UseAwsLambdaClient()</c> path; the explicit-client <c>UseAwsLambdaClient(amazonLambda)</c>
    /// overload resolves it from the pipeline's service resolver and passes it through.
    /// </param>
    public AwsLambdaClientMiddleware(IAmazonLambda amazonLambda, ICancellationTokenAccessor? cancellation = null)
    {
        _amazonLambda = amazonLambda;
        _cancellation = cancellation;
    }

    /// <summary>
    /// Gets the name of this middleware.
    /// </summary>
    public string Name => nameof(AwsLambdaClientMiddleware);

    /// <summary>
    /// Invokes the context's request against AWS Lambda and sets the response. This is a terminal
    /// middleware; it does not call <paramref name="next"/>.
    /// </summary>
    /// <param name="context">The context carrying the request to invoke and to receive the response.</param>
    /// <param name="next">Unused; this middleware does not delegate further down the pipeline.</param>
    public async Task HandleAsync(LambdaSendMessageContext context, Func<Task> next)
    {
        var cancellationToken = _cancellation?.CancellationToken ?? CancellationToken.None;
        context.Response = await _amazonLambda.InvokeAsync(context.Request, cancellationToken);
    }
}
