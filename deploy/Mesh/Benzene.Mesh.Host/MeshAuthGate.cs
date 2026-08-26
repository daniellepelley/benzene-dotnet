using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Benzene.Auth.Basic;
using Benzene.Auth.Core;
using Benzene.Mesh.Artifacts;
using Benzene.Mesh.Dispatch;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Benzene.Mesh.Host;

/// <summary>
/// The single auth gate for the whole host (work/enterprise/slice-2-auth.md, tasks 2.2-2.6). Registered
/// as ASP.NET Core middleware in <see cref="Startup.Configure"/> immediately after
/// <c>app.UseRouting()</c> and before <c>app.UseStaticFiles(...)</c> - the file-artifact-store branch
/// serves <c>/artifacts/*</c> entirely outside the Benzene pipeline below, so a gate placed only inside
/// <c>UseHttp</c> would leave that branch (the default) world-readable. See <c>Startup.cs</c>'s remarks
/// for the two artifact-store branches this one placement has to cover.
/// </summary>
/// <remarks>
/// One gate, not three mechanisms per mode: <c>proxy</c>/<c>basic</c>/<c>oidc</c> are all decided here,
/// so <c>/artifacts</c> (outside the Benzene pipeline) and everything inside it are protected the same
/// way. On success this sets <see cref="AuthenticationHolder.Principal"/> (resolved from
/// <see cref="HttpContext.RequestServices"/>) AND <c>context.User</c>, for every mode - not only
/// <c>oidc</c>, which gets <c>context.User</c> for free from <c>UseAuthentication()</c>.
/// <para>
/// <b>Neither reaches the Benzene pipeline below, in THIS host.</b> <c>Startup.Configure</c> wires
/// Benzene via <c>app.UseBenzene(IApplicationBuilder, ...)</c> - the "embedding" overload, which (per
/// its own remarks) resolves the Benzene pipeline's handlers/middleware through a SEPARATE
/// <see cref="IServiceProvider"/> built from a CLONE of the registrations, not
/// <see cref="HttpContext.RequestServices"/>'s real root provider - and
/// <c>MiddlewareApplication.HandleAsync</c> additionally creates a fresh DI scope per request/envelope
/// on top of that. So a scoped type this gate sets via <c>RequestServices</c> (like
/// <see cref="AuthenticationHolder"/>) is a DIFFERENT INSTANCE from whatever the same type resolves to
/// inside the Benzene pipeline - a downstream <c>AuthorizationExtensions.RequireRole</c> would see no
/// principal at all, not this gate's caller. <c>context.User</c> is the one thing that DOES cross that
/// boundary: <c>IHttpContextAccessor</c>'s backing store is a process-wide <c>AsyncLocal</c>, so any DI
/// container that resolves it (see <c>Startup.ConfigureServices</c>' <c>MeshDispatchIdentity</c>
/// registration) sees the SAME ambient <c>HttpContext</c>, this gate included. This is also why
/// <see cref="MeshAuthConfig.DispatchRole"/> is enforced right here, directly against
/// <c>HttpContext</c>, rather than as a Benzene-pipeline check - see <see cref="DispatchPath"/> and
/// <see cref="InvokeAsync"/>.
/// </para>
/// The push-ingestion endpoint (<see cref="IngestionPath"/>) is deliberately exempt from all of the
/// above - it is a service self-reporting, not a browser session, and is controlled independently by
/// <c>auth.ingestion</c> instead (see <see cref="HandleIngestionAsync"/>).
/// </remarks>
public class MeshAuthGate
{
    /// <summary>The push-ingestion endpoint's route - see <c>MeshReportMessageHandler</c>'s <c>[HttpEndpoint]</c>.</summary>
    public const string IngestionPath = "/mesh/report";

    /// <summary>
    /// The well-known path <c>Startup.Configure</c> mounts the opt-in dispatch envelope at. Read
    /// straight off <see cref="MeshDispatchGuardOptions"/>'s own default (there is no config knob to
    /// move either independently) rather than a second, hand-kept literal, so this and
    /// <c>Startup</c>'s own <c>MeshDispatchGuardOptions</c> instance can never drift apart.
    /// <see cref="MeshAuthConfig.DispatchRole"/> is enforced against exactly this path - see <see cref="InvokeAsync"/>.
    /// </summary>
    public static readonly string DispatchPath = new MeshDispatchGuardOptions().Path;

