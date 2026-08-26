namespace Benzene.Mesh.Contracts;

/// <summary>
/// The classification values <see cref="MeshServiceSnapshot.ErrorClass"/> can carry. Loose string
/// constants rather than an enum, matching <c>MeshServiceStatus</c>'s existing convention.
/// </summary>
/// <remarks>
/// Sits alongside <see cref="MeshServiceSnapshot.Error"/> (the redacted exception type name), not
/// instead of it: <c>Error</c> says which .NET exception was thrown, which varies per SDK
/// (<c>AmazonServiceException</c>, <c>HttpRequestException</c>, ...) and tells a reader nothing about
/// what to do next. <c>ErrorClass</c> says what kind of failure it was, in vocabulary that's the same
/// regardless of which cloud a service happens to run on - a permission failure needs a role/policy
/// fix, an unreachable one needs a network/availability check, and a timeout might just need a wider
/// bound. <c>null</c> means either there was no error, or the failure's shape wasn't one this
/// classifier recognises (see <c>MeshAggregator.ClassifyError</c>) - never guessed at.
/// </remarks>
public static class MeshErrorClass
{
    /// <summary>The fetch failed with an authentication/authorization error (e.g. HTTP 401/403).</summary>
    public const string Permission = "permission";

    /// <summary>The fetch failed to reach the service at all (connection refused/reset, DNS, or a 5xx from an intermediary).</summary>
    public const string Unreachable = "unreachable";

    /// <summary>The fetch did not complete within its bound (see <c>PerServiceFetchTimeout</c>), or the target reported a timeout.</summary>
    public const string Timeout = "timeout";

    /// <summary>The fetch failed for a reason this classifier does not recognise.</summary>
    public const string Other = "other";
}
