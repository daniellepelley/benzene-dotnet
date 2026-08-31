using Benzene.Abstractions.DI;
using Benzene.Abstractions.Messages.BenzeneClient;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Results;
using Benzene.Abstractions.Serialization;
using Benzene.Clients;
using Benzene.Core.Middleware;
using Benzene.Results;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Kafka.Core.Kafka;

/// <summary>
/// An <see cref="IBenzeneMessageClient"/> that produces outbound messages to Kafka, so business logic
/// depends only on <c>IBenzeneMessageSender</c>/<c>IBenzeneMessageClient</c> and stays
/// transport-agnostic. A message is converted to a <see cref="KafkaSendMessageContext"/> and run
/// through a one-middleware produce pipeline.
/// </summary>
public class KafkaBenzeneMessageClient : IBenzeneMessageClient
{
    // Shared across every SendMessageAsync call rather than constructed per call: JsonSerializer
    // wraps a JsonSerializerOptions instance, and System.Text.Json caches resolved converters/type
    // metadata per JsonSerializerOptions instance - a fresh one per send would silently defeat that
    // cache on every single outbound message. Benzene.Clients.JsonSerializer.Shared is the one
    // instance every outbound client package (Benzene.Clients.Http, Benzene.RabbitMq, this one) draws
    // from rather than each declaring its own.
    private static readonly ISerializer SharedSerializer = JsonSerializer.Shared;

    private readonly ILogger<KafkaBenzeneMessageClient> _logger;
    private readonly IServiceResolver _serviceResolver;
    private readonly IMiddlewarePipeline<KafkaSendMessageContext> _middlewarePipeline;

    /// <summary>Initializes a new instance publishing on the given producer.</summary>
    /// <param name="producer">The Kafka producer to publish with.</param>
    /// <param name="logger">Logs a send failure.</param>
    /// <param name="serviceResolver">The resolver the produce pipeline runs in.</param>
    public KafkaBenzeneMessageClient(IProducer<string, string> producer, ILogger<KafkaBenzeneMessageClient> logger, IServiceResolver serviceResolver)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _serviceResolver = serviceResolver;
        _logger = logger;

        var benzeneServiceContainer = new NullBenzeneServiceContainer();
        var middlewarePipelineBuilder = new MiddlewarePipelineBuilder<KafkaSendMessageContext>(benzeneServiceContainer);
        _middlewarePipeline = middlewarePipelineBuilder
            .UseKafkaClient(producer)
            .Build();
    }

    /// <summary>Initializes a new instance from an already-built produce pipeline (for testing).</summary>
    /// <param name="middlewarePipeline">The produce pipeline to run each message through.</param>
    /// <param name="logger">Logs a send failure.</param>
    /// <param name="serviceResolver">The resolver the produce pipeline runs in.</param>
    public KafkaBenzeneMessageClient(IMiddlewarePipeline<KafkaSendMessageContext> middlewarePipeline, ILogger<KafkaBenzeneMessageClient> logger, IServiceResolver serviceResolver)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _serviceResolver = serviceResolver;
        _logger = logger;
        _middlewarePipeline = middlewarePipeline;
    }

    /// <inheritdoc />
    public async Task<IBenzeneResult<TResponse>> SendMessageAsync<TRequest, TResponse>(IBenzeneClientRequest<TRequest> request)
    {
        try
        {   var converter = new KafkaContextConverter<TRequest>(SharedSerializer);
            var context = await converter.CreateRequestAsync(new BenzeneClientContext<TRequest, Void>(request));

            await _middlewarePipeline.HandleAsync(context, _serviceResolver);

            var response = context.Response;
            
            if (response.Status == PersistenceStatus.Persisted)
            {
                return BenzeneResult.Accepted<TResponse>();
            }

            return BenzeneResult.UnexpectedError<TResponse>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sending message {receiverTopic} failed", request.Topic);
            return BenzeneResult.ServiceUnavailable<TResponse>(ex.Message);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Method intentionally left empty.
    }
}