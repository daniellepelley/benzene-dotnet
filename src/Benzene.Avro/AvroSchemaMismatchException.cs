using System;

namespace Benzene.Avro;

/// <summary>
/// Thrown when an Avro binary body does not match the shape <see cref="AvroSerializer"/> resolves for
/// the target CLR type on deserialize. <c>Benzene.Avro</c> has no schema-evolution support (see
/// <c>Benzene.Avro/CLAUDE.md</c>): the reader and writer schema are the same resolved schema, with no
/// writer-schema negotiation, so a field added, removed, or reordered between the producer's and the
/// consumer's version of a type is not a supported change. This exception replaces the two failure
/// modes that mismatch used to produce silently or opaquely:
/// <list type="bullet">
/// <item>a field-count mismatch (e.g. a field removed since the payload was produced) used to
/// silently read the wrong bytes into the wrong property, with no exception at all;</item>
/// <item>a field-order mismatch used to surface as an opaque low-level exception (typically
/// <see cref="IndexOutOfRangeException"/>) from deep inside Apache.Avro's reader, with no indication
/// of the actual cause.</item>
/// </list>
/// Both are now detected and reported clearly, though not all mismatches are - see
/// <c>Benzene.Avro/CLAUDE.md</c> for what this package does and does not guarantee.
/// </summary>
public class AvroSchemaMismatchException : Exception
{
    private const string Guidance =
        "Benzene.Avro has no schema-evolution support: reader and writer must share the exact same " +
        "reflected/registered schema (same fields, same order). This is typically caused by a field " +
        "added, removed, or reordered between the producer's and the consumer's version of the type.";

    /// <summary>
    /// Initializes a new instance for a decode-time failure (e.g. a field-order mismatch that made the
    /// underlying reader misinterpret the bytes).
    /// </summary>
    /// <param name="targetType">The CLR type deserialization was attempted against.</param>
    /// <param name="innerException">The low-level exception the mismatch surfaced as.</param>
    public AvroSchemaMismatchException(Type targetType, Exception innerException)
        : base($"Failed to deserialize an Avro payload as '{targetType.FullName}': the data does not " +
               $"match the resolved schema. {Guidance}", innerException)
    {
        TargetType = targetType;
    }

    /// <summary>
    /// Initializes a new instance for a trailing-bytes failure (the resolved schema consumed fewer
    /// bytes than the payload contains - e.g. a field removed since the payload was produced).
    /// </summary>
    /// <param name="targetType">The CLR type deserialization was attempted against.</param>
    /// <param name="bytesConsumed">How many bytes the resolved schema actually consumed.</param>
    /// <param name="totalBytes">The total number of bytes in the payload.</param>
    public AvroSchemaMismatchException(Type targetType, long bytesConsumed, long totalBytes)
        : base($"Failed to deserialize an Avro payload as '{targetType.FullName}': the resolved schema " +
               $"only consumed {bytesConsumed} of the payload's {totalBytes} bytes, leaving data unread. " +
               $"{Guidance}")
    {
        TargetType = targetType;
    }

    /// <summary>The CLR type deserialization was attempted against.</summary>
    public Type TargetType { get; }
}
