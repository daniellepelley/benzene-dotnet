using System;
using System.Collections.Generic;

namespace Benzene.Mesh.Auth.Oidc;

/// <summary>
/// The explicit email allowlist check - case-insensitive, exact match only. No substring or domain
/// matching in this first pass (see this package's <c>CLAUDE.md</c>): a broader match rule is easy to
/// get subtly wrong (e.g. "ends with @company.com" matching "evilcompany.com" without a leading dot),
/// so exact match is the safe default until a real need for domain-wide allow rules shows up.
/// </summary>
internal static class EmailAllowlist
{
    /// <summary>
    /// Returns whether <paramref name="email"/> exactly matches one of <paramref name="allowedEmails"/>,
    /// ignoring case. An empty <paramref name="allowedEmails"/> or a null/empty <paramref name="email"/>
    /// always returns false - an empty allowlist denies everyone (see
    /// <see cref="MeshOidcOptions.AllowedEmails"/>), it is never treated as "no restriction".
    /// </summary>
    public static bool IsAllowed(IReadOnlyCollection<string> allowedEmails, string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || allowedEmails.Count == 0)
        {
            return false;
        }

        var trimmedEmail = email.Trim();
        foreach (var allowed in allowedEmails)
        {
            if (string.Equals(allowed.Trim(), trimmedEmail, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
