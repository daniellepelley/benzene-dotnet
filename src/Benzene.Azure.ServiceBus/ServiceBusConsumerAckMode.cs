namespace Benzene.Azure.ServiceBus;

/// <summary>
/// Configures how <see cref="BenzeneServiceBusWorker"/> settles (completes/abandons) each message
/// after its handler runs.
/// </summary>
public enum ServiceBusConsumerAckMode
{
    /// <summary>
    /// The underlying <c>ServiceBusProcessor</c>'s own auto-complete behavior applies:
    /// a message is completed once its handler returns without throwing, and abandoned (left for
    /// redelivery, subject to the entity's lock duration, max delivery count, and dead-letter
    /// settings) when the handler throws. A handler that reports a non-exception failure result
    /// still completes in this mode - only a thrown exception triggers abandonment - matching
    /// <c>Benzene.Azure.Function.ServiceBus</c>'s <c>ServiceBusAckMode.AutoComplete</c>. Opt into
    /// this only if a returned failure result should NOT keep the message; the safer
    /// <see cref="Explicit"/> is the default.
    /// </summary>
    AutoComplete = 0,

    /// <summary>
    /// The default. Benzene settles each message itself from the handler's outcome: completed after a
    /// successful outcome, abandoned after a failed one - either a thrown exception or a non-exception
    /// failure result (<c>IBenzeneResult.IsSuccessful == false</c>). Matching
    /// <c>Benzene.Azure.Function.ServiceBus</c>'s <c>ServiceBusAckMode.Explicit</c>, except no
    /// trigger configuration is needed here - the worker owns the processor, so it turns the
    /// processor's auto-complete off itself.
    /// </summary>
    Explicit = 1,

    /// <summary>
    /// At-most-once: Benzene completes every message whatever its outcome - a failure result, no
    /// result, or a thrown exception (logged) - so nothing is redelivered or dead-lettered by the
    /// entity's max delivery count. A settlement the handler requested explicitly
    /// (<see cref="ServiceBusSettlementHolder"/>) still wins. The at-most-once override
    /// transport-bindings.md §1 (obligation 8) requires every one-way binding to offer; use it only for
    /// loss-tolerant workloads. Unlike <see cref="AutoComplete"/>, a thrown exception does not abandon
    /// the message; an infrastructure (mis-wiring) failure still does - it is not a message outcome.
    /// </summary>
    AtMostOnce = 2,
}
