using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Benzene.Examples.Versioning.Services;

/// <summary>
/// A tiny in-memory record of what each handler actually received, so a test driving a fire-and-forget
/// transport (SQS/SNS) - which returns no response body - can still assert which version handler ran, or
/// what shape the casting pipeline delivered. The real deployable would write to a data store instead;
/// this is the example's stand-in, mirroring <c>InMemoryOrderDbClient</c> in the AWS example.
/// </summary>
public interface IProcessedLog
{
    void Record(string entry);
    IReadOnlyList<string> Entries { get; }
}

public class InMemoryProcessedLog : IProcessedLog
{
    // Static so the single instance survives across the per-invocation DI scopes a Lambda host creates,
    // and so a test can read it after a fire-and-forget send. Concurrent because the thread-safety test
    // drives several transports at once.
    private static readonly ConcurrentQueue<string> Log = new();

    public void Record(string entry) => Log.Enqueue(entry);

    public IReadOnlyList<string> Entries => Log.ToArray();

    public static void Clear() => Log.Clear();
}
