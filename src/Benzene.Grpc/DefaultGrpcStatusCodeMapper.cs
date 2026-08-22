using Benzene.Results;
using Grpc.Core;

namespace Benzene.Grpc;

/// <summary>
/// Provides the default mapping between Benzene result status codes and gRPC status codes.
/// </summary>
/// <remarks>
/// Unknown or null status values default to <see cref="StatusCode.Internal"/>.
/// </remarks>
public class DefaultGrpcStatusCodeMapper : IGrpcStatusCodeMapper
{
    private const StatusCode DefaultValue = StatusCode.Internal;
    private readonly IDictionary<string, StatusCode> _dictionary;

    /// <summary>Initializes a new instance of the <see cref="DefaultGrpcStatusCodeMapper"/> class.</summary>
    public DefaultGrpcStatusCodeMapper()
    {
        _dictionary = new Dictionary<string, StatusCode>
        {
            { BenzeneResultStatus.Ok, StatusCode.OK },
            { BenzeneResultStatus.Ignored, StatusCode.OK },
            { BenzeneResultStatus.Created, StatusCode.OK },
            { BenzeneResultStatus.Accepted, StatusCode.OK },
            { BenzeneResultStatus.Updated, StatusCode.OK },
            { BenzeneResultStatus.Deleted, StatusCode.OK },
            { BenzeneResultStatus.BadRequest, StatusCode.InvalidArgument },
            { BenzeneResultStatus.ValidationError, StatusCode.InvalidArgument },
            { BenzeneResultStatus.Unauthorized, StatusCode.Unauthenticated },
            { BenzeneResultStatus.Forbidden, StatusCode.PermissionDenied },
            { BenzeneResultStatus.NotFound, StatusCode.NotFound },
            { BenzeneResultStatus.Conflict, StatusCode.AlreadyExists },
            { BenzeneResultStatus.NotImplemented, StatusCode.Unimplemented },
            { BenzeneResultStatus.ServiceUnavailable, StatusCode.Unavailable },
            { BenzeneResultStatus.TooManyRequests, StatusCode.ResourceExhausted },
            { BenzeneResultStatus.Timeout, StatusCode.DeadlineExceeded },
            { BenzeneResultStatus.UnexpectedError, StatusCode.Internal }
        };
    }

    /// <inheritdoc />
    public StatusCode Map(string? benzeneResultStatus)
    {
        if (benzeneResultStatus == null)
        {
            return DefaultValue;
        }

        return _dictionary.TryGetValue(benzeneResultStatus, out var mapped)
            ? mapped
            : DefaultValue;
    }

    /// <summary>
    /// Maps a Benzene result status to a gRPC status code, honoring <paramref name="isSuccessful"/>
    /// for a status outside the known vocabulary: an application-defined successful status maps to
    /// <see cref="StatusCode.OK"/> instead of <see cref="StatusCode.Internal"/>, since the caller's
    /// <c>IsSuccessful</c> is the framework's authoritative signal for a custom status. The raw status
    /// string still reaches the client verbatim via the <c>benzene-status</c> trailer regardless.
    /// </summary>
    /// <param name="benzeneResultStatus">The Benzene result status string to map.</param>
    /// <param name="isSuccessful">Whether the result was successful.</param>
    /// <returns>The corresponding <see cref="StatusCode"/>.</returns>
    public StatusCode Map(string? benzeneResultStatus, bool isSuccessful)
    {
        if (benzeneResultStatus != null && _dictionary.TryGetValue(benzeneResultStatus, out var mapped))
        {
            return mapped;
        }

        return isSuccessful ? StatusCode.OK : DefaultValue;
    }
}
