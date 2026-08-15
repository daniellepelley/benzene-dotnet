using System;

namespace Benzene.Mesh.Auth.Oidc;

/// <summary>
/// Guards the <c>returnTo</c> query parameter (<c>/login?returnTo=...</c>) against becoming an open
/// redirect: only a same-origin, path-absolute, relative URL is accepted - never a value that could
/// send a successfully-authenticated browser to another origin.
/// </summary>
internal static class ReturnToValidator
{
    /// <summary>
    /// Returns whether <paramref name="returnTo"/> is safe to redirect to after login: non-empty,
    /// starts with a single <c>/</c> (path-absolute), is not protocol-relative (<c>//host/...</c> or
    /// <c>/\host/...</c> - some browsers treat a leading backslash as a second slash), contains no
    /// embedded scheme (<c>://</c>) anywhere, and contains no control characters (tab/CR/LF - a classic
    /// whitespace-before-scheme bypass browsers have historically tolerated).
    /// </summary>
    public static bool IsSafe(string? returnTo)
    {
        if (string.IsNullOrWhiteSpace(returnTo))
        {
            return false;
        }

        if (returnTo.IndexOfAny(new[] { '\t', '\r', '\n' }) >= 0)
        {
            return false;
        }

        if (!returnTo.StartsWith('/'))
        {
            return false;
        }

        if (returnTo.StartsWith("//") || returnTo.StartsWith("/\\"))
        {
            return false;
        }

        if (returnTo.Contains("://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
