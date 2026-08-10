namespace Benzene.Clients.InProcess;

/// <summary>
/// Thrown by <c>.UseInProcessFanOut(...)</c> when two targets in the same call name the same topic.
/// </summary>
/// <remarks>
/// Benzene's (topic, version) → at most one handler invariant is enforced <b>process-wide</b>
/// (<c>DuplicateTopicStartUpCheck</c>), not per in-process pipeline - every named pipeline
/// <see cref="InProcessMessagingBuilder"/> builds shares the same underlying handler registration,
/// the same way every transport a service exposes (HTTP, a queue consumer, ...) does today. Two
/// fan-out targets reacting to what is conceptually one event must therefore each dispatch under a
/// topic of their own (e.g. <c>"billing:order-created"</c>, <c>"shipping:order-created"</c>), not the
/// literal topic the event was published under - reusing the same topic for two targets is exactly
/// the "two handlers register for one topic" mistake <c>DuplicateTopicStartUpCheck</c> already
/// exists to catch, just discoverable earlier: at the fan-out route's own construction, rather than
/// waiting for a boot-time check that might be disabled, or for the silent misroute that happens
/// without one (the built-in <c>MessageHandlerDefinitionIndex</c> resolves an ambiguous topic to
/// whichever handler happened to win, not an error, unless something asks it to check).
/// </remarks>
public class DuplicateInProcessFanOutTargetException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DuplicateInProcessFanOutTargetException"/> class.
    /// </summary>
    /// <param name="topic">The topic named by more than one target.</param>
    public DuplicateInProcessFanOutTargetException(string topic)
        : base($"UseInProcessFanOut(...) was given more than one target for topic '{topic}'. Each " +
               "fan-out target must dispatch under a topic of its own - reusing the same topic for " +
               "two targets means two different pipelines would need a handler for the identical " +
               "topic, which Benzene's topic model does not allow process-wide.")
    {
        Topic = topic;
    }

    /// <summary>Gets the topic named by more than one target.</summary>
    public string Topic { get; }
}
