using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Threading.Tasks;
using Benzene.AspNet.Core;
using Benzene.Abstractions.Middleware;
using Benzene.Auth.Core;
using Benzene.Auth.OAuth2;
using Benzene.Core.MessageHandlers;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Examples;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Auth;

/// <summary>
/// Exercises the A.4 authorization layer (<c>RequireRole</c>/<c>RequirePolicy</c>/
/// <c>RequireAuthorization</c>, all in <c>Benzene.Auth.Core</c>) chained after <c>UseOAuth2Bearer</c>
/// in one real Kestrel-hosted pipeline — the same composition style as <see cref="RequireScopeTest"/>.
/// GET <c>/example</c> is the protected downstream route.
/// </summary>
public class AuthorizationTest
{
    private const string Issuer = "https://issuer.example.com";
    private const string Audience = "my-api";

    private static async Task<(WebApplication App, Uri BaseAddress)> StartHostAsync(
        FakeJwksServer jwks, Action<IMiddlewarePipelineBuilder<AspNetContext>> configureAuthz)
    {
        var options = new OAuth2BearerOptions
        {
            JwksUri = jwks.JwksUri,
            ValidIssuers = new[] { Issuer },
            ValidAudiences = new[] { Audience },
            ValidAlgorithms = new[] { "RS256" },
            RequireHttpsMetadata = false
        };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers();
        builder.Services.ConfigureServiceCollection();

        var app = builder.Build();
        app.UseRouting();
        app.UseBenzene(benzene => benzene
            .UseHttp(asp =>
            {
                asp.UseOAuth2Bearer(options);
                configureAuthz(asp);
                asp.UseMessageHandlers();
            })
        );
        app.UseEndpoints(_ => { });

        await app.StartAsync();
        return (app, new Uri(app.Urls.First()));
    }

    private static async Task<HttpStatusCode> SendAsync(Uri baseAddress, string? token)
    {
        using var client = new HttpClient { BaseAddress = baseAddress };
        if (token != null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await client.GetAsync(Defaults.Path);
        return response.StatusCode;
    }

    private static async Task RunWithHostAsync(
        Action<IMiddlewarePipelineBuilder<AspNetContext>> configureAuthz, Func<System.Security.Cryptography.RSA, Uri, Task> body)
    {
        using var jwks = new FakeJwksServer();
        var key = jwks.AddKey("kid1");
        var (app, baseAddress) = await StartHostAsync(jwks, configureAuthz);
        try
        {
            await body(key, baseAddress);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static string Token(System.Security.Cryptography.RSA key, IDictionary<string, object> claims)
        => FakeJwksServer.CreateToken(key, "kid1", Issuer, Audience, extraClaims: claims);

    // ---- RequireRole ----------------------------------------------------------------------------

    [Fact]
    public async Task RequireRole_NoToken_IsUnauthorized()
    {
        await RunWithHostAsync(
            asp => asp.RequireRole("admin"),
            async (_, baseAddress) => Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(baseAddress, null)));
    }

    [Fact]
    public async Task RequireRole_AuthenticatedButMissingRole_IsForbidden()
    {
        await RunWithHostAsync(
            asp => asp.RequireRole("admin"),
            async (key, baseAddress) =>
            {
                var token = Token(key, new Dictionary<string, object> { ["roles"] = "reader" });
                Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(baseAddress, token));
            });
    }

    [Fact]
    public async Task RequireRole_WithMatchingRoleClaim_PassesThrough()
    {
        await RunWithHostAsync(
            asp => asp.RequireRole("admin"),
            async (key, baseAddress) =>
            {
                var token = Token(key, new Dictionary<string, object> { ["roles"] = "admin" });
                Assert.Equal(HttpStatusCode.OK, await SendAsync(baseAddress, token));
            });
    }

    [Fact]
    public async Task RequireRole_WithRolesAsJsonArray_PassesThrough()
    {
        await RunWithHostAsync(
            asp => asp.RequireRole("admin"),
            async (key, baseAddress) =>
            {
                // Azure AD app-roles shape: a single "roles" claim whose value is a JSON array.
                var token = Token(key, new Dictionary<string, object> { ["roles"] = "[\"reader\",\"admin\"]" });
                Assert.Equal(HttpStatusCode.OK, await SendAsync(baseAddress, token));
            });
    }

    // ---- RequirePolicy --------------------------------------------------------------------------

    [Fact]
    public async Task RequirePolicy_Inline_SatisfiedByClaim_PassesThrough_OtherwiseForbidden()
    {
        Action<IMiddlewarePipelineBuilder<AspNetContext>> authz = asp =>
            asp.RequirePolicy("employees-only",
                principal => Task.FromResult(principal.HasClaim(c => c.Type == "department" && c.Value == "eng")));

        await RunWithHostAsync(authz, async (key, baseAddress) =>
        {
            var good = Token(key, new Dictionary<string, object> { ["department"] = "eng" });
            var bad = Token(key, new Dictionary<string, object> { ["department"] = "sales" });

            Assert.Equal(HttpStatusCode.OK, await SendAsync(baseAddress, good));
            Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(baseAddress, bad));
        });
    }

