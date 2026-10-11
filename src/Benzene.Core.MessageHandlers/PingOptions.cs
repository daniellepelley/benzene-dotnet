using Benzene.Abstractions;
using Benzene.Abstractions.DI;

namespace Benzene.Core.MessageHandlers;

/// <summary>
/// Whether the message router answers the reserved <see cref="BenzeneTopic.Ping"/> topic itself
/// (wire-contracts §7): a reachability probe, answered <c>ok</c> with no payload, by topic id alone and
/// ignoring version - so it never reaches version selection or a handler.
/// </summary>
/// <remarks>
/// On by default: a service that consumes a one-way transport should serve it, because an unanswered
/// probe is a <c>not-found</c> failure the transport redelivers until it dead-letters it - the queue and
/// function health checks (<c>SqsHealthCheck</c>, <c>AwsLambdaHealthCheck</c>, the envelope-over-HTTP
/// check) send exactly this topic. Opt out with <see cref="PingOptionsExtensions.DisablePing"/>, e.g. to
/// serve the topic from a handler of your own.
/// </remarks>
public sealed class PingOptions
{
    /// <summary>Whether the router answers <see cref="BenzeneTopic.Ping"/>. Defaults to <c>true</c>.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>Registration helpers for <see cref="PingOptions"/>.</summary>
public static class PingOptionsExtensions
{
    /// <summary>
    /// Stops the message router answering <see cref="BenzeneTopic.Ping"/> itself; a message to it is then
    /// routed like any other topic (to a handler registered for it, else <c>not-found</c>).
    /// </summary>
    /// <param name="services">The service container to register into.</param>
    /// <returns>The same container, for chaining.</returns>
    public static IBenzeneServiceContainer DisablePing(this IBenzeneServiceContainer services)
    {
        services.AddSingleton(_ => new PingOptions { Enabled = false });
        return services;
    }
}
