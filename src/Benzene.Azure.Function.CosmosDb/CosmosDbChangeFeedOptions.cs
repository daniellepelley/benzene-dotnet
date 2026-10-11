namespace Benzene.Azure.Function.CosmosDb;

/// <summary>
/// Configures how a Cosmos DB Change Feed trigger batch settles.
/// </summary>
public class CosmosDbChangeFeedOptions
{
    /// <summary>
    /// Gets or sets whether a failed batch is acknowledged anyway. Defaults to <c>false</c>: an
    /// exception from the pipeline propagates to the Functions host, the trigger leaves its lease
    /// where it was, and the whole batch is redelivered - the unit of settlement is the triggered
    /// batch (a fan-in handler sees every document at once, so there is no per-document result).
    /// Set <c>true</c> for at-most-once (the override transport-bindings.md §1, obligation 8,
    /// requires every one-way binding to offer): the exception is logged and the invocation returns
    /// normally, so the trigger checkpoints past the batch. An infrastructure (mis-wiring) failure
    /// still propagates: it is no document's outcome.
    /// </summary>
    public bool AtMostOnce { get; set; }
}
