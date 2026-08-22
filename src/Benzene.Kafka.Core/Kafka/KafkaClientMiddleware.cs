using Benzene.Abstractions.Middleware;
using Confluent.Kafka;

namespace Benzene.Kafka.Core.Kafka;

/// <summary>Terminal middleware that produces the context's message to Kafka and records the delivery result.</summary>
public class KafkaClientMiddleware : IMiddleware<KafkaSendMessageContext>, ITerminalMiddleware
{
    private readonly IProducer<string, string> _producer;

    /// <summary>Initializes a new instance of the <see cref="KafkaClientMiddleware"/> class.</summary>
    /// <param name="producer">The Kafka producer used to produce messages.</param>
    public KafkaClientMiddleware(IProducer<string, string> producer)
    {
        _producer = producer;
    }

    /// <summary>Gets the name of this middleware.</summary>
    public string Name => nameof(KafkaClientMiddleware);

    /// <summary>Produces the context's message and records the delivery result. Terminal middleware; does not call <paramref name="next"/>.</summary>
    public async Task HandleAsync(KafkaSendMessageContext context, Func<Task> next)
    {
        context.Response = await _producer.ProduceAsync(context.Topic, context.Message);
    }
}