    /// <summary>
    /// WP-1(c) (#4): <c>POST /mesh/auth/logout</c> - oidc mode only, see <see cref="HandleLogoutAsync"/>.
    /// A fixed literal (no config knob to move it) so <c>Startup.cs</c>'s <c>UseMeshUi(logoutUrl: ...)</c>
    /// call and the check below can never drift apart.
    /// </summary>
    public const string LogoutPath = "/mesh/auth/logout";

    /// <summary>
    /// <see cref="IngestionPath"/>/<see cref="DispatchPath"/>/<see cref="LogoutPath"/>, canonicalized
    /// once through the exact same rule <see cref="MeshDispatchGuardMiddleware"/> and the router use -
    /// see <see cref="MeshPathCanonicalizer.Canonicalize"/>'s remarks and <c>InvokeAsync</c>'s
    /// trailing-slash correction below.
    /// </summary>
    private static readonly string CanonicalIngestionPath = MeshPathCanonicalizer.Canonicalize(IngestionPath);
    private static readonly string CanonicalDispatchPath = MeshPathCanonicalizer.Canonicalize(DispatchPath);
    private static readonly string CanonicalLogoutPath = MeshPathCanonicalizer.Canonicalize(LogoutPath);

    /// <summary>The header <c>auth.ingestion.mode: "sharedSecret"</c> reads its secret from.</summary>
    public const string IngestSecretHeaderName = "X-Mesh-Ingest-Secret";

    /// <summary>
    /// WP-1(c) (#4): the CSRF header <see cref="LogoutPath"/> requires, matching the same custom-header
    /// convention <see cref="MeshRefreshGuardOptions.DefaultHeaderName"/> (<c>X-Benzene-Refresh</c>) and
    /// <see cref="MeshDispatchGuardOptions.DefaultHeaderName"/> (<c>X-Benzene-Dispatch</c>) already use:
    /// a cross-site <c>&lt;form method="post"&gt;</c> cannot set a custom header at all, and a
    /// cross-origin <c>fetch()</c> that tries to triggers a CORS preflight this pipeline never approves -
    /// so only a genuine same-origin caller can ever supply it. The value sent is not inspected, exactly
    /// like its siblings - what defends against CSRF is that a cross-site caller cannot set the header at
    /// all, not what it would have set it to.
    /// </summary>
    public const string LogoutHeaderName = "X-Benzene-Logout";

    private static readonly string[] ValidModes = { "none", "proxy", "basic", "oidc" };
    private static readonly string[] ValidIngestionModes = { "open", "sharedSecret" };

    private readonly RequestDelegate _next;
    private readonly MeshAuthConfig _config;
    private readonly IBasicAuthCredentialValidator? _basicValidator;

    /// <summary>Initializes a new instance of the <see cref="MeshAuthGate"/> class.</summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="config">The auth config to enforce.</param>
    /// <param name="dispatchEnabled">
    /// Whether <c>dispatch.enabled</c> is set (<see cref="MeshDispatchConfig.Enabled"/>) - needed only
    /// for the satisfiability check in <see cref="Validate"/> (#19); defaults to <c>false</c> so every
    /// existing single-argument call site (this constructor is resolved positionally by
    /// <c>UseMiddleware&lt;MeshAuthGate&gt;</c>) keeps behaving exactly as before.
    /// </param>
    /// <exception cref="InvalidOperationException">The config is unsafe or incomplete for the selected mode - see <see cref="Validate"/>.</exception>
    public MeshAuthGate(RequestDelegate next, MeshAuthConfig config, bool dispatchEnabled = false)
    {
        Validate(config, dispatchEnabled);
        _next = next;
        _config = config;
        if (string.Equals(config.Mode, "basic", StringComparison.OrdinalIgnoreCase))
        {
            _basicValidator = new EnvBasicAuthCredentialValidator(
                Environment.GetEnvironmentVariable("MESH_BASIC_USER")!,
                Environment.GetEnvironmentVariable("MESH_BASIC_PASSWORD")!);
        }
    }

