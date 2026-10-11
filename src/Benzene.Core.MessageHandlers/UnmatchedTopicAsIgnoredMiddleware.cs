using Benzene.Abstractions;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.Middleware;
using Benzene.Results;
using Microsoft.Extensions.Logging;

namespace Benzene.Core.MessageHandlers;

/// <summary>
/// Settles a message whose topic no handler serves as <c>ignored</c> - a success, so a one-way binding
/// acknowledges it - instead of letting it reach the router's <c>not-found</c> failure, which a one-way
/// binding does not acknowledge (transport-bindings.md §1, obligation 8). Added via
/// <see cref="UnmatchedTopicExtensions.UseUnmatchedTopicAsIgnored{TContext}"/>, for a subscriber on a
/// shared bus that sees other services' topics.
/// </summary>
/// <typeparam name="TContext">The transport-specific context type.</typeparam>
/// <remarks>
/// <para>
/// "Unmatched" means no handler is registered for the topic <b>id</b> at all, whatever version the
/// message names: a topic this service serves at some version is its own, so a version it does not
/// serve stays a <c>not-found</c> failure worth redelivering and surfacing, not a message to drop.
/// </para>
/// <para>
/// A reserved topic (<see cref="BenzeneTopic.Prefix"/>) is never ignored here: it is served by
/// framework middleware wherever the pipeline places it, and an unanswered one - an unserved
/// <c>benzene:ping</c> probe, say - is a <c>not-found</c> the operator should see (wire-contracts.md §7).
/// A message with no topic at all is not "unmatched" either - it is left to the router's own
/// missing-topic failure. Ends the pipeline with a result set, as obligation 8 requires of any
/// middleware that ends it.
/// </para>
/// </remarks>
public class UnmatchedTopicAsIgnoredMiddleware<TContext> : IMiddleware<TContext>
{
    private readonly IMessageGetter<TContext> _messageGetter;
    private readonly MessageHandlerDefinitionIndex _handlers;
    private readonly IMessageHandlerResultSetter<TContext> _resultSetter;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnmatchedTopicAsIgnoredMiddleware{TContext}"/> class.
    /// </summary>
    /// <param name="messageGetter">Resolves the message's topic, exactly as the router does.</param>
    /// <param name="handlers">The registered handler definitions, indexed by topic id.</param>
    /// <param name="resultSetter">Records the <c>ignored</c> result on the context.</param>
    /// <param name="logger">Logs each ignored message's topic.</param>
    public UnmatchedTopicAsIgnoredMiddleware(
        IMessageGetter<TContext> messageGetter,
        MessageHandlerDefinitionIndex handlers,
        IMessageHandlerResultSetter<TContext> resultSetter,
        ILogger<UnmatchedTopicAsIgnoredMiddleware<TContext>> logger)
    {
        _messageGetter = messageGetter;
        _handlers = handlers;
        _resultSetter = resultSetter;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "UnmatchedTopicAsIgnored";

    /// <inheritdoc />
    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        var topic = _messageGetter.GetTopic(context);
        var topicId = topic?.Id;

        if (string.IsNullOrEmpty(topicId)
            || topicId == Benzene.Core.Messages.Constants.Missing.Id
            || topicId.StartsWith(BenzeneTopic.Prefix, StringComparison.Ordinal)
            || _handlers.GetByTopicId(topicId).Length > 0)
        {
            await next();
            return;
        }

        _logger.LogInformation(
            "No handler serves topic {topic}; acknowledged as ignored (UseUnmatchedTopicAsIgnored)", topicId);
        await _resultSetter.SetResultAsync(context,
            new MessageHandlerResult(topic, MessageHandlerDefinition.Empty(), BenzeneResult.Ignored()));
    }
}
