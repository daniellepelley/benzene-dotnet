using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.MessageHandlers.Request;
using Benzene.Abstractions.Middleware;
using Benzene.Results;
using Microsoft.Extensions.Logging;

namespace Benzene.Core.MessageHandlers;

/// <summary>
/// The pipeline entry point for message-handler dispatch: extracts the topic from the incoming
/// context, looks up and creates the matching handler, invokes it, and hands the resulting
/// <see cref="IMessageHandlerResult"/> to the registered <see cref="IMessageHandlerResultSetter{TContext}"/>.
/// Registered as middleware via the <c>UseMessageHandlers</c> extension methods on <see cref="MiddlewarePipelineExtensions"/>.
/// </summary>
/// <typeparam name="TContext">The transport-specific pipeline context type.</typeparam>
/// <remarks>
/// If the topic is missing, no matching handler definition is found, or the factory can't create a
/// handler instance, the router short-circuits with an appropriate error result (validation error or
/// not-found, per <see cref="IDefaultStatuses"/>) instead of calling <c>next</c> in
/// <see cref="HandleAsync"/> — in all of these cases <c>next</c> is never invoked, so this middleware
/// is always the terminal step for message-handler dispatch.
/// </remarks>
public class MessageRouter<TContext> : IMiddleware<TContext>, ITerminalMiddleware
{
    private readonly ILogger<MessageRouter<TContext>> _logger;
    private readonly IMessageHandlerFactory _messageHandlerFactory;
    private readonly IMessageHandlerDefinitionLookUp _messageHandlerDefinitionLookUp;
    private readonly IMessageGetter<TContext> _messageGetter;
    private readonly IRequestMapper<TContext> _requestMapper;
    private readonly IDefaultStatuses _defaultStatuses;
    private readonly IMessageHandlerResultSetter<TContext> _messageHandlerResultSetter;

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageRouter{TContext}"/> class.
    /// </summary>
    /// <param name="messageHandlerFactory">Creates the invocable handler for a resolved definition.</param>
    /// <param name="messageGetter">
    /// Extracts the topic (and other message data) from the context. <see cref="IMessageGetter{TContext}.GetTopic"/>
    /// already returns the version-joined <see cref="ITopic"/> used for handler-version dispatch
    /// (docs/specification/versioning.md §2.3) - the join used to happen here, locally, via the shared
    /// <c>GetVersionedTopic</c> helper and a separately-injected <c>IMessageVersionGetter&lt;TContext&gt;</c>;
    /// it now happens once, in the getter itself, so every other reader of the same topic (mesh trace,
    /// health checks, cloud-service dispatch, ...) sees the same version-resolved answer this router
    /// routes on (task #98, work/archive/bug-fix-designs-round10-2026-08.md WP-V).
    /// </param>
    /// <param name="messageHandlerDefinitionLookUpUp">Resolves the handler definition registered for a topic.</param>
    /// <param name="requestMapper">Maps the context into the handler's request type.</param>
    /// <param name="messageHandlerResultSetter">Writes the outcome of dispatch back onto the context.</param>
    /// <param name="defaultStatuses">Supplies the status codes used for routing failures (missing topic, no handler found).</param>
    /// <param name="logger">Logger used to record routing decisions and failures.</param>
    public MessageRouter(IMessageHandlerFactory messageHandlerFactory,
        IMessageGetter<TContext> messageGetter,
        IMessageHandlerDefinitionLookUp messageHandlerDefinitionLookUpUp,
        IRequestMapper<TContext> requestMapper,
        IMessageHandlerResultSetter<TContext> messageHandlerResultSetter,
        IDefaultStatuses defaultStatuses,
        ILogger<MessageRouter<TContext>> logger)
    {
        _messageHandlerResultSetter = messageHandlerResultSetter;
        _defaultStatuses = defaultStatuses;
        _requestMapper = requestMapper;
        _messageHandlerDefinitionLookUp = messageHandlerDefinitionLookUpUp;
        _logger = logger;
        _messageGetter = messageGetter;
        _messageHandlerFactory = messageHandlerFactory;
    }

    /// <inheritdoc />
    public string Name => "MessageRouter";

    /// <summary>
    /// Extracts the topic from <paramref name="context"/>, resolves and invokes the matching handler,
    /// and writes the result back via the registered <see cref="IMessageHandlerResultSetter{TContext}"/>.
    /// </summary>
    /// <param name="context">The current pipeline context.</param>
    /// <param name="next">Unused - this middleware never calls the rest of the pipeline (see remarks).</param>
    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        // Version-augmentation (combining the topic with the message's own version signal before
        // FindHandler) now happens inside IMessageGetter<TContext>.GetTopic itself (task #98,
        // work/archive/bug-fix-designs-round10-2026-08.md WP-V) - this used to be the one place that called the
        // shared GetVersionedTopic helper (WP-P, work/archive/bug-fix-designs-round7-10-2026-08.md) and
        // threw the joined result away instead of caching it, leaving every other reader of the topic
        // (mesh trace, health checks, cloud-service dispatch, ...) to see a version-blind topic. Simply
        // consuming the getter's own answer here means this router and every other consumer agree.
        var topic = _messageGetter.GetTopic(context);

