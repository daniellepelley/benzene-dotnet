using System.Linq;
using System.Threading.Tasks;
using Amazon.Lambda.SNSEvents;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Aws.Lambda.Core;
using Benzene.Core.MessageHandlers.Info;
using Benzene.Core.Middleware;

namespace Benzene.Aws.Lambda.Sns;

/// <summary>
/// Processes an SNS batch event by mapping each record to an <see cref="SnsRecordContext"/> and running
/// them all through the middleware pipeline concurrently, tagging the transport as <c>"sns"</c> for the
/// duration.
/// </summary>
public class SnsApplication : SingleContextEscalatingApplicationBase<SnsApplication, SnsRecordContext>, IMiddlewareApplication<SNSEvent>
{
    private readonly SnsOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="SnsApplication"/> class.
    /// </summary>
    /// <param name="pipeline">The built SNS middleware pipeline to run each record through.</param>
    /// <param name="options">
    /// Configures how a handler's exceptions and failure results are handled. Defaults to a new
    /// <see cref="SnsOptions"/> instance (safe-by-default:
    /// <see cref="SnsOptions.RaiseOnFailureStatus"/> on, <see cref="SnsOptions.CatchExceptions"/>
    /// off) if omitted.
    /// </param>
    public SnsApplication(IMiddlewarePipeline<SnsRecordContext> pipeline, SnsOptions options = null)
        : base(
            new TransportMiddlewarePipeline<SnsRecordContext>(TransportNames.Sns, pipeline),
            (options ??= new SnsOptions()).CatchExceptions,
            options.RaiseOnFailureStatus,
            context => context.SnsRecord.Sns.MessageId,
            messageId => new SnsMessageProcessingException(messageId),
            "Processing SNS message {id} failed")
    {
        _options = options;
    }

    /// <summary>
    /// Handles an SNS batch event, running each record through the pipeline in its own service scope.
    /// </summary>
    /// <param name="event">The SNS batch event to process.</param>
    /// <param name="serviceResolverFactory">The service resolver factory used to create a scope per record.</param>
    /// <returns>
    /// A task that completes once every record has been processed. Whether a record's exception or
    /// failure result propagates out of this call (and therefore out of the Lambda invocation, so
    /// SNS's own subscription retry policy applies) is governed by <see cref="SnsOptions.CatchExceptions"/>
    /// and <see cref="SnsOptions.RaiseOnFailureStatus"/>.
    /// </returns>
    public async Task HandleAsync(SNSEvent @event, IServiceResolverFactory serviceResolverFactory)
    {
        // BoundedFanOut optionally caps how many records run at once (SnsOptions.MaxDegreeOfParallelism);
        // unset leaves the fan-out unbounded, exactly as before.
        var contexts = @event.Records.Select(record => SnsRecordContext.CreateInstance(@event, record));
        await BoundedFanOut.WhenAllAsync(contexts, context => ProcessAsync(context, serviceResolverFactory), _options.MaxDegreeOfParallelism);
    }
}
