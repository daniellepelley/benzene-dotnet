using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Benzene.Outbox;

/// <summary>
/// The default <see cref="IOutboxStage"/>: buffers staged envelopes in an in-memory list for the
/// scope's lifetime. An outbox-aware unit of work (see <c>Benzene.Outbox.DynamoDb</c>/
/// <c>Benzene.Outbox.EntityFramework</c>) calls <see cref="DrainStaged"/> as part of its own atomic
/// commit; core registers this type scoped via <c>AddOutbox</c>, alongside <see cref="IOutboxStage"/>
/// resolving to the very same scoped instance.
/// </summary>
public class BufferedOutboxStage : IOutboxStage, IDisposable
{
    private readonly List<OutboxEnvelope> _staged = new();
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BufferedOutboxStage"/> class.
    /// </summary>
    /// <param name="logger">
    /// The logger used to warn when the scope disposes with staged-but-undrained envelopes.
    /// Defaults to <see cref="NullLogger.Instance"/>.
    /// </param>
    public BufferedOutboxStage(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public Task StageAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _staged.Add(envelope);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns every envelope staged so far and clears the buffer. Called by the outbox-aware unit
    /// of work as part of building its single atomic write.
    /// </summary>
    public IReadOnlyList<OutboxEnvelope> DrainStaged()
    {
        if (_staged.Count == 0)
        {
            return Array.Empty<OutboxEnvelope>();
        }

        var drained = _staged.ToArray();
        _staged.Clear();
        return drained;
    }

    /// <summary>
    /// Warns if the scope disposes with staged envelopes never drained/committed - expected when a
    /// handler throws before committing (the envelopes are correctly discarded, since no state was
    /// written either); unexpected otherwise, and worth a loud signal rather than silent data loss.
    /// </summary>
    public void Dispose()
    {
        if (_staged.Count > 0)
        {
            _logger.LogWarning(
                "BufferedOutboxStage disposed with {Count} staged outbox envelope(s) never drained/committed - " +
                "they are discarded (consistent by construction: no state was written either). Expected when " +
                "the handler threw before its own commit; otherwise check that the handler actually commits " +
                "through its outbox-aware unit of work.",
                _staged.Count);
        }
    }
}
