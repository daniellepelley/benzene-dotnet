namespace Benzene.Azure.Function.BlobStorage;

/// <summary>
/// Configures how a blob-trigger delivery settles.
/// </summary>
public class BlobStorageOptions
{
    /// <summary>
    /// Gets or sets whether a failed delivery is acknowledged anyway. Defaults to <c>false</c>: an
    /// exception from the pipeline propagates to the Functions host, which retries the blob (up to its
    /// max dequeue count) and then writes it to the <c>webjobs-blobtrigger-poison</c> queue - the
    /// one-shot shape of transport-bindings.md §1, obligation 8 (there is no result to settle on: the
    /// blob is not message-routed, so a failure is a throw). Set <c>true</c> for at-most-once (the
    /// override that obligation requires of every one-way binding): the exception is logged and the
    /// invocation returns normally, so the blob is not retried. An infrastructure (mis-wiring) failure
    /// still propagates: it is not the blob's outcome.
    /// </summary>
    public bool AtMostOnce { get; set; }
}
