namespace Benzene.Outbox;

/// <summary>The tally from one <see cref="IOutboxDispatcher.RunOnceAsync"/> sweep.</summary>
public sealed class OutboxDispatchResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxDispatchResult"/> class.
    /// </summary>
    public OutboxDispatchResult(int dispatched, int rescheduled, int parked, int deletedRetired, int sentButUnsettled = 0)
    {
        Dispatched = dispatched;
        Rescheduled = rescheduled;
        Parked = parked;
        DeletedRetired = deletedRetired;
        SentButUnsettled = sentButUnsettled;
    }

    /// <summary>Gets how many envelopes were successfully dispatched this run.</summary>
    public int Dispatched { get; }

    /// <summary>Gets how many envelopes failed but were rescheduled with backoff this run.</summary>
    public int Rescheduled { get; }

    /// <summary>Gets how many envelopes reached <see cref="OutboxOptions.MaxAttempts"/> and were parked this run.</summary>
    public int Parked { get; }

    /// <summary>Gets how many retention-expired dispatched envelopes were deleted this run.</summary>
    public int DeletedRetired { get; }

    /// <summary>
    /// Gets how many envelopes were sent successfully this run but could not have their dispatched state
    /// recorded (a store settle failure surviving one retry - see <see cref="OutboxDispatchOutcome.SentButUnsettled"/>).
    /// These are left claimed for the sweeper to reclaim once their lease lapses, not rescheduled/parked.
    /// </summary>
    public int SentButUnsettled { get; }
}
