using System;
using System.Collections.Generic;
using System.Text;

namespace Benzene.Mesh.Auth.Oidc;

/// <summary>Parses an incoming <c>Cookie</c> header and builds outgoing <c>Set-Cookie</c> header
/// values. Deliberately minimal (no attribute parsing beyond what this package itself sets) - this is
/// not a general-purpose cookie library.</summary>
internal static class CookieHeader
{
    /// <summary>Parses a raw <c>Cookie</c> header (<c>"a=1; b=2"</c>) into a name/value dictionary.
    /// Returns an empty dictionary for a null/empty header. Later duplicates win (matches how browsers
    /// send/most servers interpret repeated names).</summary>
    public static IDictionary<string, string> Parse(string? cookieHeader)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return result;
        }

        foreach (var pair in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = pair.Trim();
            var separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var name = trimmed[..separatorIndex].Trim();
            var value = trimmed[(separatorIndex + 1)..].Trim();
            result[name] = value;
        }

        return result;
    }

    /// <summary>Builds a <c>Set-Cookie</c> value for issuing a cookie: <c>HttpOnly</c>, <c>Secure</c>,
    /// <c>SameSite=Lax</c>, scoped to <paramref name="path"/>, expiring after <paramref name="maxAge"/>.</summary>
    public static string Build(string name, string value, string path, TimeSpan maxAge)
    {
        var sb = new StringBuilder();
        sb.Append(name).Append('=').Append(value);
        sb.Append("; Path=").Append(path);
        sb.Append("; Max-Age=").Append((long)maxAge.TotalSeconds);
        sb.Append("; HttpOnly; Secure; SameSite=Lax");
        return sb.ToString();
    }

    /// <summary>Builds a <c>Set-Cookie</c> value that immediately expires the named cookie (logout /
    /// clearing a single-use state cookie after it's been consumed).</summary>
    public static string BuildExpired(string name, string path)
    {
        var sb = new StringBuilder();
        sb.Append(name).Append("=; Path=").Append(path);
        sb.Append("; Max-Age=0; HttpOnly; Secure; SameSite=Lax");
        return sb.ToString();
    }
}
