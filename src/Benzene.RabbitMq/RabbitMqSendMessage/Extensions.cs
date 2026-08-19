using Benzene.Abstractions.Messages.BenzeneClient;
using Benzene.Abstractions.Middleware;
using Benzene.Clients;
using Benzene.Core.Middleware;
using RabbitMQ.Client;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.RabbitMq.RabbitMqSendMessage;

/// <summary>
/// Pipeline-builder extensions for the outbound RabbitMQ publish path.
/// </summary>
public static class Extensions
{
    /// <summary>Adds the RabbitMQ publish middleware to an outbound pipeline.</summary>
    /// <param name="app">The outbound pipeline builder.</param>
    /// <param name="channel">The RabbitMQ channel to publish on.</param>
    /// <param name="mandatory">Whether an unroutable message is returned rather than dropped.</param>
    /// <param name="persistent">Whether the message is published persistently (delivery mode 2). Defaults to <c>true</c>.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<RabbitMqSendMessageContext> UseRabbitMqClient(
        this IMiddlewarePipelineBuilder<RabbitMqSendMessageContext> app, IChannel channel, bool mandatory = false, bool persistent = true)
    {
        return app.Use(_ => new RabbitMqClientMiddleware(channel, mandatory, persistent));
    }

    /// <summary>
    /// Converts a Benzene outbound client context to a RabbitMQ publish and runs it through the given
    /// inner pipeline - the <c>OutboundRoutingBuilder</c> integration point, mirroring <c>UseKafka</c>.
    /// </summary>
    /// <typeparam name="T">The request message type.</typeparam>
    /// <param name="app">The outbound client pipeline builder.</param>
    /// <param name="exchange">The exchange to publish to (empty string for the default exchange).</param>
    /// <param name="action">Configures the inner RabbitMQ publish pipeline.</param>
    /// <param name="topicHeaderKey">
    /// The message-property header the topic is written to. Defaults to
    /// <see cref="RabbitMqConstants.DefaultTopicHeader"/> (<c>"topic"</c>); pass a different key to
    /// publish for a consumer that routes on another header.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<IBenzeneClientContext<T, Void>> UseRabbitMq<T>(
        this IMiddlewarePipelineBuilder<IBenzeneClientContext<T, Void>> app, string exchange,
        Action<IMiddlewarePipelineBuilder<RabbitMqSendMessageContext>> action,
        string topicHeaderKey = RabbitMqConstants.DefaultTopicHeader)
    {
        var converter = new RabbitMqContextConverter<T>(new Benzene.Core.MessageHandlers.Serialization.JsonSerializer(), exchange, topicHeaderKey);
        var middlewarePipeline = app.CreateMiddlewarePipeline(action);
        return app.Use(serviceResolver => new ContextConverterMiddleware<IBenzeneClientContext<T, Void>, RabbitMqSendMessageContext>(converter, middlewarePipeline, serviceResolver));
    }

    /// <summary>
    /// Converts a Benzene outbound client context to a RabbitMQ publish over the given channel.
    /// </summary>
    /// <typeparam name="T">The request message type.</typeparam>
    /// <param name="app">The outbound client pipeline builder.</param>
    /// <param name="channel">The RabbitMQ channel to publish on.</param>
    /// <param name="exchange">The exchange to publish to (empty string for the default exchange).</param>
    /// <param name="topicHeaderKey">
    /// The message-property header the topic is written to. Defaults to
    /// <see cref="RabbitMqConstants.DefaultTopicHeader"/> (<c>"topic"</c>).
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<IBenzeneClientContext<T, Void>> UseRabbitMq<T>(
        this IMiddlewarePipelineBuilder<IBenzeneClientContext<T, Void>> app, IChannel channel, string exchange = "",
        string topicHeaderKey = RabbitMqConstants.DefaultTopicHeader)
    {
        return app.UseRabbitMq<T>(exchange, builder => builder.UseRabbitMqClient(channel), topicHeaderKey);
    }

    /// <summary>
    /// Converts an outbound route pipeline (<c>OutboundRoutingBuilder.Route</c>) to publish via
    /// RabbitMQ, using a custom middleware configuration - the <see cref="OutboundContext"/>
    /// counterpart of <see cref="UseRabbitMq{T}(IMiddlewarePipelineBuilder{IBenzeneClientContext{T, Void}}, string, Action{IMiddlewarePipelineBuilder{RabbitMqSendMessageContext}}, string)"/>,
    /// mirroring <c>UseSqs</c>/<c>UseServiceBus</c>/<c>UseInProcess</c>.
    /// </summary>
    /// <param name="app">The outbound pipeline builder to convert.</param>
    /// <param name="exchange">The exchange to publish to (empty string for the default exchange, where the routing key is the target queue name).</param>
    /// <param name="action">Configures the inner RabbitMQ publish pipeline.</param>
    /// <param name="topicHeaderKey">
    /// The message-property header the topic is written to. Defaults to
    /// <see cref="RabbitMqConstants.DefaultTopicHeader"/>; pass a different key to publish for a
    /// consumer that routes on another header.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// This is a shorthand over the explicit form, which you can write yourself from public API:
    /// <c>app.Convert(new OutboundRabbitMqContextConverter(exchange, topicHeaderKey), action)</c>.
    /// Drop to that when you want to supply your own <c>ISerializer</c>.
    /// </remarks>
    public static IMiddlewarePipelineBuilder<OutboundContext> UseRabbitMq(
        this IMiddlewarePipelineBuilder<OutboundContext> app, string exchange,
        Action<IMiddlewarePipelineBuilder<RabbitMqSendMessageContext>> action,
        string topicHeaderKey = RabbitMqConstants.DefaultTopicHeader)
    {
        return app.Convert(new OutboundRabbitMqContextConverter(exchange, topicHeaderKey), action);
    }

    /// <summary>
    /// Converts an outbound route pipeline (<c>OutboundRoutingBuilder.Route</c>) to publish via
    /// RabbitMQ over the given channel, using the default <see cref="RabbitMqClientMiddleware"/>
    /// configuration.
    /// </summary>
    /// <param name="app">The outbound pipeline builder to convert.</param>
    /// <param name="channel">The RabbitMQ channel to publish on.</param>
    /// <param name="exchange">The exchange to publish to (empty string, the default, for the default exchange).</param>
    /// <param name="topicHeaderKey">
    /// The message-property header the topic is written to. Defaults to
    /// <see cref="RabbitMqConstants.DefaultTopicHeader"/>.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// One rung of shorthand over
    /// <see cref="UseRabbitMq(IMiddlewarePipelineBuilder{OutboundContext}, string, Action{IMiddlewarePipelineBuilder{RabbitMqSendMessageContext}}, string)"/>:
    /// it is exactly <c>app.UseRabbitMq(exchange, builder =&gt; builder.UseRabbitMqClient(channel), topicHeaderKey)</c>.
    /// Drop one level to that when you need <c>mandatory</c>/<c>persistent</c> publish flags or extra
    /// middleware around the publish.
    /// </remarks>
    public static IMiddlewarePipelineBuilder<OutboundContext> UseRabbitMq(
        this IMiddlewarePipelineBuilder<OutboundContext> app, IChannel channel, string exchange = "",
        string topicHeaderKey = RabbitMqConstants.DefaultTopicHeader)
    {
        return app.UseRabbitMq(exchange, builder => builder.UseRabbitMqClient(channel), topicHeaderKey);
    }
}
