namespace Benzene.Outbox;

/// <summary>The tally from one <see cref="IOutboxDispatcher.RunOnceAsync"/> sweep.</summary>
public sealed class OutboxDispatchResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxDispatchResult"/> class.
    /// </summary>
    public OutboxDispatchResult(int dispatched, int rescheduled, int parked, int deletedRetired)
    {
        Dispatched = dispatched;
        Rescheduled = rescheduled;
        Parked = parked;
        DeletedRetired = deletedRetired;
    }

    /// <summary>Gets how many envelopes were successfully dispatched this run.</summary>
    public int Dispatched { get; }

    /// <summary>Gets how many envelopes failed but were rescheduled with backoff this run.</summary>
    public int Rescheduled { get; }

    /// <summary>Gets how many envelopes reached <see cref="OutboxOptions.MaxAttempts"/> and were parked this run.</summary>
    public int Parked { get; }

    /// <summary>Gets how many retention-expired dispatched envelopes were deleted this run.</summary>
    public int DeletedRetired { get; }
}
