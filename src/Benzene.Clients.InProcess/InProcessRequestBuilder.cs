using Benzene.Abstractions.Serialization;
using Benzene.Clients;
using Benzene.Core.Messages.BenzeneMessage;

namespace Benzene.Clients.InProcess;

/// <summary>
/// Builds the <see cref="BenzeneMessageRequest"/> an <see cref="OutboundContext"/> becomes for
/// in-process dispatch - shared by <see cref="InProcessContextConverter"/> (single-target
/// <c>.UseInProcess(name)</c>) and <see cref="InProcessFanOutClientMiddleware"/> (multi-target
/// <c>.UseInProcessFanOut(targets)</c>), so both build the exact same request shape from the same
/// outbound context.
/// </summary>
internal static class InProcessRequestBuilder
{
    /// <summary>
    /// Serializes <paramref name="context"/>'s request into a <see cref="BenzeneMessageRequest"/> for
    /// <paramref name="context"/>'s own topic, carrying headers across unchanged.
    /// </summary>
    /// <param name="context">The outbound context to convert.</param>
    /// <param name="serializer">The serializer used for the request body.</param>
    /// <returns>The built request.</returns>
    public static IBenzeneMessageRequest Build(OutboundContext context, ISerializer serializer) =>
        Build(context, serializer, context.Topic);

    /// <summary>
    /// Serializes <paramref name="context"/>'s request into a <see cref="BenzeneMessageRequest"/> for
    /// an explicit <paramref name="topic"/> - used by fan-out, where each target pipeline dispatches
    /// under its <em>own</em> topic (see <see cref="InProcessFanOutClientMiddleware"/> for why the
    /// topic can't simply be <paramref name="context"/>'s own: Benzene's (topic, version) → at most
    /// one handler invariant is process-wide, so two different fan-out targets reacting to what is
    /// conceptually one event must each register their handler under a topic of their own).
    /// </summary>
    /// <param name="context">The outbound context to convert.</param>
    /// <param name="serializer">The serializer used for the request body.</param>
    /// <param name="topic">The topic to dispatch under.</param>
    /// <returns>The built request.</returns>
    public static IBenzeneMessageRequest Build(OutboundContext context, ISerializer serializer, string topic) =>
        new BenzeneMessageRequest
        {
            Topic = topic,
            Headers = context.Headers,
            Body = serializer.Serialize(context.Request)
        };
}
