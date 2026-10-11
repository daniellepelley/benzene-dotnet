using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.Middleware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Benzene.Core.MessageHandlers;

/// <summary>
/// The unmatched-topic settlement option every one-way binding offers (transport-bindings.md §1,
/// obligation 8).
/// </summary>
public static class UnmatchedTopicExtensions
{
    /// <summary>
    /// Acknowledges a message whose topic no handler serves - settling it <c>ignored</c> - instead of
    /// failing it <c>not-found</c>, which a one-way binding (SQS, SNS, Service Bus, Kafka, ...) does not
    /// acknowledge, so the transport redelivers it until its redrive policy dead-letters it. For a
    /// subscriber on a shared bus that sees other services' topics. Off unless called.
    /// </summary>
    /// <typeparam name="TContext">The binding's context type - the option has the same name and shape on every binding.</typeparam>
    /// <param name="app">The binding's pipeline builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// Call it immediately before <c>UseMessageHandlers</c>, after anything that sets or derives the
    /// topic (<c>UsePresetTopic</c>, <c>UseTopicFrom</c>): it judges the topic the router would route
    /// on. Only a topic id with no handler at any version is ignored; a reserved <c>benzene:</c> topic
    /// and a message with no topic are never ignored. See
    /// <see cref="UnmatchedTopicAsIgnoredMiddleware{TContext}"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// app.UseSqs(sqs => sqs
    ///     .UseUnmatchedTopicAsIgnored()
    ///     .UseMessageHandlers());
    /// </code>
    /// </example>
    public static IMiddlewarePipelineBuilder<TContext> UseUnmatchedTopicAsIgnored<TContext>(this IMiddlewarePipelineBuilder<TContext> app)
    {
        return app.Use(resolver => new UnmatchedTopicAsIgnoredMiddleware<TContext>(
            resolver.GetService<IMessageGetter<TContext>>(),
            resolver.GetService<MessageHandlerDefinitionIndex>(),
            resolver.GetService<IMessageHandlerResultSetter<TContext>>(),
            resolver.TryGetService<ILogger<UnmatchedTopicAsIgnoredMiddleware<TContext>>>()
                ?? NullLogger<UnmatchedTopicAsIgnoredMiddleware<TContext>>.Instance));
    }
}
