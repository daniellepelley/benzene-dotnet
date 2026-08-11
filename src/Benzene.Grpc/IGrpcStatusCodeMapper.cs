using Grpc.Core;

namespace Benzene.Grpc;

/// <summary>
/// Defines a contract for mapping Benzene result status codes to gRPC status codes.
/// </summary>
public interface IGrpcStatusCodeMapper
{
    /// <summary>
    /// Maps a Benzene result status to a gRPC status code.
    /// </summary>
    /// <param name="benzeneResultStatus">The Benzene result status string to map.</param>
    /// <returns>The corresponding <see cref="StatusCode"/>.</returns>
    StatusCode Map(string? benzeneResultStatus);

    /// <summary>
    /// Maps a Benzene result to a gRPC status code, with the result's
    /// <see cref="Benzene.Abstractions.Results.IBenzeneResult.IsSuccessful"/> available alongside its
    /// status. Default implementation delegates to <see cref="Map(string)"/> (ignoring
    /// <paramref name="isSuccessful"/>), so an existing custom mapper that only overrides the
    /// status-only overload keeps compiling and behaving exactly as before. The framework's own
    /// <c>DefaultGrpcStatusCodeMapper</c> overrides this richer overload: an application-defined
    /// status outside the known vocabulary maps to <see cref="StatusCode.OK"/> when
    /// <paramref name="isSuccessful"/> is true instead of <see cref="StatusCode.Internal"/> - the raw
    /// status string still reaches the client verbatim via the <c>benzene-status</c> trailer
    /// (wire-contracts.md §4.2) regardless of which gRPC code it maps to.
    /// </summary>
    /// <param name="benzeneResultStatus">The Benzene result status string to map.</param>
    /// <param name="isSuccessful">Whether the result was successful.</param>
    /// <returns>The corresponding <see cref="StatusCode"/>.</returns>
    StatusCode Map(string? benzeneResultStatus, bool isSuccessful) => Map(benzeneResultStatus);
}
