namespace Benzene.Mesh.Auth.Oidc;

/// <summary>Path normalization shared by every middleware in this package, matching
/// <c>Benzene.Mesh.Ui.MeshUiMiddleware</c>'s exact normalization rule (lowercase, leading slash, no
/// trailing slash) so the two families of path-matching middleware behave identically.</summary>
internal static class OidcPaths
{
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var trimmed = path.Trim();
        if (!trimmed.StartsWith('/'))
        {
            trimmed = "/" + trimmed;
        }

        if (trimmed.Length > 1 && trimmed.EndsWith('/'))
        {
            trimmed = trimmed.TrimEnd('/');
        }

        return trimmed.ToLowerInvariant();
    }
}
