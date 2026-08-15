using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Benzene.Mesh.Auth.Oidc.Test.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Benzene.Mesh.Auth.Oidc.Test;

public class OidcCallbackMiddlewareTest
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");
    private const string ClientId = "client-id";

    private sealed class Fixture : IDisposable
    {
        public FakeOidcProvider Provider { get; }
        public System.Security.Cryptography.RSA SigningKey { get; }
        public MeshOidcOptions Options { get; }
        public OidcCallbackMiddleware<FakeHttpContext> Middleware { get; }

        public Fixture(string[]? allowedEmails = null)
        {
            Provider = new FakeOidcProvider();
            SigningKey = Provider.AddKey("kid1");

            Options = new MeshOidcOptions
            {
                Issuer = Provider.Issuer,
                ClientId = ClientId,
                ClientSecret = "client-secret",
                SigningKey = Encoding.UTF8.GetString(Key),
                AllowedEmails = allowedEmails ?? new[] { "user@example.com" },
                BasePath = "/mesh/auth",
                RequireHttpsMetadata = false,
            };

            var configurationManager = OidcConfigurationManagerFactory.Create(Options);
            var validationParameters = new TokenValidationParameters
            {
                ValidIssuers = Extensions.ValidIssuersFor(Options.Issuer),
                ValidAudiences = new[] { ClientId },
                ValidAlgorithms = Options.ValidAlgorithms,
                ClockSkew = TimeSpan.FromMinutes(2),
                ConfigurationManager = configurationManager,
                ValidateIssuerSigningKey = true,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
            };
            var idTokenValidator = new OidcIdTokenValidator(new JsonWebTokenHandler(), validationParameters);
            var tokenExchangeClient = new OidcTokenExchangeClient(new HttpClient());

            Middleware = new OidcCallbackMiddleware<FakeHttpContext>(
                Options, Key, configurationManager, tokenExchangeClient, idTokenValidator,
                new FakeHttpRequestAdapter(), new FakeResponseAdapter(), new FakeQueryStringReader(),
                NullLogger.Instance);
        }

        public string MintIdToken(string email, bool emailVerified = true) =>
            FakeOidcProvider.CreateToken(SigningKey, "kid1", Provider.Issuer, ClientId, extraClaims: new Dictionary<string, object>
            {
                ["email"] = email,
                ["email_verified"] = emailVerified,
            });

        public void Dispose() => Provider.Dispose();
    }

    private static FakeHttpContext CallbackContext(string basePath = "/mesh/auth") => new()
    {
        Method = "GET",
        Path = basePath + "/callback",
        Headers = { ["host"] = "mesh.example.com" },
    };

    [Fact]
    public async Task NonMatchingPath_CallsNext()
    {
        using var fixture = new Fixture();
        var context = new FakeHttpContext { Method = "GET", Path = "/mesh-ui" };

        var nextCalled = false;
        await fixture.Middleware.HandleAsync(context, () => { nextCalled = true; return Task.CompletedTask; });

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task MissingState_DeniesWithoutSession()
    {
        using var fixture = new Fixture();
        var context = CallbackContext();
        context.QueryParameters["code"] = "some-code";

        await fixture.Middleware.HandleAsync(context, () => Task.CompletedTask);

        Assert.Equal(401, context.StatusCode);
        Assert.DoesNotContain(context.SetCookies, c => c.StartsWith("benzene_mesh_session="));
    }

    [Fact]
    public async Task StateMismatch_BetweenQueryAndCookie_Denies()
    {
        using var fixture = new Fixture();
        var stateA = OidcStateToken.Create(Key, "/mesh-ui");
        var stateB = OidcStateToken.Create(Key, "/mesh-ui");
        var context = CallbackContext();
        context.QueryParameters["state"] = stateA;
        context.QueryParameters["code"] = "some-code";
        context.Headers["cookie"] = $"benzene_mesh_oidc_state={stateB}";

        await fixture.Middleware.HandleAsync(context, () => Task.CompletedTask);

        Assert.Equal(401, context.StatusCode);
    }

    [Fact]
    public async Task ValidState_ButMissingCode_Denies()
    {
        using var fixture = new Fixture();
        var state = OidcStateToken.Create(Key, "/mesh-ui");
        var context = CallbackContext();
        context.QueryParameters["state"] = state;
        context.Headers["cookie"] = $"benzene_mesh_oidc_state={state}";

        await fixture.Middleware.HandleAsync(context, () => Task.CompletedTask);

        Assert.Equal(401, context.StatusCode);
    }

    [Fact]
    public async Task TokenExchangeFailure_Denies()
    {
        using var fixture = new Fixture();
        var state = OidcStateToken.Create(Key, "/mesh-ui");
        fixture.Provider.RegisterTokenError("bad-code", 400);
        var context = CallbackContext();
        context.QueryParameters["state"] = state;
        context.QueryParameters["code"] = "bad-code";
        context.Headers["cookie"] = $"benzene_mesh_oidc_state={state}";

        await fixture.Middleware.HandleAsync(context, () => Task.CompletedTask);

        Assert.Equal(401, context.StatusCode);
    }

    [Fact]
    public async Task EmailNotVerified_Denies()
    {
        using var fixture = new Fixture();
        var idToken = fixture.MintIdToken("user@example.com", emailVerified: false);
        fixture.Provider.RegisterTokenResponse("good-code", idToken);
        var state = OidcStateToken.Create(Key, "/mesh-ui");
        var context = CallbackContext();
        context.QueryParameters["state"] = state;
        context.QueryParameters["code"] = "good-code";
        context.Headers["cookie"] = $"benzene_mesh_oidc_state={state}";

        await fixture.Middleware.HandleAsync(context, () => Task.CompletedTask);

        Assert.Equal(401, context.StatusCode);
        Assert.DoesNotContain(context.SetCookies, c => c.StartsWith("benzene_mesh_session="));
    }

    [Fact]
    public async Task EmailNotOnAllowlist_Denies()
    {
        using var fixture = new Fixture(allowedEmails: new[] { "someone-else@example.com" });
        var idToken = fixture.MintIdToken("user@example.com");
        fixture.Provider.RegisterTokenResponse("good-code", idToken);
        var state = OidcStateToken.Create(Key, "/mesh-ui");
        var context = CallbackContext();
        context.QueryParameters["state"] = state;
        context.QueryParameters["code"] = "good-code";
        context.Headers["cookie"] = $"benzene_mesh_oidc_state={state}";

        await fixture.Middleware.HandleAsync(context, () => Task.CompletedTask);

        Assert.Equal(401, context.StatusCode);
        Assert.DoesNotContain(context.SetCookies, c => c.StartsWith("benzene_mesh_session="));
    }

    [Fact]
    public async Task FullSuccessfulLogin_IssuesSessionAndRedirectsToReturnTo()
    {
        using var fixture = new Fixture();
        var idToken = fixture.MintIdToken("user@example.com");
        fixture.Provider.RegisterTokenResponse("good-code", idToken);
        var state = OidcStateToken.Create(Key, "/mesh-ui?tab=catalog");
        var context = CallbackContext();
        context.QueryParameters["state"] = state;
        context.QueryParameters["code"] = "good-code";
        context.Headers["cookie"] = $"benzene_mesh_oidc_state={state}";

        await fixture.Middleware.HandleAsync(context, () => Task.CompletedTask);

        Assert.Equal(302, context.StatusCode);
        Assert.Equal("/mesh-ui?tab=catalog", context.Location);

        var sessionCookie = context.SetCookies.Single(c => c.StartsWith("benzene_mesh_session="));
        var sessionValue = sessionCookie.Split(';')[0].Split('=')[1];
        var ok = OidcSessionToken.TryValidate(Key, sessionValue, out var email);
        Assert.True(ok);
        Assert.Equal("user@example.com", email);
    }

    [Fact]
    public async Task DeniedCallback_ClearsStateCookie()
    {
        using var fixture = new Fixture();
        var context = CallbackContext();
        // No state/code at all - immediate deny.

        await fixture.Middleware.HandleAsync(context, () => Task.CompletedTask);

        var setCookie = Assert.Single(context.SetCookies);
        Assert.StartsWith("benzene_mesh_oidc_state=;", setCookie);
        Assert.Contains("Max-Age=0", setCookie);
    }
}
