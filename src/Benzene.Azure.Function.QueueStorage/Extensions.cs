using System.Threading;
using Benzene.Abstractions.Middleware;
using Benzene.Azure.Function.Core;
using Benzene.Core.MessageHandlers.BenzeneMessage;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;

namespace Benzene.Azure.Function.QueueStorage;

/// <summary>
/// Provides extension methods for adding direct-message handling to a Queue Storage middleware
/// pipeline, and for dispatching Queue Storage messages to a built <see cref="IAzureFunctionApp"/>.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Adds direct Benzene message handling to the pipeline, configuring the inner message pipeline inline.
    /// </summary>
    /// <param name="app">The Queue Storage pipeline builder to add message handling to.</param>
    /// <param name="action">The action that configures the inner direct-message pipeline.</param>
    /// <returns>The pipeline builder, for method chaining.</returns>
    public static IMiddlewarePipelineBuilder<QueueStorageContext> UseBenzeneMessage(this IMiddlewarePipelineBuilder<QueueStorageContext> app, Action<IMiddlewarePipelineBuilder<BenzeneMessageContext>> action)
    {
        app.Register(x => x.AddBenzeneMessage());
        var middlewarePipelineBuilder = app.Create<BenzeneMessageContext>();
        // Queue Storage has no reply channel - the response is discarded, so don't serialize it.
        middlewarePipelineBuilder.SuppressResponse();
        action(middlewarePipelineBuilder);
        var pipeline = middlewarePipelineBuilder.Build();
        return app.Use(resolver => new BenzeneMessageQueueStorageHandler(pipeline, resolver));
    }

    /// <summary>
    /// Adds direct Benzene message handling to the pipeline, using an already-built inner message pipeline.
    /// </summary>
    /// <param name="app">The Queue Storage pipeline builder to add message handling to.</param>
    /// <param name="builder">The already-configured inner direct-message pipeline builder.</param>
    /// <returns>The pipeline builder, for method chaining.</returns>
    public static IMiddlewarePipelineBuilder<QueueStorageContext> UseBenzeneMessage(this IMiddlewarePipelineBuilder<QueueStorageContext> app, IMiddlewarePipelineBuilder<BenzeneMessageContext> builder)
    {
        var pipeline = builder.Build();
        return app.Use(resolver => new BenzeneMessageQueueStorageHandler(pipeline, resolver));
    }

    /// <summary>
    /// Dispatches Queue Storage messages to the Azure Function app's Queue Storage entry point
    /// application. The trigger delivers one message per invocation; the <c>params</c> shape exists
    /// for tests and callers with several in hand.
    /// </summary>
    /// <param name="source">The built Azure Function app to dispatch to.</param>
    /// <param name="messages">The Queue Storage messages to handle.</param>
    /// <returns>A task that completes when the messages have been handled.</returns>
    public static Task HandleQueueMessages(this IAzureFunctionApp source, params QueueStorageMessage[] messages)
    {
        return source.HandleAsync(messages);
    }

    /// <summary>
    /// Dispatches Queue Storage messages to the Azure Function app's Queue Storage entry point
    /// application, forwarding <paramref name="cancellationToken"/> so any component resolved during
    /// the pipeline can observe it via <c>ICancellationTokenAccessor</c>. A leading (rather than
    /// optional trailing) parameter - a <c>params</c> array must be last, so the token can't default
    /// after it; bind the isolated worker's <see cref="CancellationToken"/> trigger method parameter
    /// and pass it here.
    /// </summary>
    /// <param name="source">The built Azure Function app to dispatch to.</param>
    /// <param name="cancellationToken">The isolated worker's cancellation token for this invocation.</param>
    /// <param name="messages">The Queue Storage messages to handle.</param>
    /// <returns>A task that completes when the messages have been handled.</returns>
    public static Task HandleQueueMessages(this IAzureFunctionApp source, CancellationToken cancellationToken, params QueueStorageMessage[] messages)
    {
        return source.HandleAsync(messages, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Dispatches Queue Storage messages to the <paramref name="name"/>-keyed Queue Storage entry
    /// point - use when more than one <c>[QueueTrigger]</c> function is registered (each via
    /// <c>UseQueueStorage(..., name: "queue")</c>). The <c>[QueueTrigger("queue")]</c> method passes
    /// its own queue name here.
    /// </summary>
    /// <param name="source">The built Azure Function app to dispatch to.</param>
    /// <param name="name">The discriminator name matching the registered <c>UseQueueStorage(..., name)</c>.</param>
    /// <param name="messages">The Queue Storage messages to handle.</param>
    /// <returns>A task that completes when the messages have been handled.</returns>
    public static Task HandleQueueMessages(this IAzureFunctionApp source, string name, params QueueStorageMessage[] messages)
    {
        return source.HandleAsync(messages, name);
    }

    /// <summary>
    /// Dispatches Queue Storage messages to the <paramref name="name"/>-keyed entry point, forwarding
    /// <paramref name="cancellationToken"/> so any component resolved during the pipeline can observe
    /// it via <c>ICancellationTokenAccessor</c>.
    /// </summary>
    /// <param name="source">The built Azure Function app to dispatch to.</param>
    /// <param name="name">The discriminator name matching the registered <c>UseQueueStorage(..., name)</c>.</param>
    /// <param name="cancellationToken">The isolated worker's cancellation token for this invocation.</param>
    /// <param name="messages">The Queue Storage messages to handle.</param>
    /// <returns>A task that completes when the messages have been handled.</returns>
    public static Task HandleQueueMessages(this IAzureFunctionApp source, string name, CancellationToken cancellationToken, params QueueStorageMessage[] messages)
    {
        return source.HandleAsync(messages, name, cancellationToken);
    }

    /// <summary>
    /// Dispatches a single Queue Storage message, bound as its message text - the common
    /// <c>[QueueTrigger] string</c> binding - to the Azure Function app's Queue Storage entry point
    /// application.
    /// </summary>
    /// <param name="source">The built Azure Function app to dispatch to.</param>
    /// <param name="messageText">The queue message's text.</param>
    /// <param name="cancellationToken">
    /// The isolated worker's cancellation token for this invocation, forwarded so any component
    /// resolved during the pipeline can observe it via <c>ICancellationTokenAccessor</c>. Defaults to
    /// <see cref="CancellationToken.None"/> if the trigger doesn't bind one.
    /// </param>
    /// <returns>A task that completes when the message has been handled.</returns>
    public static Task HandleQueueMessage(this IAzureFunctionApp source, string messageText, CancellationToken cancellationToken = default)
    {
        return source.HandleQueueMessages(cancellationToken, new QueueStorageMessage(messageText));
    }

    /// <summary>
    /// Dispatches a single Queue Storage message to the <paramref name="name"/>-keyed entry point -
    /// use when more than one <c>[QueueTrigger]</c> function is registered.
    /// </summary>
    /// <param name="source">The built Azure Function app to dispatch to.</param>
    /// <param name="name">The discriminator name matching the registered <c>UseQueueStorage(..., name)</c>.</param>
    /// <param name="messageText">The queue message's text.</param>
    /// <param name="cancellationToken">
    /// The isolated worker's cancellation token for this invocation, forwarded so any component
    /// resolved during the pipeline can observe it via <c>ICancellationTokenAccessor</c>. Defaults to
    /// <see cref="CancellationToken.None"/> if the trigger doesn't bind one.
    /// </param>
    /// <returns>A task that completes when the message has been handled.</returns>
    public static Task HandleQueueMessage(this IAzureFunctionApp source, string name, string messageText, CancellationToken cancellationToken = default)
    {
        return source.HandleQueueMessages(name, cancellationToken, new QueueStorageMessage(messageText));
    }
}
