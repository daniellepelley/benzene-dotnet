using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Benzene.Examples.Aws.Minimal;

/// <summary>
/// A tiny in-memory record of what the handler received. A real service would write to a data store;
/// this stand-in lets a test that drives a fire-and-forget source (SNS/SQS/EventBridge) - which returns
/// no response body - still assert the handler ran. Mirrors the Versioning example's <c>IProcessedLog</c>.
/// </summary>
public interface IProcessedLog
{
    void Record(string entry);
    IReadOnlyList<string> Entries { get; }
}

public class InMemoryProcessedLog : IProcessedLog
{
    // Static so the single record survives the per-invocation DI scope a Lambda host creates, and so a
    // test can read it after a fire-and-forget send. Concurrent so parallel sends are safe.
    private static readonly ConcurrentQueue<string> Log = new();

    public void Record(string entry) => Log.Enqueue(entry);

    public IReadOnlyList<string> Entries => Log.ToArray();

    public static void Clear() => Log.Clear();
}