    [Fact]
    public async Task RequirePolicy_ByName_ResolvesRegisteredPolicy()
    {
        Action<IMiddlewarePipelineBuilder<AspNetContext>> authz = asp =>
        {
            asp.Register(x => x.AddAuthorizationPolicy("employees-only",
                principal => principal.HasClaim(c => c.Type == "department" && c.Value == "eng")));
            asp.RequirePolicy("employees-only");
        };

        await RunWithHostAsync(authz, async (key, baseAddress) =>
        {
            var good = Token(key, new Dictionary<string, object> { ["department"] = "eng" });
            var bad = Token(key, new Dictionary<string, object> { ["department"] = "sales" });

            Assert.Equal(HttpStatusCode.OK, await SendAsync(baseAddress, good));
            Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(baseAddress, bad));
        });
    }

    [Fact]
    public async Task RequirePolicy_NoToken_IsUnauthorized()
    {
        await RunWithHostAsync(
            asp => asp.RequirePolicy("always", _ => Task.FromResult(true)),
            async (_, baseAddress) => Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(baseAddress, null)));
    }

    [Fact]
    public async Task RequirePolicy_ByName_MissingPolicy_Throws500OnEveryRequest()
    {
        // #179: the "not registered" wiring error is a genuine misconfiguration, not something a
        // caller can trigger or avoid - it must keep surfacing (as a 500, since nothing catches it),
        // on every request, not just the first, so an operator watching error logs after a bad deploy
        // sees it consistently rather than it going quiet after the first hit.
        await RunWithHostAsync(
            asp => asp.RequirePolicy("never-registered"),
            async (key, baseAddress) =>
            {
                var token = Token(key, new Dictionary<string, object>());
                Assert.Equal(HttpStatusCode.InternalServerError, await SendAsync(baseAddress, token));
                Assert.Equal(HttpStatusCode.InternalServerError, await SendAsync(baseAddress, token));
            });
    }

    [Fact]
    public void RequirePolicy_ByName_ResolvesRegisteredPolicyOnceAndReusesIt()
    {
        // #179: the DI lookup + Name-matching LINQ scan used to run inside the per-request delegate on
        // every single request. This drives the pipeline directly (no Kestrel host needed - the
        // middleware's actual per-invocation behavior is what's under test) through several real,
        // policy-satisfying invocations, and asserts the counting policy's Name getter (read once per
        // candidate during the by-name lookup - see CountingNameAuthorizationPolicy below) is touched
        // exactly once overall. Deliberately every invocation is one the policy is satisfied by, not a
        // mix with a failing one: an unsatisfied request legitimately reads the resolved policy's Name
        // again to build its Forbidden detail message ($"Policy '{policy.Name}' not satisfied" in
        // PolicyMiddleware) - a real, separate cost this fix was never meant to touch, which would
        // make a mixed probe measure the wrong thing.
        var lookupCount = 0;
        var inner = new DelegateAuthorizationPolicy("employees-only", (ClaimsPrincipal _) => Task.FromResult(true));
        var counting = new CountingNameAuthorizationPolicy(inner, () => lookupCount++);

        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);
        var app = new MiddlewarePipelineBuilder<object>(container);
        app.Register(x => x.AddAuthorizationPolicy(counting));
        app.RequirePolicy("employees-only");
        var pipeline = app.Build();

        using var factory = new MicrosoftServiceResolverFactory(services);
        var authenticated = new ClaimsPrincipal(new ClaimsIdentity("test"));

        for (var i = 0; i < 3; i++)
        {
            using var scope = factory.CreateScope();
            scope.GetService<AuthenticationHolder>().Principal = authenticated;
            pipeline.HandleAsync(new object(), scope).GetAwaiter().GetResult();
        }

        // The by-name lookup (which reads every candidate's Name to find the match) ran exactly once
        // across all three invocations above - the fix caches the resolved policy instance after the
        // first successful lookup instead of repeating GetServices<>().FirstOrDefault() every time.
        Assert.Equal(1, lookupCount);
    }

    /// <summary>Wraps an <see cref="IAuthorizationPolicy"/> and counts every read of <see cref="Name"/>
    /// - the by-name resolution in <c>RequirePolicy(string)</c> reads <c>Name</c> once per candidate per
    /// lookup attempt, so this is a direct probe of how many times that lookup actually ran.</summary>
    private class CountingNameAuthorizationPolicy : IAuthorizationPolicy
    {
        private readonly IAuthorizationPolicy _inner;
        private readonly Action _onNameRead;

        public CountingNameAuthorizationPolicy(IAuthorizationPolicy inner, Action onNameRead)
        {
            _inner = inner;
            _onNameRead = onNameRead;
        }

        public string Name
        {
            get
            {
                _onNameRead();
                return _inner.Name;
            }
        }

        public Task<bool> IsSatisfiedAsync(ClaimsPrincipal principal) => _inner.IsSatisfiedAsync(principal);
    }

    // ---- RequireAuthorization (resource-based) --------------------------------------------------

    private record OrderResource(string Tenant);

    private class SameTenantAuthorizationHandler : IAuthorizationHandler<OrderResource>
    {
        public Task<bool> IsAuthorizedAsync(ClaimsPrincipal principal, OrderResource resource)
            => Task.FromResult(principal.HasClaim(c => c.Type == "tenant" && c.Value == resource.Tenant));
    }

    [Fact]
    public async Task RequireAuthorization_CallerInResourceTenant_PassesThrough_OtherwiseForbidden()
    {
        Action<IMiddlewarePipelineBuilder<AspNetContext>> authz = asp =>
        {
            asp.Register(x => x.AddScoped<IAuthorizationHandler<OrderResource>, SameTenantAuthorizationHandler>());
            asp.RequireAuthorization<AspNetContext, OrderResource>(_ => new OrderResource("acme"));
        };

        await RunWithHostAsync(authz, async (key, baseAddress) =>
        {
            var good = Token(key, new Dictionary<string, object> { ["tenant"] = "acme" });
            var bad = Token(key, new Dictionary<string, object> { ["tenant"] = "globex" });

            Assert.Equal(HttpStatusCode.OK, await SendAsync(baseAddress, good));
            Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(baseAddress, bad));
        });
    }
}
