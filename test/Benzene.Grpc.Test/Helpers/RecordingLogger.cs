using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Benzene.Grpc.Test.Helpers;

/// <summary>A minimal <see cref="ILogger{T}"/> that records every call's <see cref="LogLevel"/>, for
/// asserting a code path does/does not log at a given level (round-10 #109).</summary>
public class RecordingLogger<T> : ILogger<T>
{
    public List<LogLevel> Levels { get; } = new();

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Levels.Add(logLevel);
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    private class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
