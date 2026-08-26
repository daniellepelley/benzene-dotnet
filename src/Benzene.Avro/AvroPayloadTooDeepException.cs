using System;

namespace Benzene.Avro;

/// <summary>
/// Thrown when reading or writing an Avro payload exceeds the configured maximum nesting depth
/// (<see cref="AvroOptions.MaxDepth"/>) - e.g. a self-referencing or deeply-nested schema/object graph
/// driving unbounded recursion. Left unguarded, that recursion eventually blows the CLR call stack with
/// an <em>uncatchable</em> <see cref="StackOverflowException"/> that kills the whole process; this is
/// thrown well before that point is reached, so it can be caught like any other error.
/// </summary>
public class AvroPayloadTooDeepException : Exception
{
    /// <summary>Initializes a new instance.</summary>
    /// <param name="depth">The nesting depth reached when the guard tripped.</param>
    /// <param name="maxDepth">The maximum nesting depth the serializer/deserializer will accept.</param>
    /// <param name="direction">Which direction tripped the guard ("serializing" or "deserializing").</param>
    public AvroPayloadTooDeepException(long depth, long maxDepth, string direction)
        : base($"Avro payload exceeded the maximum nesting depth of {maxDepth} while {direction} " +
               $"(reached depth {depth}). This guards against unbounded recursion - e.g. a " +
               "self-referencing or very deeply-nested schema/object graph - driving an uncatchable " +
               "CLR stack overflow. Increase AvroOptions.MaxDepth if this is a legitimate deeply-nested " +
               "payload.")
    {
        Depth = depth;
        MaxDepth = maxDepth;
    }

    /// <summary>The nesting depth reached when the guard tripped.</summary>
    public long Depth { get; }

    /// <summary>The maximum nesting depth the serializer/deserializer will accept.</summary>
    public long MaxDepth { get; }
}
