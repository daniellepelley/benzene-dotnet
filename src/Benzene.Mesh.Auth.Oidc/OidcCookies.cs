namespace Benzene.Mesh.Auth.Oidc;

/// <summary>The two cookie names this package uses. Not configurable in this first pass - see this
/// package's <c>CLAUDE.md</c> follow-ups.</summary>
internal static class OidcCookies
{
    /// <summary>The short-lived CSRF state cookie set at <c>/login</c> and consumed at <c>/callback</c>.</summary>
    public const string StateCookieName = "benzene_mesh_oidc_state";

    /// <summary>The session cookie set on successful login, checked by <see cref="OidcSessionGateMiddleware{TContext}"/>.</summary>
    public const string SessionCookieName = "benzene_mesh_session";
}
