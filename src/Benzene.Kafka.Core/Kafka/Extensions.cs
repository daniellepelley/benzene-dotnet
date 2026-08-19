using Benzene.Abstractions.Messages.BenzeneClient;
using Benzene.Abstractions.Middleware;
using Benzene.Clients;
using Benzene.Core.Middleware;
using Confluent.Kafka;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Kafka.Core.Kafka;

public static class Extensions
{
    public static IMiddlewarePipelineBuilder<KafkaSendMessageContext> UseKafkaClient(
        this IMiddlewarePipelineBuilder<KafkaSendMessageContext> app, IProducer<string, string> producer)
    {
        return app.Use(_ => new KafkaClientMiddleware(producer));
    }
 
    public static IMiddlewarePipelineBuilder<KafkaSendMessageContext> UseKafkaClient(
        this IMiddlewarePipelineBuilder<KafkaSendMessageContext> app)
    {
        app.Register(x => x.AddScoped<KafkaClientMiddleware>());
        return app.Use<KafkaSendMessageContext, KafkaClientMiddleware>();
    }
   
    public static IMiddlewarePipelineBuilder<TContext> Convert<TContext, TContextOut>(this IMiddlewarePipelineBuilder<TContext> app,
        IContextConverter<TContext, TContextOut> converter, Action<IMiddlewarePipelineBuilder<TContextOut>> action)
    {
        var middlewarePipeline = app.CreateMiddlewarePipeline(action);
        return app.Use(serviceResolver => new ContextConverterMiddleware<TContext, TContextOut>(converter, middlewarePipeline, serviceResolver));
    }
    
    public static IMiddlewarePipelineBuilder<IBenzeneClientContext<T, Void>> UseKafka<T>(this IMiddlewarePipelineBuilder<IBenzeneClientContext<T, Void>> app,
        Action<IMiddlewarePipelineBuilder<KafkaSendMessageContext>> action, string keyHeader = null)
    {
        return Convert(app, new KafkaContextConverter<T>(new Benzene.Core.MessageHandlers.Serialization.JsonSerializer(), keyHeader), action);
    }

    public static IMiddlewarePipelineBuilder<IBenzeneClientContext<T, Void>> UseKafka<T>(this IMiddlewarePipelineBuilder<IBenzeneClientContext<T, Void>> app, string keyHeader = null)
    {
        return app.Convert(new KafkaContextConverter<T>(new Benzene.Core.MessageHandlers.Serialization.JsonSerializer(), keyHeader), builder => builder.UseKafkaClient());
    }

    /// <summary>
    /// Converts an outbound route pipeline (<c>OutboundRoutingBuilder.Route</c>) to produce via Kafka,
    /// using a custom middleware configuration - the <see cref="OutboundContext"/> counterpart of
    /// <see cref="UseKafka{T}(IMiddlewarePipelineBuilder{IBenzeneClientContext{T, Void}}, Action{IMiddlewarePipelineBuilder{KafkaSendMessageContext}}, string)"/>,
    /// mirroring <c>UseSqs</c>/<c>UseServiceBus</c>/<c>UseInProcess</c>.
    /// </summary>
    /// <param name="app">The outbound pipeline builder to convert.</param>
    /// <param name="action">Configures the inner Kafka produce pipeline.</param>
    /// <param name="keyHeader">
    /// The outbound header whose value becomes the Kafka message key (hash(key) → partition).
    /// <c>null</c> (the default) produces a keyless message.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// This is a shorthand over the explicit form, which you can write yourself from public API:
    /// <c>app.Convert(new OutboundKafkaContextConverter(keyHeader), action)</c>. Drop to that when you
    /// want to supply your own <c>ISerializer</c>.
    /// </remarks>
    public static IMiddlewarePipelineBuilder<OutboundContext> UseKafka(
        this IMiddlewarePipelineBuilder<OutboundContext> app,
        Action<IMiddlewarePipelineBuilder<KafkaSendMessageContext>> action, string? keyHeader = null)
    {
        return app.Convert(new OutboundKafkaContextConverter(keyHeader), action);
    }

    /// <summary>
    /// Converts an outbound route pipeline (<c>OutboundRoutingBuilder.Route</c>) to produce via Kafka,
    /// using the default <see cref="KafkaClientMiddleware"/> configuration (which resolves its
    /// <c>IProducer&lt;string,string&gt;</c> from the container).
    /// </summary>
    /// <param name="app">The outbound pipeline builder to convert.</param>
    /// <param name="keyHeader">
    /// The outbound header whose value becomes the Kafka message key. <c>null</c> (the default)
    /// produces a keyless message.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// One rung of shorthand over
    /// <see cref="UseKafka(IMiddlewarePipelineBuilder{OutboundContext}, Action{IMiddlewarePipelineBuilder{KafkaSendMessageContext}}, string)"/>:
    /// it is exactly <c>app.UseKafka(builder =&gt; builder.UseKafkaClient(), keyHeader)</c>. Drop one
    /// level to that to pass an explicit producer (<c>UseKafkaClient(producer)</c>) or add middleware
    /// around the produce.
    /// </remarks>
    public static IMiddlewarePipelineBuilder<OutboundContext> UseKafka(
        this IMiddlewarePipelineBuilder<OutboundContext> app, string? keyHeader = null)
    {
        return app.UseKafka(builder => builder.UseKafkaClient(), keyHeader);
    }
}
