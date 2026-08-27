using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Http;

namespace Benzene.Mesh.Auth.Oidc;

/// <summary>
/// The generic, detail-free response <see cref="OidcLoginMiddleware{TContext}"/> and
/// <see cref="OidcCallbackMiddleware{TContext}"/> both write when OIDC discovery itself fails at
/// request time - a misconfigured (unreachable, non-existent) issuer, or a transient IdP outage.
/// </summary>
/// <remarks>
/// <para>
/// #173 / round 1's #20: <see cref="MeshOidcOptions.Validate"/> rejects a non-HTTPS issuer at wire-up
/// time, but that can only ever prove the issuer LOOKS safe - nothing at wire-up time can prove the
/// issuer is actually reachable and serving a valid discovery document, and that can only fail at
/// request time (the first, or any later, time <c>ConfigurationManager&lt;OpenIdConnectConfiguration&gt;
/// .GetConfigurationAsync()</c> is actually called). Before this fix neither <c>/login</c> nor
/// <c>/callback</c> caught that failure at all, so it surfaced as an unhandled 500 - this is what both
/// routes catch it into instead.
/// </para>
/// <para>
/// Deliberately distinct from <see cref="OidcCallbackMiddleware{TContext}"/>'s "access denied" copy
/// (used for a failed state/token-exchange/ID-token/allowlist check): this response is not a statement
/// about the caller's account or credentials, so a caller retrying a moment later is the right instinct
/// to encourage, not "try a different account". <c>503</c>, not <c>401</c>/<c>500</c>, for the same
/// reason - this is the service being (temporarily, or misconfigured-ly) unavailable, not the caller
/// being unauthorized.
/// </para>
/// </remarks>
internal static class OidcDiscoveryFailureResponse
{
    private const string Body =
        "<!doctype html><html><head><title>Sign-in unavailable</title></head><body>" +
        "<h1>Sign-in unavailable</h1><p>Sign-in is temporarily unavailable. Please try again shortly.</p></body></html>";

    /// <summary>Writes the generic discovery-failure response and finalizes it.</summary>
    public static async Task WriteAsync<TContext>(IBenzeneResponseAdapter<TContext> responseAdapter, TContext context)
        where TContext : IHttpContext
    {
        responseAdapter.SetStatusCode(context, "503");
        responseAdapter.SetContentType(context, "text/html; charset=utf-8");
        responseAdapter.SetBody(context, Body);
        await responseAdapter.FinalizeAsync(context);
    }
}