    /// <summary>
    /// Fails fast, naming the problem, rather than starting with a config that would silently under-
    /// protect the host - the house rule this whole feature exists to uphold. Called both from the
    /// constructor (so the running host refuses to start) and from <see cref="MeshConfigValidator"/>
    /// (so <c>--validate-config</c> catches it before a deploy).
    /// </summary>
    /// <param name="config">The auth config to validate.</param>
    /// <param name="dispatchEnabled">
    /// Whether <c>dispatch.enabled</c> is set (<see cref="MeshDispatchConfig.Enabled"/>) - feeds only
    /// the satisfiability check below (#19); defaults to <c>false</c> so every existing call site that
    /// doesn't care about dispatch keeps validating exactly as before.
    /// </param>
    public static void Validate(MeshAuthConfig config, bool dispatchEnabled = false)
    {
        if (!ValidModes.Contains(config.Mode, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unknown auth mode '{config.Mode}'. Valid values: {string.Join(", ", ValidModes)}.");
        }

        if (!ValidIngestionModes.Contains(config.Ingestion.Mode, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unknown auth.ingestion mode '{config.Ingestion.Mode}'. Valid values: {string.Join(", ", ValidIngestionModes)}.");
        }

        switch (config.Mode.ToLowerInvariant())
        {
            case "proxy":
                if (config.Proxy.TrustedProxies.Length == 0)
                {
                    throw new InvalidOperationException(
                        "auth.mode 'proxy' requires at least one entry in auth.proxy.trustedProxies - " +
                        "trusting a forwarded-identity header from an unrestricted set of peers is a " +
                        "total authentication bypass (anyone who can reach the host could set it themselves).");
                }

                break;
            case "basic":
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MESH_BASIC_USER")) ||
                    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MESH_BASIC_PASSWORD")))
                {
                    throw new InvalidOperationException(
                        "auth.mode 'basic' requires the MESH_BASIC_USER and MESH_BASIC_PASSWORD " +
                        "environment variables to both be set.");
                }

                break;
            case "oidc":
                if (string.IsNullOrEmpty(config.Oidc.Authority) || string.IsNullOrEmpty(config.Oidc.ClientId))
                {
                    throw new InvalidOperationException("auth.mode 'oidc' requires auth.oidc.authority and auth.oidc.clientId to be set.");
                }

                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(config.Oidc.ClientSecretEnvVar)))
                {
                    throw new InvalidOperationException(
                        $"auth.mode 'oidc' requires the environment variable named by auth.oidc.clientSecretEnvVar " +
                        $"('{config.Oidc.ClientSecretEnvVar}') to be set.");
                }

                // WP-1(b) (#20): a non-https authority used to reach the OIDC handler unvalidated and
                // crash with an unhandled 500 the first time discovery metadata was actually fetched
                // (mid-request, not at startup). Reject it here instead - fail-fast (P1) - unless the
                // operator has explicitly opted into plain HTTP via auth.oidc.requireHttpsMetadata:
                // false, which exists for the host's own Docker-first local story (a docker-composed
                // IdP with no TLS in front of it).
                if (config.Oidc.RequireHttpsMetadata &&
                    Uri.TryCreate(config.Oidc.Authority, UriKind.Absolute, out var authorityUri) &&
                    string.Equals(authorityUri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"auth.oidc.authority ('{config.Oidc.Authority}') is not https, and " +
                        "auth.oidc.requireHttpsMetadata is true (the default) - fetching OIDC discovery/" +
                        "JWKS metadata over plain HTTP is a man-in-the-middle risk with nothing to detect " +
                        "a spoofed authority. This is allowed ONLY for local development against an " +
                        "authority you run yourself with no TLS: set auth.oidc.requireHttpsMetadata to " +
                        "false explicitly if that's genuinely the case here - never in production.");
                }

                break;
        }

        if (string.Equals(config.Ingestion.Mode, "sharedSecret", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MESH_INGEST_SECRET")))
        {
            throw new InvalidOperationException("auth.ingestion.mode 'sharedSecret' requires the MESH_INGEST_SECRET environment variable to be set.");
        }

        // WP-1(a) (#3, #6, #19, #27): the mode x option satisfiability matrix - see
        // deploy/Mesh/CONFIG.md's "Which options work under which auth modes" (the same table,
        // verbatim) and work/bug-fix-designs-2026-08.md's "WP-1" for the ruling this implements. Every
        // rejection below names the offending config key(s) and the mode, so an operator who wrote a
        // config that reads as "gate X by role/domain/dispatch" but would actually leave X reachable by
        // any caller regardless finds out at startup, not by discovering the gap in production.
        //
        //                          | none    | basic   | proxy (no groupsHeader) | proxy (+groupsHeader) | oidc |
        // requiredGroups           | reject  | reject  | reject                  | allow                  | allow |
        // dispatchRole             | reject  | reject  | reject                  | allow                  | allow |
        // allowedEmailDomains      | reject  | reject  | allow                   | allow                  | allow |
        // dispatch.enabled         | reject  | allow   | allow                   | allow                  | allow |
        //
        // Rejected alternative (recorded - do not add this casually, amend the ruling first): a
        // MESH_BASIC_ROLES env knob to let the basic-auth account carry roles. "basic" stays a
        // deliberately minimal single-account mode; anyone needing roles has outgrown it.
        var mode = config.Mode.ToLowerInvariant();
        var proxyCarriesGroupClaims = mode == "proxy" && !string.IsNullOrEmpty(config.Proxy.GroupsHeader);
        var carriesGroupClaims = mode == "oidc" || proxyCarriesGroupClaims;
        var carriesEmailIdentity = mode == "proxy" || mode == "oidc";

        if (config.RequiredGroups.Length > 0 && !carriesGroupClaims)
        {
            throw new InvalidOperationException(
                $"auth.requiredGroups is set but auth.mode '{config.Mode}' cannot carry group claims - " +
                "requiredGroups needs mode 'oidc', or mode 'proxy' with auth.proxy.groupsHeader " +
                "configured. Choose one of those, or remove auth.requiredGroups.");
        }

        // Found by adversarial review (corrected 2026-08-23), then generalized here from "mode none"
        // to the full matrix: InvokeAsync's dispatchRole check can only ever fire against a principal
        // that actually carries group/role claims - none of "none" (no principal at all), "basic" (a
        // single hand-configured account with no roles - #27), or "proxy" without a groupsHeader (an
        // identity with no role claims at all - #6) can ever satisfy a dispatchRole requirement, so
        // configuring one alongside any of those modes silently leaves mesh:dispatch reachable by any
        // caller regardless of role - precisely the silent under-protection this method exists to catch.
        if (!string.IsNullOrEmpty(config.DispatchRole) && !carriesGroupClaims)
        {
            throw new InvalidOperationException(
                $"auth.dispatchRole is set but auth.mode '{config.Mode}' cannot carry group claims - " +
                "dispatchRole needs mode 'oidc', or mode 'proxy' with auth.proxy.groupsHeader " +
                "configured. Choose one of those, or remove auth.dispatchRole.");
        }

        // #37 (P6 - no inert options): dispatchRole gates ONE path, InvokeAsync's check against
        // DispatchPath (see its remarks) - and that path is only ever reachable at all when
        // dispatch.enabled is true (Startup.Configure only mounts the mesh:dispatch envelope behind
        // that flag). A dispatchRole configured while dispatch stays disabled is never evaluated by
        // anything - not unsafe (there is nothing to bypass), just a config an operator would
        // reasonably read as "role-gate dispatch" that silently does nothing. Same fail-fast treatment
        // as every other inert/unsatisfiable combination this method already rejects (dispatch.enabled
        // + mode "none" below is the mirror image: a real endpoint with a check that can never pass).
        if (!string.IsNullOrEmpty(config.DispatchRole) && !dispatchEnabled)
        {
            throw new InvalidOperationException(
                "auth.dispatchRole is set but dispatch.enabled is false - dispatchRole only gates " +
                "mesh:dispatch (InvokeAsync's role check runs against DispatchPath), which is not a " +
                "reachable endpoint at all while dispatch stays disabled, so the role requirement would " +
                "never be evaluated. Set dispatch.enabled true, or remove auth.dispatchRole.");
        }

        // #3: under "none" there is no identity to filter at all. Also rejected under "basic" - not
        // part of #3's own repro, but the same rationale extends to it: the operator defines the one
        // basic-auth account themselves, so domain-filtering it is meaningless busywork, not a real
        // control - reject rather than silently ignore.
        if (config.AllowedEmailDomains.Length > 0 && !carriesEmailIdentity)
        {
            throw new InvalidOperationException(
                $"auth.allowedEmailDomains is set but auth.mode '{config.Mode}' does not establish an " +
                "email-bearing identity - allowedEmailDomains needs mode 'proxy' or 'oidc'. Under " +
                "'basic' the operator defines the one account themselves, so domain-filtering it is " +
                "meaningless; under 'none' there is no identity to filter at all. Choose 'proxy'/'oidc', " +
                "or remove auth.allowedEmailDomains.");
        }

        // #19 - the live-reproduced bug: with dispatch.enabled true and auth.mode "none", mesh:dispatch
        // was reachable at the HTTP layer but MeshDispatchGate's own fail-closed identity check refuses
        // every request anyway (mode "none" never sets an identity) - so dispatch was permanently
        // 403'd regardless of a valid CSRF header, with nothing telling the operator why. Reject the
        // combination at startup instead, naming both keys.
        if (dispatchEnabled && mode == "none")
        {
            throw new InvalidOperationException(
                "dispatch.enabled is true but auth.mode is 'none' - mode 'none' establishes no caller " +
                "identity at all, so mesh:dispatch (which invokes a real handler) would be permanently " +
                "refused by MeshDispatchGate's fail-closed identity check regardless of a valid CSRF " +
                "header. Choose an auth mode that establishes an identity (basic/proxy/oidc), or leave " +
                "dispatch.enabled false.");
        }
    }

    /// <summary>Handles one request - see the class remarks for the overall shape.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        // Corrected 2026-08-22: this used to compare context.Request.Path against IngestionPath/
        // DispatchPath with plain PathString.Equals - exact-match only, no trailing-slash
        // normalization. BenzeneMessageHttpMiddleware/UrlMatcher/MeshDispatchGuardMiddleware all DO
        // normalize a trailing slash away before routing, so "/mesh/report/" or "/mesh/dispatch/"
        // (one added slash) missed this gate's exact-match checks entirely while still reaching the
        // real handler - the ingestion secret check and the dispatchRole check were both fully
        // bypassable this way. Canonicalizing through the same rule the guard/router already use
        // closes that gap; see MeshPathCanonicalizer.Canonicalize's remarks.
        var canonicalPath = MeshPathCanonicalizer.Canonicalize(context.Request.Path);
        if (canonicalPath == CanonicalIngestionPath)
        {
            await HandleIngestionAsync(context);
            return;
        }

        // WP-1(c) (#4): oidc mode only - see HandleLogoutAsync's remarks. Matched here, ahead of the
        // mode dispatch below, the same way IngestionPath is: logout must succeed even for a caller
        // whose session cookie has already expired (nothing to authenticate against, still fine to
        // sign out of), so it is not routed through AuthenticateOidcAsync's challenge/redirect branch.
        if (canonicalPath == CanonicalLogoutPath && string.Equals(_config.Mode, "oidc", StringComparison.OrdinalIgnoreCase))
        {
            await HandleLogoutAsync(context);
            return;
        }

        if (string.Equals(_config.Mode, "none", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var principal = _config.Mode.ToLowerInvariant() switch
        {
            "proxy" => await AuthenticateProxyAsync(context),
            "basic" => await AuthenticateBasicAsync(context),
            "oidc" => await AuthenticateOidcAsync(context),
            _ => null,
        };

        if (principal == null)
        {
            // Each Authenticate* above has already written the 401 (or, for oidc, the redirect
            // challenge) - nothing more to do.
            return;
        }

        if (!IsPermitted(principal))
        {
            await WriteForbiddenAsync(context, "Not permitted (allowedEmailDomains/requiredGroups)");
            return;
        }

        // work/enterprise/slice-2-auth.md task 2.5's dispatchRole gate. Enforced HERE, not as
        // AuthorizationExtensions.RequireRole on the mesh:dispatch BenzeneMessage envelope in
        // Startup.Configure: that envelope's inner pipeline runs in its own freshly-created DI scope
        // (see MiddlewareApplication.HandleAsync's serviceResolverFactory.CreateScope()) so it never
        // sees the AuthenticationHolder this gate populates on HttpContext.RequestServices - a
        // RequireRole there would see no principal at all and refuse every dispatch, role or no role.
        // This gate already runs once per request against the one HttpContext, so the check belongs
        // here, matched to the fixed, well-known dispatch path (see DispatchPath) exactly as
        // IngestionPath is above - one gate for the whole host, per the class remarks.
        if (!string.IsNullOrEmpty(_config.DispatchRole) &&
            canonicalPath == CanonicalDispatchPath &&
            !HasAnyRole(principal, new[] { _config.DispatchRole }))
        {
            await WriteForbiddenAsync(context,
                $"Missing required role for mesh dispatch (requires: {_config.DispatchRole})");
            return;
        }

        var holder = context.RequestServices.GetService<AuthenticationHolder>();
        if (holder != null)
        {
            holder.Principal = principal;
        }

        // Publishes the authenticated caller on the standard ASP.NET Core surface, for every mode -
        // not only oidc, which gets this for free from UseAuthentication(). This (not AuthenticationHolder
        // above) is what MeshDispatchIdentity's registration in Startup.ConfigureServices reads via
        // IHttpContextAccessor: HttpContextAccessor's backing store is a process-wide AsyncLocal, so it
        // is visible from ANY DI container/scope that resolves it - including the mesh:dispatch
        // BenzeneMessage envelope's own, separately-built container (see Startup's remarks on why
        // AuthenticationHolder/MeshDispatchIdentity registered directly cannot cross that boundary).
        context.User = principal;

        await _next(context);
    }

    private async Task<ClaimsPrincipal?> AuthenticateProxyAsync(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(_config.Proxy.UserHeader, out var userHeader) || string.IsNullOrWhiteSpace(userHeader))
        {
            await WriteUnauthorizedAsync(context, "Missing identity header");
            return null;
        }

        var peer = context.Connection.RemoteIpAddress;
        var isTrusted = peer != null && _config.Proxy.TrustedProxies.Any(p => IPAddress.TryParse(p, out var trusted) && trusted.Equals(peer));
        if (!isTrusted)
        {
            await WriteUnauthorizedAsync(context, "Untrusted proxy");
            return null;
        }

        var identity = new ClaimsIdentity("Proxy");
        identity.AddClaim(new Claim(ClaimTypes.Name, userHeader.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Email, userHeader.ToString()));

        if (!string.IsNullOrEmpty(_config.Proxy.GroupsHeader) &&
            context.Request.Headers.TryGetValue(_config.Proxy.GroupsHeader, out var groupsHeader))
        {
            foreach (var group in groupsHeader.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, group));
            }
        }

        return new ClaimsPrincipal(identity);
    }

