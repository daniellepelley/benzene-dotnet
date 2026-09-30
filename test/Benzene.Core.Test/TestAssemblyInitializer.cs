using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Benzene.Test;

/// <summary>
/// Raises the thread pool's minimum before any test runs.
/// </summary>
/// <remarks>
/// The pool starts with one worker per core (4 on the CI runners) and adds more only slowly once those
/// are busy. xUnit runs this assembly's test classes in parallel, and a few dozen of them block a pool
/// thread on purpose (sync-over-async paths under test, <c>.Result</c> in assertions), so on a small
/// runner the pool was routinely starved for seconds at a time. Every test that bounds a deadline -
/// a 50ms timeout, a cancelled token - then measured the starvation instead of the mechanism: a 50ms
/// deadline observed at 38s, a 5s delay finishing before a 50ms cancellation got a thread to run on.
/// A floor well above the suite's concurrency removes that without loosening a single assertion.
/// </remarks>
internal static class TestAssemblyInitializer
{
    private const int MinimumThreads = 64;

#pragma warning disable CA2255 // A module initializer is exactly right for a test assembly's one-time process setup.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void RaiseThreadPoolMinimum()
    {
        ThreadPool.GetMinThreads(out var workerThreads, out var completionPortThreads);
        ThreadPool.SetMinThreads(Math.Max(workerThreads, MinimumThreads), Math.Max(completionPortThreads, MinimumThreads));
    }
}
