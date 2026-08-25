using System.Net;
using System.Security.Claims;
using System.Text;
using Benzene.Auth.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Mesh.Host.Test;

/// <summary>
/// Unit-level coverage of <see cref="MeshAuthGate"/> - the single gate every non-<c>none</c> auth mode
/// goes through (work/enterprise/slice-2-auth.md tasks 2.2, 2.3, 2.5, 2.6). Exercises the gate directly
/// against a <see cref="DefaultHttpContext"/> rather than a hosted server: modes <c>proxy</c>/
/// <c>basic</c> and the ingestion path need nothing beyond request headers/peer address, so a real
/// Kestrel pipeline would only add noise here. Mode <c>oidc</c>'s "already authenticated" branch is
/// covered too (it only reads <c>context.User</c>); its "challenge/redirect" branch needs a real
/// <c>IAuthenticationService</c> and is covered by <see cref="MeshAuthAcceptanceTest"/> instead. The
/// end-to-end acceptance coverage proving <c>/artifacts</c> is actually protected once the gate is wired
/// into <see cref="Startup"/> - in both artifact-store branches - lives in
/// <see cref="MeshAuthAcceptanceTest"/>.
/// </summary>
[Collection(EnvVarMutatingTestCollection.Name)]
public class MeshAuthGateTest
{
    private static DefaultHttpContext NewContext(string path = "/mesh-ui")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        context.RequestServices = new ServiceCollection().AddScoped<AuthenticationHolder>().BuildServiceProvider();
        return context;
    }

    private static RequestDelegate NextDelegate(out Func<bool> wasCalled)
    {
        var called = false;
        wasCalled = () => called;
        return _ =>
        {
            called = true;
            return Task.CompletedTask;
        };
    }

    private static void Invoke(MeshAuthGate gate, HttpContext context) => gate.InvokeAsync(context).GetAwaiter().GetResult();

    // --- Validate() / constructor fail-fast --------------------------------------------------

    [Fact]
    public void Validate_UnknownMode_ThrowsNamingValidValues()
    {
        var config = new MeshAuthConfig { Mode = "ldap" };

        var exception = Assert.Throws<InvalidOperationException>(() => MeshAuthGate.Validate(config));

        Assert.Contains("none, proxy, basic, oidc", exception.Message);
    }

    [Fact]
    public void Validate_ProxyModeWithEmptyTrustedProxies_Throws()
    {
        var config = new MeshAuthConfig { Mode = "proxy" };

        var exception = Assert.Throws<InvalidOperationException>(() => MeshAuthGate.Validate(config));

        Assert.Contains("trustedProxies", exception.Message);
    }

    [Fact]
    public void Validate_ProxyModeWithTrustedProxies_DoesNotThrow()
    {
        var config = new MeshAuthConfig { Mode = "proxy" };
        config.Proxy.TrustedProxies = new[] { "10.0.0.5" };

        var exception = Record.Exception(() => MeshAuthGate.Validate(config));

        Assert.Null(exception);
    }

    [Fact]
    public void Validate_BasicModeWithoutEnvVars_Throws()
    {
        WithEnvVars(new (string, string?)[] { ("MESH_BASIC_USER", null), ("MESH_BASIC_PASSWORD", null) }, () =>
        {
            var config = new MeshAuthConfig { Mode = "basic" };

            var exception = Assert.Throws<InvalidOperationException>(() => MeshAuthGate.Validate(config));

            Assert.Contains("MESH_BASIC_USER", exception.Message);
        });
    }

    [Fact]
    public void Validate_OidcModeWithoutAuthorityOrClientId_Throws()
    {
        var config = new MeshAuthConfig { Mode = "oidc" };

        var exception = Assert.Throws<InvalidOperationException>(() => MeshAuthGate.Validate(config));

        Assert.Contains("auth.oidc.authority", exception.Message);
    }

    [Fact]
    public void Validate_OidcModeWithoutClientSecretEnvVarSet_Throws()
    {
        var config = new MeshAuthConfig { Mode = "oidc" };
        config.Oidc.Authority = "https://idp.example.com";
        config.Oidc.ClientId = "client-id";

        WithEnvVars(new (string, string?)[] { (config.Oidc.ClientSecretEnvVar, null) }, () =>
        {
            var exception = Assert.Throws<InvalidOperationException>(() => MeshAuthGate.Validate(config));

            Assert.Contains(config.Oidc.ClientSecretEnvVar, exception.Message);
        });
    }

    [Fact]
    public void Validate_UnknownIngestionMode_ThrowsNamingValidValues()
    {
        var config = new MeshAuthConfig();
        config.Ingestion.Mode = "hmac";

        var exception = Assert.Throws<InvalidOperationException>(() => MeshAuthGate.Validate(config));

        Assert.Contains("open, sharedSecret", exception.Message);
    }

    // Regression (found by adversarial review, corrected 2026-08-23): InvokeAsync's "mode none" branch
    // returns immediately (see the top of InvokeAsync) BEFORE the dispatchRole check further down ever
    // runs - mode "none" establishes no principal at all, so a dispatchRole requirement configured
    // alongside it could never be enforced. An operator who sets auth.mode "none" (e.g. trusting a
    // network perimeter for general access) and auth.dispatchRole (meaning to additionally gate the
    // dangerous mesh:dispatch endpoint by role) silently got a mesh:dispatch open to ANY caller, role or
    // no role - the exact "documented/bound but never wired into the path that needs it" pattern this
    // file's other Validate() checks exist to catch before a deploy, not discover after one.
    [Fact]
    public void Validate_DispatchRoleSetWithModeNone_Throws()
    {
        var config = new MeshAuthConfig { Mode = "none", DispatchRole = "mesh-admins" };

        var exception = Assert.Throws<InvalidOperationException>(() => MeshAuthGate.Validate(config));

        Assert.Contains("dispatchRole", exception.Message);
        Assert.Contains("none", exception.Message);
    }

    [Fact]
    public void Validate_DispatchRoleUnsetWithModeNone_DoesNotThrow()
    {
        var config = new MeshAuthConfig { Mode = "none" };

        var exception = Record.Exception(() => MeshAuthGate.Validate(config));

        Assert.Null(exception);
    }

    // --- WP-1(a): the mode x option satisfiability matrix (work/bug-fix-designs-2026-08.md "WP-1",
    // tasks #3, #6, #19, #27) - deploy/Mesh/CONFIG.md's "Which options work under which auth modes"
    // carries the same table this exercises. ------------------------------------------------------

    // requiredGroups (#6's sibling: it needs group claims exactly like dispatchRole does)

    [Theory]
    [InlineData("none")]
    [InlineData("basic")]
    public void Validate_RequiredGroupsSetWithModeThatCannotCarryGroups_Throws(string mode)
    {
        WithEnvVars(("MESH_BASIC_USER", "admin"), ("MESH_BASIC_PASSWORD", "s3cret"), () =>
        {
            var config = new MeshAuthConfig { Mode = mode, RequiredGroups = new[] { "mesh-admins" } };

            var exception = Assert.Throws<InvalidOperationException>(() => MeshAuthGate.Validate(config));

            Assert.Contains("requiredGroups", exception.Message);
            Assert.Contains(mode, exception.Message);
        });
    }

    [Fact]
    public void Validate_RequiredGroupsSetWithProxyModeWithoutGroupsHeader_Throws()
    {
        var config = ProxyConfig("10.0.0.5");
        config.RequiredGroups = new[] { "mesh-admins" };

        var exception = Assert.Throws<InvalidOperationException>(() => MeshAuthGate.Validate(config));

        Assert.Contains("requiredGroups", exception.Message);
        Assert.Contains("proxy", exception.Message);
    }

    [Fact]
    public void Validate_RequiredGroupsSetWithProxyModeWithGroupsHeader_DoesNotThrow()
    {
        var config = ProxyConfig("10.0.0.5");
        config.RequiredGroups = new[] { "mesh-admins" };
        config.Proxy.GroupsHeader = "X-Forwarded-Groups";

        var exception = Record.Exception(() => MeshAuthGate.Validate(config));

        Assert.Null(exception);
    }

    [Fact]
    public void Validate_RequiredGroupsSetWithOidcMode_DoesNotThrow()
    {
        WithEnvVars(("MESH_OIDC_CLIENT_SECRET", "shh"), () =>
        {
            var config = new MeshAuthConfig { Mode = "oidc", RequiredGroups = new[] { "mesh-admins" } };
            config.Oidc.Authority = "https://idp.example.com";
            config.Oidc.ClientId = "client-id";

            var exception = Record.Exception(() => MeshAuthGate.Validate(config));

            Assert.Null(exception);
        });
    }

    // dispatchRole (#6, #27) - the existing mode "none" case above already covers that leg

    [Fact]
    public void Validate_DispatchRoleSetWithModeBasic_Throws()
    {
        // #27: basic is a deliberately minimal single-account mode - anyone needing roles has
        // outgrown it. Do NOT "fix" this by adding a MESH_BASIC_ROLES knob; that alternative is
        // explicitly rejected in the ruling (work/bug-fix-designs-2026-08.md "WP-1").
        WithEnvVars(("MESH_BASIC_USER", "admin"), ("MESH_BASIC_PASSWORD", "s3cret"), () =>
        {
            var config = new MeshAuthConfig { Mode = "basic", DispatchRole = "mesh-admins" };

            var exception = Assert.Throws<InvalidOperationException>(() => MeshAuthGate.Validate(config));

            Assert.Contains("dispatchRole", exception.Message);
            Assert.Contains("basic", exception.Message);
        });
    }

    [Fact]
    public void Validate_DispatchRoleSetWithProxyModeWithoutGroupsHeader_Throws()
    {
        // #6: proxy without a groupsHeader establishes an identity but no group/role claims at all,
        // so a dispatchRole requirement could never be satisfied by any caller.
        var config = ProxyConfig("10.0.0.5");
        config.DispatchRole = "mesh-admins";

        var exception = Assert.Throws<InvalidOperationException>(() => MeshAuthGate.Validate(config));

        Assert.Contains("dispatchRole", exception.Message);
        Assert.Contains("proxy", exception.Message);
    }

    [Fact]
    public void Validate_DispatchRoleSetWithProxyModeWithGroupsHeader_DoesNotThrow()
    {
        var config = ProxyConfig("10.0.0.5");
        config.DispatchRole = "mesh-admins";
        config.Proxy.GroupsHeader = "X-Forwarded-Groups";

        var exception = Record.Exception(() => MeshAuthGate.Validate(config));

        Assert.Null(exception);
    }

    [Fact]
    public void Validate_DispatchRoleSetWithOidcMode_DoesNotThrow()
    {
        WithEnvVars(("MESH_OIDC_CLIENT_SECRET", "shh"), () =>
        {
            var config = new MeshAuthConfig { Mode = "oidc", DispatchRole = "mesh-admins" };
            config.Oidc.Authority = "https://idp.example.com";
            config.Oidc.ClientId = "client-id";

            var exception = Record.Exception(() => MeshAuthGate.Validate(config));

            Assert.Null(exception);
        });
    }

    // allowedEmailDomains (#3)

    [Fact]
    public void Validate_AllowedEmailDomainsSetWithModeNone_Throws()
    {
        var config = new MeshAuthConfig { Mode = "none", AllowedEmailDomains = new[] { "example.com" } };

        var exception = Assert.Throws<InvalidOperationException>(() => MeshAuthGate.Validate(config));

        Assert.Contains("allowedEmailDomains", exception.Message);
        Assert.Contains("none", exception.Message);
    }

    [Fact]
    public void Validate_AllowedEmailDomainsSetWithModeBasic_Throws()
    {
        // Under "basic" the operator IS the one account - domain-filtering it is meaningless, so this
        // is rejected rather than silently ignored (matching the ruling's rationale, not #3's own
        // repro, which used "none").
        WithEnvVars(("MESH_BASIC_USER", "admin"), ("MESH_BASIC_PASSWORD", "s3cret"), () =>
        {
            var config = new MeshAuthConfig { Mode = "basic", AllowedEmailDomains = new[] { "example.com" } };

            var exception = Assert.Throws<InvalidOperationException>(() => MeshAuthGate.Validate(config));

            Assert.Contains("allowedEmailDomains", exception.Message);
            Assert.Contains("basic", exception.Message);
        });
    }

    [Fact]
    public void Validate_AllowedEmailDomainsSetWithProxyMode_DoesNotThrow()
    {
        var config = ProxyConfig("10.0.0.5");
        config.AllowedEmailDomains = new[] { "example.com" };

        var exception = Record.Exception(() => MeshAuthGate.Validate(config));

        Assert.Null(exception);
    }

    [Fact]
    public void Validate_AllowedEmailDomainsSetWithOidcMode_DoesNotThrow()
    {
        WithEnvVars(("MESH_OIDC_CLIENT_SECRET", "shh"), () =>
        {
            var config = new MeshAuthConfig { Mode = "oidc", AllowedEmailDomains = new[] { "example.com" } };
            config.Oidc.Authority = "https://idp.example.com";
            config.Oidc.ClientId = "client-id";

            var exception = Record.Exception(() => MeshAuthGate.Validate(config));

            Assert.Null(exception);
        });
    }

    // dispatch.enabled (#19 - the live-reproduced "dispatch is permanently 403'd" bug: mode "none"
    // establishes no identity at all, so MeshDispatchGate's fail-closed identity check refuses every
    // dispatch regardless of a valid CSRF header. The fix turns that into a clear startup-time
    // rejection instead of a confusing always-403 at request time.)

    [Fact]
    public void Validate_DispatchEnabledWithModeNone_Throws()
    {
        var config = new MeshAuthConfig { Mode = "none" };

        var exception = Assert.Throws<InvalidOperationException>(() => MeshAuthGate.Validate(config, dispatchEnabled: true));

        Assert.Contains("dispatch.enabled", exception.Message);
        Assert.Contains("none", exception.Message);
    }

    [Fact]
    public void Validate_DispatchDisabledWithModeNone_DoesNotThrow()
    {
        var config = new MeshAuthConfig { Mode = "none" };

        var exception = Record.Exception(() => MeshAuthGate.Validate(config, dispatchEnabled: false));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("basic")]
    [InlineData("proxy")]
    [InlineData("oidc")]
    public void Validate_DispatchEnabledWithModeThatEstablishesIdentity_DoesNotThrow(string mode)
    {
        WithEnvVars(("MESH_BASIC_USER", "admin"), ("MESH_BASIC_PASSWORD", "s3cret"), () =>
        {
            WithEnvVars(("MESH_OIDC_CLIENT_SECRET", "shh"), () =>
            {
                var config = new MeshAuthConfig { Mode = mode };
                config.Proxy.TrustedProxies = new[] { "10.0.0.5" };
                config.Oidc.Authority = "https://idp.example.com";
                config.Oidc.ClientId = "client-id";

                var exception = Record.Exception(() => MeshAuthGate.Validate(config, dispatchEnabled: true));

                Assert.Null(exception);
            });
        });
    }

    // --- WP-1(b): auth.oidc.requireHttpsMetadata (#20 - a non-https authority used to reach the
    // OIDC handler and crash with an unhandled 500 at request time; this rejects it at startup
    // instead). ---------------------------------------------------------------------------------

    [Fact]
    public void Validate_OidcModeWithHttpAuthorityAndDefaultRequireHttpsMetadata_Throws()
    {
        WithEnvVars(("MESH_OIDC_CLIENT_SECRET", "shh"), () =>
        {
            var config = new MeshAuthConfig { Mode = "oidc" };
            config.Oidc.Authority = "http://idp.internal.example.com";
            config.Oidc.ClientId = "client-id";

            var exception = Assert.Throws<InvalidOperationException>(() => MeshAuthGate.Validate(config));

            Assert.Contains("requireHttpsMetadata", exception.Message);
            Assert.Contains("http://idp.internal.example.com", exception.Message);
        });
    }

    [Fact]
    public void Validate_OidcModeWithHttpAuthorityAndRequireHttpsMetadataFalse_DoesNotThrow()
    {
        WithEnvVars(("MESH_OIDC_CLIENT_SECRET", "shh"), () =>
        {
            var config = new MeshAuthConfig { Mode = "oidc" };
            config.Oidc.Authority = "http://localhost:8080";
            config.Oidc.ClientId = "client-id";
            config.Oidc.RequireHttpsMetadata = false;

            var exception = Record.Exception(() => MeshAuthGate.Validate(config));

            Assert.Null(exception);
        });
    }

    [Fact]
    public void Validate_OidcModeWithHttpsAuthority_DoesNotThrow()
    {
        WithEnvVars(("MESH_OIDC_CLIENT_SECRET", "shh"), () =>
        {
            var config = new MeshAuthConfig { Mode = "oidc" };
            config.Oidc.Authority = "https://idp.example.com";
            config.Oidc.ClientId = "client-id";

            var exception = Record.Exception(() => MeshAuthGate.Validate(config));

            Assert.Null(exception);
        });
    }

    // --- mode none -----------------------------------------------------------------------------

    [Fact]
    public void NoneMode_CallsNextForAnyPath()
    {
        var next = NextDelegate(out var wasCalled);
        var gate = new MeshAuthGate(next, new MeshAuthConfig { Mode = "none" });
        var context = NewContext("/artifacts/manifest.json");

        Invoke(gate, context);

        Assert.True(wasCalled());
    }

    // --- mode proxy (task 2.2) ------------------------------------------------------------------

    private static MeshAuthConfig ProxyConfig(params string[] trustedProxies)
    {
        var config = new MeshAuthConfig { Mode = "proxy" };
        config.Proxy.TrustedProxies = trustedProxies;
        return config;
    }

    [Fact]
    public void ProxyMode_HeaderAbsent_Unauthorized()
    {
        var next = NextDelegate(out var wasCalled);
        var gate = new MeshAuthGate(next, ProxyConfig("127.0.0.1"));
        var context = NewContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");

        Invoke(gate, context);

        Assert.False(wasCalled());
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public void ProxyMode_HeaderPresentFromUntrustedPeer_Unauthorized()
    {
        var next = NextDelegate(out var wasCalled);
        var gate = new MeshAuthGate(next, ProxyConfig("10.0.0.5"));
        var context = NewContext();
        context.Request.Headers["X-Forwarded-User"] = "alice@example.com";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9"); // not in trustedProxies

        Invoke(gate, context);

        Assert.False(wasCalled());
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public void ProxyMode_HeaderPresentFromTrustedPeer_CallsNextAndSetsHolder()
    {
        var next = NextDelegate(out var wasCalled);
        var gate = new MeshAuthGate(next, ProxyConfig("10.0.0.5"));
        var context = NewContext();
        context.Request.Headers["X-Forwarded-User"] = "alice@example.com";
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");

        Invoke(gate, context);

        Assert.True(wasCalled());
        Assert.Equal("alice@example.com",
            context.RequestServices.GetRequiredService<AuthenticationHolder>().Principal?.FindFirst(ClaimTypes.Email)?.Value);
    }

    // --- mode basic (task 2.3) ------------------------------------------------------------------

    [Fact]
    public void BasicMode_NoAuthorizationHeader_UnauthorizedWithChallenge()
    {
        WithEnvVars(("MESH_BASIC_USER", "admin"), ("MESH_BASIC_PASSWORD", "s3cret"), () =>
        {
            var next = NextDelegate(out var wasCalled);
            var gate = new MeshAuthGate(next, new MeshAuthConfig { Mode = "basic" });
            var context = NewContext();

            Invoke(gate, context);

            Assert.False(wasCalled());
            Assert.Equal(401, context.Response.StatusCode);
            Assert.Equal("Basic realm=\"Benzene Mesh\"", context.Response.Headers["WWW-Authenticate"].ToString());
        });
    }

    [Fact]
    public void BasicMode_WrongPassword_Unauthorized()
    {
        WithEnvVars(("MESH_BASIC_USER", "admin"), ("MESH_BASIC_PASSWORD", "s3cret"), () =>
        {
            var next = NextDelegate(out var wasCalled);
            var gate = new MeshAuthGate(next, new MeshAuthConfig { Mode = "basic" });
            var context = NewContext();
            context.Request.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:wrong"));

            Invoke(gate, context);

            Assert.False(wasCalled());
            Assert.Equal(401, context.Response.StatusCode);
        });
    }

    [Fact]
    public void BasicMode_CorrectCredentials_CallsNextAndSetsHolder()
    {
        WithEnvVars(("MESH_BASIC_USER", "admin"), ("MESH_BASIC_PASSWORD", "s3cret"), () =>
        {
            var next = NextDelegate(out var wasCalled);
            var gate = new MeshAuthGate(next, new MeshAuthConfig { Mode = "basic" });
            var context = NewContext();
            context.Request.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:s3cret"));

            Invoke(gate, context);

            Assert.True(wasCalled());
            Assert.Equal("admin", context.RequestServices.GetRequiredService<AuthenticationHolder>().Principal?.Identity?.Name);
        });
    }

    // --- mode oidc (task 2.4) - only the "already authenticated" branch; see the class doc comment ---

    [Fact]
    public void OidcMode_AlreadyAuthenticated_CallsNextAndSetsHolder()
    {
        WithEnvVars(("MESH_OIDC_CLIENT_SECRET", "shh"), () =>
        {
            var next = NextDelegate(out var wasCalled);
            var config = new MeshAuthConfig { Mode = "oidc" };
            config.Oidc.Authority = "https://idp.example.com";
            config.Oidc.ClientId = "client-id";
            var gate = new MeshAuthGate(next, config);

            var context = NewContext();
            var identity = new ClaimsIdentity("TestScheme");
            identity.AddClaim(new Claim(ClaimTypes.Email, "bob@example.com"));
            context.User = new ClaimsPrincipal(identity);

            Invoke(gate, context);

            Assert.True(wasCalled());
            Assert.Equal("bob@example.com",
                context.RequestServices.GetRequiredService<AuthenticationHolder>().Principal?.FindFirst(ClaimTypes.Email)?.Value);
        });
    }

    // --- allowedEmailDomains / requiredGroups (task 2.5) ----------------------------------------

    [Fact]
    public void AllowedEmailDomains_CallerOutsideAllowedDomains_Forbidden()
    {
        var next = NextDelegate(out var wasCalled);
        var config = ProxyConfig("10.0.0.5");
        config.AllowedEmailDomains = new[] { "example.com" };
        var gate = new MeshAuthGate(next, config);
        var context = NewContext();
        context.Request.Headers["X-Forwarded-User"] = "alice@other.com";
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");

        Invoke(gate, context);

        Assert.False(wasCalled());
        Assert.Equal(403, context.Response.StatusCode);
    }

    [Fact]
    public void AllowedEmailDomains_CallerInsideAllowedDomains_CallsNext()
    {
        var next = NextDelegate(out var wasCalled);
        var config = ProxyConfig("10.0.0.5");
        config.AllowedEmailDomains = new[] { "example.com" };
        var gate = new MeshAuthGate(next, config);
        var context = NewContext();
        context.Request.Headers["X-Forwarded-User"] = "alice@example.com";
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");

        Invoke(gate, context);

        Assert.True(wasCalled());
    }

    [Fact]
    public void RequiredGroups_CallerMissingGroup_Forbidden()
    {
        var next = NextDelegate(out var wasCalled);
        var config = ProxyConfig("10.0.0.5");
        config.RequiredGroups = new[] { "mesh-admins" };
        // requiredGroups needs a mode that can carry group claims (WP-1(a)) - proxy only does with a
        // groupsHeader configured. Alice's request below still sends no X-Forwarded-Groups header, so
        // this is still a "caller authenticated but holds no group" case, just a satisfiable config.
        config.Proxy.GroupsHeader = "X-Forwarded-Groups";
        var gate = new MeshAuthGate(next, config);
        var context = NewContext();
        context.Request.Headers["X-Forwarded-User"] = "alice@example.com";
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");

        Invoke(gate, context);

        Assert.False(wasCalled());
        Assert.Equal(403, context.Response.StatusCode);
    }

    // --- the ingestion endpoint (task 2.6) - exempt from the modes above, controlled on its own -----

    [Fact]
    public void IngestionPath_OpenMode_CallsNextEvenWithoutAnyAuth()
    {
        // Proves the ingestion path is exempt from the session-auth mode entirely, not just from the
        // secret check below - a "proxy" host with no forwarded-identity header still accepts a report.
        var next = NextDelegate(out var wasCalled);
        var gate = new MeshAuthGate(next, ProxyConfig("10.0.0.5"));
        var context = NewContext(MeshAuthGate.IngestionPath);

        Invoke(gate, context);

        Assert.True(wasCalled());
    }

    [Fact]
    public void IngestionPath_SharedSecretModeMissingHeader_Unauthorized()
    {
        WithEnvVars(("MESH_INGEST_SECRET", "top-secret"), () =>
        {
            var next = NextDelegate(out var wasCalled);
            var config = new MeshAuthConfig();
            config.Ingestion.Mode = "sharedSecret";
            var gate = new MeshAuthGate(next, config);
            var context = NewContext(MeshAuthGate.IngestionPath);

            Invoke(gate, context);

            Assert.False(wasCalled());
            Assert.Equal(401, context.Response.StatusCode);
        });
    }

    [Fact]
    public void IngestionPathWithTrailingSlash_SharedSecretModeMissingHeader_StillUnauthorized()
    {
        // Regression test (corrected 2026-08-22): the exact-path checks in InvokeAsync used to compare
        // context.Request.Path against IngestionPath/DispatchPath with plain PathString.Equals - no
        // trailing-slash normalization - while the router downstream DOES strip a trailing slash before
        // matching. A request to "/mesh/report/" (one added slash) missed this gate's check entirely
        // and reached MeshReportMessageHandler unauthenticated, on the default config. See
        // MeshPathCanonicalizer.Canonicalize's remarks.
        WithEnvVars(("MESH_INGEST_SECRET", "top-secret"), () =>
        {
            var next = NextDelegate(out var wasCalled);
            var config = new MeshAuthConfig();
            config.Ingestion.Mode = "sharedSecret";
            var gate = new MeshAuthGate(next, config);
            var context = NewContext(MeshAuthGate.IngestionPath + "/");

            Invoke(gate, context);

            Assert.False(wasCalled());
            Assert.Equal(401, context.Response.StatusCode);
        });
    }

    [Fact]
    public void IngestionPath_SharedSecretModeWrongSecret_Unauthorized()
    {
        WithEnvVars(("MESH_INGEST_SECRET", "top-secret"), () =>
        {
            var next = NextDelegate(out var wasCalled);
            var config = new MeshAuthConfig();
            config.Ingestion.Mode = "sharedSecret";
            var gate = new MeshAuthGate(next, config);
            var context = NewContext(MeshAuthGate.IngestionPath);
            context.Request.Headers[MeshAuthGate.IngestSecretHeaderName] = "wrong";

            Invoke(gate, context);

            Assert.False(wasCalled());
            Assert.Equal(401, context.Response.StatusCode);
        });
    }

    [Fact]
    public void IngestionPath_SharedSecretModeCorrectSecret_CallsNext()
    {
        WithEnvVars(("MESH_INGEST_SECRET", "top-secret"), () =>
        {
            var next = NextDelegate(out var wasCalled);
            var config = new MeshAuthConfig();
            config.Ingestion.Mode = "sharedSecret";
            var gate = new MeshAuthGate(next, config);
            var context = NewContext(MeshAuthGate.IngestionPath);
            context.Request.Headers[MeshAuthGate.IngestSecretHeaderName] = "top-secret";

            Invoke(gate, context);

            Assert.True(wasCalled());
        });
    }

    // --- env var helpers ---------------------------------------------------------------------
    // Safe without an xunit [Collection("Sequential")] guard: xUnit runs the FACTS WITHIN one class
    // sequentially by default (only different classes/collections run in parallel with each other),
    // and no other class in this assembly reads/writes these specific env var names.

    private static void WithEnvVars((string Name, string Value) vars, Action action)
        => WithEnvVars(new (string, string?)[] { (vars.Name, vars.Value) }, action);

    private static void WithEnvVars((string Name, string Value) var1, (string Name, string Value) var2, Action action)
        => WithEnvVars(new (string, string?)[] { (var1.Name, var1.Value), (var2.Name, var2.Value) }, action);

    private static void WithEnvVars((string, string?)[] vars, Action action)
    {
        var previous = vars.Select(v => (v.Item1, Previous: Environment.GetEnvironmentVariable(v.Item1))).ToArray();
        try
        {
            foreach (var (name, value) in vars)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            action();
        }
        finally
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
