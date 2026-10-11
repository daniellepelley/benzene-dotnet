namespace Benzene.Aws.Lambda.DynamoDb;

/// <summary>
/// Configures how <see cref="DynamoDbApplication"/> settles a DynamoDB Streams batch.
/// </summary>
public class DynamoDbOptions
{
    /// <summary>
    /// Gets or sets whether every record is acknowledged whatever its outcome. Defaults to
    /// <c>false</c>: records run in shard order and the batch stops at the first failed record (a
    /// failure result, no result, or a thrown exception), reporting its sequence number so Lambda
    /// checkpoints there and redelivers it and every record after it - the unit of settlement is
    /// "the first failure and everything behind it in the batch". Set <c>true</c> for at-most-once
    /// (the override transport-bindings.md §1, obligation 8, requires every one-way binding to offer):
    /// a failed record is logged and skipped, processing continues in order, and the batch is
    /// reported fully processed. An infrastructure (mis-wiring) failure still stops the batch: it is
    /// not a record's outcome.
    /// </summary>
    public bool AtMostOnce { get; set; }
}