        if (string.IsNullOrEmpty(topic?.Id))
        {
            // Name the remedy: a newcomer whose producer isn't a Benzene client (never sets the topic
            // attribute/header) otherwise has nothing actionable to go on.
            const string topicMissing = "Topic is missing - no topic could be resolved from the message. " +
                "Set the transport's topic attribute/header on the producer, or configure UsePresetTopic(...) " +
                "for this pipeline to route every message to a fixed topic.";
            _logger.LogWarning(topicMissing);
            await _messageHandlerResultSetter.SetResultAsync(context, new MessageHandlerResult(topic, MessageHandlerDefinition.Empty(), BenzeneResult.Set( _defaultStatuses.ValidationError, topicMissing)));
            return;
        }

        _logger.LogDebug("Finding message handler for {topic}", topic.Id);
        var messageHandlerDefinition = _messageHandlerDefinitionLookUp.FindHandler(topic);
        if (messageHandlerDefinition == null)
        {
            // Most built-in topic getters convert an unresolvable topic into the "<missing>" sentinel
            // (Topic's constructor does it), so the null-topic branch above never fires for them and
            // THIS branch is where a wrong-attribute producer actually lands. That is NOT universal,
            // though (task #98, work/archive/bug-fix-designs-round10-2026-08.md WP-V, correcting an earlier
            // version of this comment): EventGridMessageTopicGetter, QueueStorageMessageTopicGetter and
            // TimerMessageMappers return a null ITopic instead, which the null-topic branch above DOES
            // catch (reported as ValidationError there, rather than NotFound here) - a recorded, not
            // yet resolved, cross-transport asymmetry (see the missing-topic status asymmetry
            // [DECISION] in work/outstanding-bugs.md), not something this comment should paper over.
            // Name the remedy here too - "No handler found for topic '<missing>'" on its own gives a
            // newcomer nothing actionable to go on.
            var detail = topic.Id == Benzene.Core.Messages.Constants.Missing.Id
                ? "No topic could be resolved from the message. On HTTP this means no route matched; " +
                  "on a queue/stream transport, set the transport's topic attribute/header on the " +
                  "producer (check the configured topic key matches the one the producer sends), or " +
                  "configure UsePresetTopic(...) to route every message on this pipeline to a fixed topic."
                // Single-quote the topic id on the wire (this detail is serialized into the HTTP
                // body): quotes delimit the value legibly and, unlike the internal "<missing>"
                // sentinel's angle brackets, don't rely on the sentinel spelling being wire-friendly.
                : $"No handler found for topic '{topic.Id}'";
            _logger.LogWarning("No handler found for topic {topic}. {detail}", topic.Id, detail);
            await _messageHandlerResultSetter.SetResultAsync(context, new MessageHandlerResult(topic, MessageHandlerDefinition.Empty(), BenzeneResult.Set(_defaultStatuses.NotFound, detail)));
            return;
        }

        var handler = _messageHandlerFactory.Create(messageHandlerDefinition);
        if (handler == null)
        {
            // A definition WAS found and its handler type resolved from DI, but the instance
            // implements neither IMessageHandler<TRequest,TResponse> nor IMessageHandler<TRequest> for
            // the definition's declared request/response types - a wiring/signature bug, not a routing
            // miss. Report it as such (naming the type and expected interface) so the developer fixes
            // the handler rather than hunting for a missing [Message] registration that isn't the cause.
            var mismatch = $"Handler {messageHandlerDefinition.HandlerType.Name} for topic {topic.Id} does not implement " +
                $"IMessageHandler<{messageHandlerDefinition.RequestType.Name}, {messageHandlerDefinition.ResponseType.Name}> " +
                $"(nor IMessageHandler<{messageHandlerDefinition.RequestType.Name}>) - check the handler's declared request/response types.";
            _logger.LogWarning(mismatch);
            await _messageHandlerResultSetter.SetResultAsync(context, new MessageHandlerResult(topic, messageHandlerDefinition, BenzeneResult.UnexpectedError(mismatch)));
            return;
        }

        _logger.LogDebug("Handler mapped to topic");

        var result = await handler.HandleAsync(new DeferredRequestMapper<TContext>(_requestMapper, context));

        // A baseline failure signal even when no logging middleware is wired: an unsuccessful handler
        // result (BadRequest/NotFound/UnexpectedError/...) is otherwise invisible in logs - the router
        // only logged routing failures, and UseLogResult logs every result at Information. Warn once,
        // with the topic, status, and any error messages, so "show me the errors" surfaces it.
        if (!result.IsSuccessful)
        {
            _logger.LogWarning("Handler {handler} for topic {topic} returned unsuccessful status {status}{errors}",
                messageHandlerDefinition.HandlerType.Name, topic.Id, result.Status,
                result.Errors.Count > 0 ? " - " + string.Join("; ", result.Errors) : string.Empty);
        }

        await _messageHandlerResultSetter.SetResultAsync(context, new MessageHandlerResult(topic, messageHandlerDefinition, result));
    }
}
