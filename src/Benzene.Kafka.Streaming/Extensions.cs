using Benzene.Abstractions.Middleware;
using Benzene.Core.Middleware;
using Benzene.Kafka.Core;
using Benzene.SelfHost;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Benzene.Kafka.Streaming;

/// <summary>
/// Provides extension methods for adding a windowed, checkpointed Kafka stream consumer to a Benzene
/// worker.
/// </summary>
/// <remarks>
/// The streaming sibling of <c>Benzene.Kafka.Core</c>'s <c>UseKafka&lt;TKey,TValue&gt;</c>: same
/// self-hosted worker model, same consumer factory seam, same health check — but the pipeline is a
/// fan-in <see cref="StreamContext{TItem}"/> over accumulated batches rather than one context (and
/// one message-handler route) per record.
/// </remarks>
public static class Extensions
{
    /// <summary>
    /// Adds a windowed, partitioned and checkpointed Kafka consumer to the worker. Records are
    /// accumulated into batches — flushed on <see cref="KafkaStreamOptions.MaxBatchSize"/> or
    /// <see cref="KafkaStreamOptions.MaxBatchWait"/>, whichever comes first — and each batch is
    /// handed to the pipeline as one <see cref="StreamContext{TItem}"/> of
    /// <see cref="ConsumeResult{TKey,TValue}"/>, consumed with <c>UseStream(...)</c>.
    /// </summary>
    /// <remarks>
    /// There is no <c>UseMessageHandlers()</c>-style routing on this binding — a stream is handed to
    /// the handler intact, exactly as with <c>UseKinesisStream</c> and
    /// <c>UseCosmosDbChangeFeed</c>. Use <c>UseKafka&lt;TKey,TValue&gt;</c> instead when you want
    /// per-record message-handler dispatch.
    /// </remarks>
    /// <typeparam name="TKey">The consumer's key type.</typeparam>
    /// <typeparam name="TValue">The consumer's value type.</typeparam>
    /// <param name="app">The worker startup to add the Kafka stream consumer to.</param>
    /// <param name="benzeneKafkaConfig">
    /// The Kafka connection configuration (shared with <c>UseKafka</c>). This binding reads
    /// <c>ConsumerConfig</c>, <c>Topics</c>, <c>DrainTimeout</c> and <c>ConsumeExceptionRetryDelay</c>;
    /// the per-record dispatch knobs on it describe a fan-out model streaming doesn't use.
    /// </param>
    /// <param name="action">The action that configures the inner Kafka stream pipeline.</param>
    /// <param name="options">
    /// Windowing and checkpointing options. Defaults to a new <see cref="KafkaStreamOptions"/>
    /// (500-record / 1-second windows, <see cref="KafkaStreamOptions.AutoCheckpointOnSuccess"/> on,
    /// retry-on-failure) if omitted.
    /// </param>
    /// <param name="consumerFactory">
    /// Optionally supplies the underlying <c>IConsumer</c> — for <c>ConsumerBuilder</c>
    /// configuration plain <c>ConsumerConfig</c> can't express (deserializers, handlers,
    /// <c>SetOAuthBearerTokenRefreshHandler</c> for secretless OAUTHBEARER auth). Defaults to
    /// building straight from the config.
    /// </param>
    /// <param name="healthCheck">
    /// When <c>true</c> (the default) the same non-destructive Kafka reachability check
    /// <c>UseKafka</c> registers (cluster metadata + subscribed-topic existence) is auto-registered
    /// on the deep <c>healthcheck</c> layer. Pass <c>false</c> to opt out.
    /// </param>
    /// <returns>The worker startup for method chaining.</returns>
    /// <example>
    /// <code>
    /// app.UseWorker(worker => worker.UseKafkaStream&lt;Ignore, string&gt;(
    ///     new BenzeneKafkaConfig
    ///     {
    ///         ConsumerConfig = new ConsumerConfig { BootstrapServers = "localhost:9092", GroupId = "aggregator" },
    ///         Topics = new[] { "market-data" },
    ///     },
    ///     stream => stream.UseStream&lt;ConsumeResult&lt;Ignore, string&gt;&gt;(async context =>
    ///     {
    ///         await foreach (var partition in context.Items.PartitionBy(r => r.TopicPartition, context.CancellationToken))
    ///         {
    ///             foreach (var window in partition.Value.Chunk(50))
    ///             {
    ///                 // aggregate the window, then mark the partition safe up to its last record
    ///                 await context.Checkpointer.CheckpointAsync(window[^1]);
    ///             }
    ///         }
    ///     }),
    ///     new KafkaStreamOptions { MaxBatchSize = 1000, MaxBatchWait = TimeSpan.FromMilliseconds(250) }));
    /// </code>
    /// </example>
    public static IBenzeneWorkerStartup UseKafkaStream<TKey, TValue>(this IBenzeneWorkerStartup app,
        BenzeneKafkaConfig benzeneKafkaConfig,
        Action<IMiddlewarePipelineBuilder<StreamContext<ConsumeResult<TKey, TValue>>>> action,
        KafkaStreamOptions? options = null,
        IKafkaConsumerFactory<TKey, TValue>? consumerFactory = null,
        bool healthCheck = true)
    {
        // Reuses Benzene.Kafka.Core's registrations wholesale (transport info + the Kafka mappers)
        // rather than duplicating them. AddBenzeneMessage() is deliberately not called: a stream
        // carries no message envelope to route on, matching UseCosmosDbChangeFeed.
        app.Register(x => x.AddKafka<TKey, TValue>());

        if (healthCheck)
        {
            app.Register(x => x.AddKafkaDependencyHealthCheck(benzeneKafkaConfig));
        }

        var middlewarePipelineBuilder = app.Create<StreamContext<ConsumeResult<TKey, TValue>>>();
        action(middlewarePipelineBuilder);
        var pipeline = middlewarePipelineBuilder.Build();

        var application = new KafkaStreamApplication<TKey, TValue>(pipeline);

        app.Add(serviceResolverFactory =>
        {
            using var scope = serviceResolverFactory.CreateScope();
            var logger = scope.GetService<ILogger<BenzeneKafkaStreamWorker<TKey, TValue>>>();
            return new BenzeneKafkaStreamWorker<TKey, TValue>(serviceResolverFactory, application,
                benzeneKafkaConfig, options, logger, consumerFactory);
        });

        return app;
    }
}
