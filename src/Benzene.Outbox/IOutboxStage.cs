namespace Benzene.Outbox;

/// <summary>
/// The scoped staging seam <see cref="OutboxMiddleware"/> writes through in
/// <see cref="OutboxWriteMode.Transactional"/> mode, instead of persisting straight to
/// <see cref="IOutboxStore"/>. An outbox-aware unit of work (the application's own commit - see
/// <c>Benzene.Outbox.DynamoDb</c>/<c>Benzene.Outbox.EntityFramework</c>, Phase 2/4) drains the staged
/// envelopes and persists them together with the handler's own state write, atomically.
/// </summary>
/// <remarks>
/// Registered scoped, one instance per message - core ships <see cref="BufferedOutboxStage"/> (an
/// in-memory list). An envelope staged but never drained/committed is discarded with the scope,
/// consistent by construction: no state was written either.
/// </remarks>
public interface IOutboxStage
{
    /// <summary>
    /// Stages an envelope for this scope's eventual atomic commit. Does not persist it - see the
    /// remarks on <see cref="IOutboxStage"/>.
    /// </summary>
    /// <param name="envelope">The envelope to stage.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task StageAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default);
}