    private async Task<ClaimsPrincipal?> AuthenticateBasicAsync(HttpContext context)
    {
        const string schemePrefix = "Basic ";
        if (!context.Request.Headers.TryGetValue("Authorization", out var authorization) ||
            string.IsNullOrEmpty(authorization) ||
            !authorization.ToString().StartsWith(schemePrefix, StringComparison.OrdinalIgnoreCase))
        {
            await ChallengeBasicAsync(context, "Missing or malformed Authorization header");
            return null;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization.ToString()[schemePrefix.Length..].Trim()));
        }
        catch (FormatException)
        {
            await ChallengeBasicAsync(context, "Malformed Basic credentials");
            return null;
        }

        var separator = decoded.IndexOf(':');
        if (separator < 0)
        {
            await ChallengeBasicAsync(context, "Malformed Basic credentials");
            return null;
        }

        var principal = await _basicValidator!.ValidateAsync(decoded[..separator], decoded[(separator + 1)..]);
        if (principal == null)
        {
            await ChallengeBasicAsync(context, "Invalid credentials");
            return null;
        }

        return principal;
    }

    private async Task<ClaimsPrincipal?> AuthenticateOidcAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            return context.User;
        }

        await context.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme);
        return null;
    }

    private bool IsPermitted(ClaimsPrincipal principal)
    {
        if (_config.AllowedEmailDomains.Length > 0)
        {
            var email = principal.FindFirst(ClaimTypes.Email)?.Value;
            var domain = email != null && email.Contains('@') ? email[(email.IndexOf('@') + 1)..] : null;
            if (domain == null || !_config.AllowedEmailDomains.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        if (_config.RequiredGroups.Length > 0 && !HasAnyRole(principal, _config.RequiredGroups))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="principal"/> holds at least one of <paramref name="anyOfRoles"/>, read
    /// from a <c>groups</c> claim or the common role claim types (<see cref="ClaimTypes.Role"/>,
    /// <c>role</c>, <c>roles</c>) as well as <see cref="ClaimsPrincipal.IsInRole"/>. Shared by
    /// <see cref="IsPermitted"/> (<see cref="MeshAuthConfig.RequiredGroups"/>) and the
    /// <see cref="MeshAuthConfig.DispatchRole"/> gate in <see cref="InvokeAsync"/> - both are "does this
    /// caller hold role X", just against a different config value.
    /// </summary>
    private static bool HasAnyRole(ClaimsPrincipal principal, IReadOnlyCollection<string> anyOfRoles)
    {
        var granted = principal.FindAll("groups").Concat(principal.FindAll(ClaimTypes.Role))
            .Concat(principal.FindAll("role")).Concat(principal.FindAll("roles"))
            .Select(c => c.Value);
        return anyOfRoles.Any(r => granted.Contains(r, StringComparer.Ordinal)) || anyOfRoles.Any(principal.IsInRole);
    }

    /// <summary>
    /// <c>open</c> (default) preserves today's behaviour: self-reporting services need nothing.
    /// <c>sharedSecret</c> requires <see cref="IngestSecretHeaderName"/> to match
    /// <c>MESH_INGEST_SECRET</c>, compared in constant time - a naive <c>==</c> on a secret is a
    /// timing oracle. Deliberately independent of <see cref="MeshAuthConfig.Mode"/>: a browser login
    /// flow cannot cover a service-to-service write endpoint.
    /// </summary>
    private async Task HandleIngestionAsync(HttpContext context)
    {
        if (!string.Equals(_config.Ingestion.Mode, "sharedSecret", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var expected = Environment.GetEnvironmentVariable("MESH_INGEST_SECRET") ?? string.Empty;
        var provided = context.Request.Headers.TryGetValue(IngestSecretHeaderName, out var header) ? header.ToString() : string.Empty;
        if (!FixedTimeEquals(provided, expected))
        {
            await WriteUnauthorizedAsync(context, "Missing or invalid ingestion secret");
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// WP-1(c) (#4): <c>POST /mesh/auth/logout</c>, oidc mode only. GET is rejected (<c>405</c>) - a
    /// GET-triggered logout is a CSRF hazard in its own right (a bare <c>&lt;img src=".../logout"&gt;</c>
    /// on another site would sign the victim out); a POST additionally requires
    /// <see cref="LogoutHeaderName"/> (<c>403</c> if absent), the same custom-header CSRF convention
    /// <see cref="MeshRefreshGuardMiddleware{TContext}"/>/<see cref="MeshDispatchGuardMiddleware{TContext}"/>
    /// already use. Signs out the cookie session, then answers
    /// <c>{"redirect": &lt;end_session_url or null&gt;}</c> - the end-session URL built from the OIDC
    /// authority's discovered <c>end_session_endpoint</c> (with <c>post_logout_redirect_uri</c>) when
    /// discovery provides one, else <c>null</c> (local sign-out only; the caller reloads instead of
    /// navigating away). A discovery failure here does not fail the whole logout - the cookie is
    /// already cleared by the time discovery is even attempted, so the caller still ends up signed out
    /// locally, just without an IdP-side redirect.
    /// </summary>
    private async Task HandleLogoutAsync(HttpContext context)
    {
        if (!string.Equals(context.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            await context.Response.WriteAsync("Method not allowed - mesh logout must be POSTed (a GET-triggered logout is a CSRF hazard).");
            return;
        }

        if (!context.Request.Headers.TryGetValue(LogoutHeaderName, out var header) || string.IsNullOrWhiteSpace(header))
        {
            await WriteForbiddenAsync(context, "forbidden");
            return;
        }

        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        string? redirect = null;
        var oidcOptions = context.RequestServices.GetService<IOptionsMonitor<OpenIdConnectOptions>>()
            ?.Get(OpenIdConnectDefaults.AuthenticationScheme);
        if (oidcOptions?.ConfigurationManager != null)
        {
            try
            {
                var configuration = await oidcOptions.ConfigurationManager.GetConfigurationAsync(context.RequestAborted);
                if (!string.IsNullOrEmpty(configuration.EndSessionEndpoint))
                {
                    var postLogoutRedirectUri = $"{context.Request.Scheme}://{context.Request.Host}/";
                    redirect = QueryHelpers.AddQueryString(
                        configuration.EndSessionEndpoint, "post_logout_redirect_uri", postLogoutRedirectUri);
                }
            }
            catch
            {
                // Discovery unreachable/failed - the local sign-out above already happened, so this
                // does not fail the request. redirect stays null: local sign-out only.
            }
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { redirect }));
    }

    private async Task ChallengeBasicAsync(HttpContext context, string detail)
    {
        context.Response.Headers.Append("WWW-Authenticate", "Basic realm=\"Benzene Mesh\"");
        await WriteUnauthorizedAsync(context, detail);
    }

    private static async Task WriteUnauthorizedAsync(HttpContext context, string detail)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync(detail);
    }

    private static async Task WriteForbiddenAsync(HttpContext context, string detail)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync(detail);
    }

    /// <summary>
    /// Constant-time string equality - a naive <c>==</c> on a secret is a timing oracle. Compares two
    /// equal-length buffers derived from <paramref name="a"/> itself when lengths differ, so a length
    /// mismatch doesn't return measurably faster than a same-length mismatch.
    /// </summary>
    internal static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return aBytes.Length == bBytes.Length
            ? CryptographicOperations.FixedTimeEquals(aBytes, bBytes)
            : CryptographicOperations.FixedTimeEquals(aBytes, aBytes) && false;
    }
}

/// <summary>
/// Mode <c>basic</c>'s <see cref="IBasicAuthCredentialValidator"/> - a single service-account-style
/// credential pair from the environment (<c>MESH_BASIC_USER</c>/<c>MESH_BASIC_PASSWORD</c>), never
/// <c>mesh.json</c>. Reuses <c>Benzene.Auth.Basic</c>'s validator contract rather than a bespoke shape.
/// </summary>
internal class EnvBasicAuthCredentialValidator : IBasicAuthCredentialValidator
{
    private readonly string _username;
    private readonly string _password;

    public EnvBasicAuthCredentialValidator(string username, string password)
    {
        _username = username;
        _password = password;
    }

    public Task<ClaimsPrincipal?> ValidateAsync(string username, string password)
    {
        var matches = MeshAuthGate.FixedTimeEquals(username, _username) & MeshAuthGate.FixedTimeEquals(password, _password);
        if (!matches)
        {
            return Task.FromResult<ClaimsPrincipal?>(null);
        }

        var identity = new ClaimsIdentity("Basic");
        identity.AddClaim(new Claim(ClaimTypes.Name, username));
        return Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(identity));
    }
}
