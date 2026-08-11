namespace Benzene.Http;

/// <summary>
/// Defines a contract for mapping Benzene result status codes to HTTP status codes.
/// </summary>
/// <remarks>
/// This interface enables consistent HTTP status code handling across different result types
/// by providing a mapping from Benzene's internal result status strings to standard HTTP
/// status code strings (e.g., "200", "404", "500").
/// </remarks>
public interface IHttpStatusCodeMapper
{
    /// <summary>
    /// Maps a Benzene result status to an HTTP status code.
    /// </summary>
    /// <param name="benzeneResultStatus">The Benzene result status string to map.</param>
    /// <returns>The corresponding HTTP status code as a string (e.g., "200", "404", "500").</returns>
    string Map(string benzeneResultStatus);

    /// <summary>
    /// Maps a Benzene result to an HTTP status code, with the result's
    /// <see cref="Benzene.Abstractions.Results.IBenzeneResult.IsSuccessful"/> available alongside its
    /// status. Default implementation delegates to <see cref="Map(string)"/> (ignoring
    /// <paramref name="isSuccessful"/>), so an existing custom mapper that only overrides the
    /// status-only overload keeps compiling and behaving exactly as before. The framework's own
    /// <c>DefaultHttpStatusCodeMapper</c> overrides this richer overload: an application-defined
    /// status outside the known vocabulary maps to 200 when <paramref name="isSuccessful"/> is true
    /// rather than the generic-error row, so <c>IsSuccessful</c> is honored for a custom status even
    /// though the status-only overload has no way to see it. Override this instead of
    /// <see cref="Map(string)"/> if your own mapping needs to vary by success/failure for statuses
    /// outside the known vocabulary.
    /// </summary>
    /// <param name="benzeneResultStatus">The Benzene result status string to map.</param>
    /// <param name="isSuccessful">Whether the result was successful.</param>
    /// <returns>The corresponding HTTP status code as a string (e.g., "200", "404", "500").</returns>
    string Map(string benzeneResultStatus, bool isSuccessful) => Map(benzeneResultStatus);
}
