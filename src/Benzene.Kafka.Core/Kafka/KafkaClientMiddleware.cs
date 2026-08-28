using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Confluent.Kafka;

namespace Benzene.Kafka.Core.Kafka;

/// <summary>Terminal middleware that produces the context's message to Kafka and records the delivery result.</summary>
public class KafkaClientMiddleware : IMiddleware<KafkaSendMessageContext>, ITerminalMiddleware
{
    private readonly IProducer<string, string> _producer;
    private readonly ICancellationTokenAccessor? _cancellation;

    /// <summary>Initializes a new instance of the <see cref="KafkaClientMiddleware"/> class.</summary>
    /// <param name="producer">The Kafka producer used to produce messages.</param>
    /// <param name="cancellation">
    /// Supplies the ambient cancellation token to pass into the produce call (the
    /// <c>HttpBenzeneMessageClient</c> constructor-optional accessor idiom); null observes no
    /// cancellation. Resolved automatically from the container on the DI-registered
    /// <c>UseKafkaClient()</c> path; the explicit-producer <c>UseKafkaClient(producer)</c> overload
    /// resolves it from the pipeline's service resolver and passes it through.
    /// </param>
    public KafkaClientMiddleware(IProducer<string, string> producer, ICancellationTokenAccessor? cancellation = null)
    {
        _producer = producer;
        _cancellation = cancellation;
    }

    /// <summary>Gets the name of this middleware.</summary>
    public string Name => nameof(KafkaClientMiddleware);

    /// <summary>Produces the context's message and records the delivery result. Terminal middleware; does not call <paramref name="next"/>.</summary>
    public async Task HandleAsync(KafkaSendMessageContext context, Func<Task> next)
    {
        var cancellationToken = _cancellation?.CancellationToken ?? CancellationToken.None;
        context.Response = await _producer.ProduceAsync(context.Topic, context.Message, cancellationToken);
    }
}
