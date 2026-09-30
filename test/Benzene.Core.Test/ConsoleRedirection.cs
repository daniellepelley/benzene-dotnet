using System;
using System.IO;
using Xunit;

namespace Benzene.Test;

/// <summary>
/// The test collection for every test that replaces <c>Console.Out</c> or <c>Console.Error</c>. That
/// covers tests capturing CLI output, and tests that construct a real <c>Amazon.Lambda.RuntimeSupport</c>
/// <c>LambdaBootstrap</c>, directly or through <c>AwsLambdaBootstrap</c> / <c>BenzeneLambdaServer</c>.
/// </summary>
/// <remarks>
/// <para>
/// The console is process-global state, and xUnit runs test classes in parallel. Two failure modes came
/// out of that.
/// </para>
/// <para>
/// First, capture-then-restore races. One test restores the writer it saved, while that writer is
/// another test's capture.
/// </para>
/// <para>
/// Second, a hang. Constructing a <c>LambdaBootstrap</c> wraps whatever <c>Console.Out</c> currently is
/// in a log-level writer and never puts the original back. In a Lambda that happens once per process,
/// but here it happened once per such test, leaving a chain of nested synchronised writers. On Unix, a
/// console stream's flush takes <c>lock (Console.Out)</c>, the OUTERMOST writer, while holding the lock
/// of the inner writer it belongs to. A test calling <c>Console.WriteLine</c> takes the same two locks
/// outermost first. A background console logger flushing at that moment deadlocked against it and hung
/// the test host, roughly one full run in two on a 4-core machine.
/// </para>
/// <para>
/// Tests in this collection never run alongside anything else, and each one leaves the console as it
/// found it: the Lambda ones via <see cref="ConsoleRedirection"/>, the capturing ones via their own
/// <c>finally</c>.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleRedirectionCollection
{
    public const string Name = "Console redirection";
}

/// <summary>
/// Snapshots <c>Console.Out</c>/<c>Console.Error</c> when a test class is constructed, and restores them
/// when it is disposed. It is for tests whose code under test redirects the console and never restores
/// it. See <see cref="ConsoleRedirectionCollection"/>.
/// </summary>
public abstract class ConsoleRedirection : IDisposable
{
    private readonly TextWriter _out = Console.Out;
    private readonly TextWriter _error = Console.Error;

    public void Dispose()
    {
        Console.SetOut(_out);
        Console.SetError(_error);
        GC.SuppressFinalize(this);
    }
}
