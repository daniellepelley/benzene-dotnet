namespace Benzene.ClaimCheck;

/// <summary>
/// Context passed to <see cref="IClaimCheckStore.PutAsync"/> describing the message being offloaded.
/// A class, not a record or struct, so fields can be added additively without a breaking API change
/// as store implementations grow (e.g. a content-type hint for the S3/Blob stores).
/// </summary>
public class ClaimCheckPutContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClaimCheckPutContext"/> class.
    /// </summary>
    /// <param name="topic">The topic the offloaded message is being sent on. Stores use it for key
    /// partitioning (e.g. an S3 key prefix per topic); it plays no role in reference resolution.</param>
    public ClaimCheckPutContext(string topic)
    {
        Topic = topic;
    }

    /// <summary>Gets the topic the offloaded message is being sent on.</summary>
    public string Topic { get; }
}
