namespace Benzene.Kafka.Core;

/// <summary>
/// Thrown by <see cref="BenzeneKafkaWorker{TKey, TValue}"/> when
/// <see cref="BenzeneKafkaConfig.RaiseOnFailureStatus"/> is enabled and a message handler reported an
/// unsuccessful result without itself throwing - escalating the failure onto the same settlement path
/// as an unhandled exception, so the record is dead-lettered (or its offset withheld) rather than
/// silently committed.
/// </summary>
public class KafkaMessageProcessingException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="KafkaMessageProcessingException"/> class.
    /// </summary>
    /// <param name="topic">The Kafka topic the failing record was on.</param>
    /// <param name="partition">The partition the failing record was on.</param>
    /// <param name="offset">The offset of the failing record.</param>
    /// <param name="status">The status the handler's result reported, if any.</param>
    public KafkaMessageProcessingException(string topic, int partition, long offset, string? status = null)
        : base($"Message handler reported an unsuccessful result{(status == null ? "" : $" ('{status}')")} " +
               $"for the Kafka record at {topic}[{partition}]@{offset}.")
    {
        Topic = topic;
        Partition = partition;
        Offset = offset;
        Status = status;
    }

    /// <summary>Gets the Kafka topic the failing record was on.</summary>
    public string Topic { get; }

    /// <summary>Gets the partition the failing record was on.</summary>
    public int Partition { get; }

    /// <summary>Gets the offset of the failing record.</summary>
    public long Offset { get; }

    /// <summary>Gets the status the handler's result reported, if any.</summary>
    public string? Status { get; }
}
