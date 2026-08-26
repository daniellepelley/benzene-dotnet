namespace Benzene.Abstractions.Results;

/// <summary>
/// Represents the result of a Benzene operation with status, errors, and a payload.
/// This interface enables consistent result handling across different handler types and middleware.
/// </summary>
public interface IBenzeneResult
{
    /// <summary>
    /// Gets the status code or status identifier for the result (e.g., "200", "404", "success", "not-found").
    /// </summary>
    string Status { get; }

    /// <summary>
    /// Gets a value indicating whether the operation completed successfully.
    /// </summary>
    bool IsSuccessful { get; }

    /// <summary>
    /// Gets the payload as an object for non-generic access. <c>null</c> for a failed result and for
    /// a void/no-payload result (e.g. <see cref="IBenzeneResult{T}"/> where <c>T</c> is
    /// <see cref="Void"/>) - callers should null-check or use <c>?.</c> rather than assume a payload
    /// is always present.
    /// </summary>
    object? PayloadAsObject { get; }

    /// <summary>
    /// Gets the structured errors, if any occurred during the operation. Never <c>null</c> - an empty
    /// list when the result carries no errors (the common case for a successful result, backed by a
    /// shared empty instance so the success path allocates nothing). Ordered; when more than one error
    /// is present, the order is significant. For the pre-<see cref="BenzeneError"/> <c>string[]</c>
    /// shape, project with <c>ErrorMessages()</c> (<c>Benzene.Results</c>).
    /// </summary>
    IReadOnlyList<BenzeneError> Errors { get; }
}

/// <summary>
/// Represents a strongly-typed result of a Benzene operation with a typed payload.
/// </summary>
/// <typeparam name="T">The type of the payload.</typeparam>
public interface IBenzeneResult<T> : IBenzeneResult
{
    /// <summary>
    /// Gets the strongly-typed payload of the result. <c>null</c>/<c>default</c> for a failed result
    /// and for a void/no-payload result - callers should null-check (or rely on <typeparamref name="T"/>
    /// being a non-nullable value type, where <c>default</c> is never actually <c>null</c>) rather than
    /// assume a payload is always present.
    /// </summary>
    T? Payload { get; }
}