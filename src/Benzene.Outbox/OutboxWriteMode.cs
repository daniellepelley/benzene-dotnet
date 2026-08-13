namespace Benzene.Outbox;

/// <summary>
/// How <see cref="OutboxMiddleware"/> captures an envelope. See the "Two write modes" honesty note
/// in <c>Benzene.Outbox/CLAUDE.md</c> - neither mode is exactly-once; both exist to make an
/// explicit, documented trade-off rather than a silent one.
/// </summary>
public enum OutboxWriteMode
{
    /// <summary>
    /// Store-and-forward (the default). Capture writes the envelope straight to
    /// <see cref="IOutboxStore.AddAsync"/>. The send survives process death and transport outages and
    /// is retried with backoff - but it is a second, independent write, not atomic with whatever state
    /// write the handler also performs.
    /// </summary>
    Immediate,

    /// <summary>
    /// Capture stages the envelope into the scoped <see cref="IOutboxStage"/> instead of writing it
    /// immediately. The application's own commit (an outbox-aware unit of work - see
    /// <c>Benzene.Outbox.DynamoDb</c>/<c>Benzene.Outbox.EntityFramework</c>, Phase 2/4) persists the
    /// staged envelope together with the handler's state write in one atomic operation. An envelope
    /// staged but never committed is discarded with the scope - consistent by construction, since no
    /// state was written either.
    /// </summary>
    Transactional
}
