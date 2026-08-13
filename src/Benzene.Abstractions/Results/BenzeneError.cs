using System.Text.Json.Serialization;

namespace Benzene.Abstractions.Results;

/// <summary>
/// One error in a failed <see cref="IBenzeneResult"/>: the human-readable message, and optionally the
/// field it belongs to and a machine-readable code a caller can branch on.
/// </summary>
/// <remarks>
/// A <c>sealed record</c> (value equality, so <c>Assert.Equal(new BenzeneError(...), result.Errors[0])</c>
/// is a natural test assertion) with a public parameterless constructor and init-only properties -
/// deliberately not a positional record. This type doubles as the wire DTO for a result's errors, and
/// Benzene ships several serializers, some of which require a parameterless constructor to deserialize
/// into; init-only setters are ordinary setters to reflection, so this shape round-trips through all of
/// them. <see cref="ToString"/> returns <see cref="Message"/>, so pre-existing
/// <c>string.Join(", ", result.Errors)</c>-style call sites keep compiling and keep producing the same
/// text after <see cref="IBenzeneResult.Errors"/> changed from <c>string[]</c> to
/// <c>IReadOnlyList&lt;BenzeneError&gt;</c>.
/// </remarks>
public sealed record BenzeneError
{
    /// <summary>Initializes a new instance with every member left at its default.</summary>
    public BenzeneError()
    {
    }

    /// <summary>Initializes a new instance with the given message and optional field/code.</summary>
    /// <param name="message">The human-readable error message.</param>
    /// <param name="field">
    /// The field this error belongs to, if any - the producer's property path (e.g. a .NET property
    /// name for the validation middlewares, or a JSON Pointer for <c>Benzene.JsonSchema</c>). Each
    /// producer documents which shape it emits.
    /// </param>
    /// <param name="code">
    /// A machine-readable rule code a caller can branch on, if the producer supplies one (e.g.
    /// FluentValidation's <c>ErrorCode</c>, emitted verbatim).
    /// </param>
    public BenzeneError(string message, string? field = null, string? code = null)
    {
        Message = message;
        Field = field;
        Code = code;
    }

    /// <summary>The human-readable error message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// The field this error belongs to, if any. <c>null</c> when the error isn't scoped to a single
    /// field, or the producer can't determine one.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Field { get; init; }

    /// <summary>
    /// A machine-readable rule code a caller can branch on, if the producer supplies one. <c>null</c>
    /// when no producer-specific code is available.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; init; }

    /// <summary>Returns <see cref="Message"/> - see the type-level remarks for why this matters.</summary>
    public override string ToString() => Message;
}
