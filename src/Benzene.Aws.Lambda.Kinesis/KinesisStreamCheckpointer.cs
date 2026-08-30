using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Benzene.Core.Middleware;

namespace Benzene.Aws.Lambda.Kinesis;

/// <summary>
/// An <see cref="IStreamCheckpointer{TItem}"/> for a Kinesis batch: tracks which records a stream
/// handler has confirmed and computes the sequence number AWS should resume from if the batch didn't
/// finish - see <c>work/archive/kinesis-batch-failure-handling-design-2026-07.md</c> §3.2.
/// </summary>
/// <remarks>
/// Tracks a <b>contiguous-prefix</b> watermark, not a single monotonic max-index one (#273): the
/// resume point is the first record whose original batch position hasn't been confirmed, i.e. the
/// longest fully-confirmed prefix - never simply "the highest index confirmed so far". A single
/// monotonic max-index watermark is unsound under <c>StreamOperators.PartitionBy</c> (the pattern this
/// package's own <see cref="KinesisStreamApplication"/> class doc recommends for restoring per-key
/// ordering): a handler that partitions by key and checkpoints as it finishes each record can
/// checkpoint a later-index record from one partition's group before an earlier-index record from a
/// different partition's group has even been looked at. If that earlier record then fails, a
/// max-index watermark would already have advanced past it - silently reporting a record that never
/// succeeded as done, and AWS would never retry it (silent data loss). Tracking confirmed positions
/// individually and resuming from the first gap fixes that: the failed record is always reported,
/// never skipped. Accepted tradeoff: a record confirmed ahead of an earlier gap (like the partition
/// that finished first in the scenario above) is redelivered even though it already succeeded - safe
/// over-retry (at-least-once), not the silent-skip failure mode this fix closes. For a plain
/// sequential handler that checkpoints strictly in order (no gaps), this produces byte-identical
/// resume points to the old max-index watermark.
/// </remarks>
internal class KinesisStreamCheckpointer : IStreamCheckpointer<KinesisEventRecord>
{
    private readonly List<KinesisEventRecord> _records;
    private readonly bool[] _confirmed;

    /// <summary>
    /// Initializes a new instance of the <see cref="KinesisStreamCheckpointer"/> class.
    /// </summary>
    /// <param name="records">The batch's records, in their original order.</param>
    public KinesisStreamCheckpointer(List<KinesisEventRecord> records)
    {
        _records = records;
        _confirmed = new bool[records.Count];
    }

    /// <inheritdoc />
    public Task CheckpointAsync(KinesisEventRecord lastProcessed)
    {
        // IndexOf returns -1 for a record that isn't in the batch by reference equality (e.g. a
        // projected/transformed copy the handler passes) - ignored, exactly as before: a foreign
        // record can neither advance nor rewind the watermark. A confirmed index can never become
        // unconfirmed (there's no "unconfirm" operation), matching the old code's "only ever advance"
        // guarantee at the level of each individual record.
        var index = _records.IndexOf(lastProcessed);
        if (index >= 0)
        {
            _confirmed[index] = true;
        }

        return Task.CompletedTask;
    }

    /// <summary>Whether the handler has checkpointed at least one record.</summary>
    public bool HasCheckpointed => Array.IndexOf(_confirmed, true) >= 0;

    /// <summary>
    /// Advances the checkpoint to the last record in the batch, marking the whole batch processed.
    /// Used by <see cref="KinesisStreamOptions.AutoCheckpointOnSuccess"/> when a batch completes
    /// without the handler checkpointing anything itself.
    /// </summary>
    public void CheckpointAll() => Array.Fill(_confirmed, true);

    /// <summary>
    /// Gets the sequence number of the first record that hasn't been confirmed - the longest
    /// confirmed prefix's end, and the record AWS should resume the batch from - or <c>null</c> if
    /// every record has been confirmed (or the batch is empty).
    /// </summary>
    /// <remarks>
    /// Null-conditional through <c>Kinesis</c> deliberately: this getter runs from
    /// <c>KinesisStreamApplication</c>'s <c>resultMapper</c>, which the base
    /// <c>MiddlewareApplication</c> invokes <em>after</em> <c>CatchAndCheckpointPipeline</c>'s own
    /// try/catch has already returned - so an NRE here would not be caught by
    /// <see cref="KinesisStreamOptions.CatchExceptions"/> and would cascade unhandled, discarding
    /// whatever partial-resume point the handler had already checkpointed. A malformed record with no
    /// <c>Kinesis</c> payload must degrade to a <c>null</c> resume point instead.
    /// </remarks>
    public string? FirstUncheckpointedSequenceNumber
    {
        get
        {
            var firstGap = Array.IndexOf(_confirmed, false);
            return firstGap >= 0 ? _records[firstGap]?.Kinesis?.SequenceNumber : null;
        }
    }
}
