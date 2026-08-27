using System;
using System.Text;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Core.Middleware;
using Benzene.Http;
using Benzene.Mesh.Auth.Oidc.Test.Fakes;
using Benzene.Microsoft.Dependencies;
using Xunit;

namespace Benzene.Mesh.Auth.Oidc.Test;

/// <summary>
/// #172: exercises the REAL <see cref="Extensions.UseMeshOidcAuth{TContext}"/> registration against a
/// real Microsoft.Extensions.DependencyInjection container, unlike every other test in this project
/// (which constructs <see cref="OidcSessionGateMiddleware{TContext}"/> directly and so cannot see a DI
/// lifetime mistake at all). <see cref="OidcSessionGateMiddleware{TContext}"/> used to be registered
/// <c>AddSingleton</c> despite taking a SCOPED <see cref="IOidcSessionSink"/> through its constructor -
/// the container resolved that scoped sink exactly once, at whichever scope happened to ask for the
/// middleware first, and pinned that one instance (and the middleware instance itself) for the rest of
/// the container's life. Every later scope's session gate silently attributed its identity to the FIRST
/// scope's sink, not its own.
/// </summary>
public class OidcSessionGateMiddlewareDiScopeTest
{
    private sealed class RecordingSessionSink : IOidcSessionSink
    {
        public string? AuthenticatedEmail { get; private set; }

        public void Authenticated(string email) => AuthenticatedEmail = email;
    }

    [Fact]
    public async Task ResolvingFromTwoDifferentScopes_EachGetsItsOwnMiddlewareAndSessionSinkInstance()
    {
        var signingKey = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");
        var options = new MeshOidcOptions
        {
            Issuer = "https://accounts.google.com",
            ClientId = "client-id",
            ClientSecret = "client-secret",
            SigningKey = Encoding.UTF8.GetString(signingKey),
            AllowedEmails = new[] { "user@example.com" },
            BasePath = "/mesh/auth",
        };

        var container = new MicrosoftBenzeneServiceContainer();
        container.AddSingleton<IHttpRequestAdapter<FakeHttpContext>>(new FakeHttpRequestAdapter());
        container.AddSingleton<IBenzeneResponseAdapter<FakeHttpContext>>(new FakeResponseAdapter());
        container.AddSingleton<IOidcQueryStringReader<FakeHttpContext>>(new FakeQueryStringReader());
        // The scoped dependency at the heart of the bug - see IOidcSessionSink's remarks and
        // Extensions.cs's registration comment for #172.
        container.AddScoped<IOidcSessionSink, RecordingSessionSink>();

        var app = new MiddlewarePipelineBuilder<FakeHttpContext>(container);
        app.UseMeshOidcAuth(options);

        using var factory = container.CreateServiceResolverFactory();
        using var scopeA = factory.CreateScope();
        using var scopeB = factory.CreateScope();

        var middlewareA = scopeA.GetService<OidcSessionGateMiddleware<FakeHttpContext>>();
        var middlewareB = scopeB.GetService<OidcSessionGateMiddleware<FakeHttpContext>>();

        // The regression itself: a singleton registration would resolve the exact SAME middleware
        // instance from both scopes, permanently wired to whichever scope resolved it first.
        Assert.NotSame(middlewareA, middlewareB);

        var sinkA = Assert.IsType<RecordingSessionSink>(scopeA.GetService<IOidcSessionSink>());
        var sinkB = Assert.IsType<RecordingSessionSink>(scopeB.GetService<IOidcSessionSink>());
        Assert.NotSame(sinkA, sinkB);

        var session = OidcSessionToken.Create(signingKey, "user@example.com", TimeSpan.FromHours(24));
        var context = new FakeHttpContext
        {
            Method = "GET",
            Path = "/mesh-ui",
            Headers = { ["cookie"] = $"benzene_mesh_session={session}" },
        };

        await middlewareA.HandleAsync(context, () => Task.CompletedTask);

        // The behavioural proof, not just object identity: scope A's middleware authenticated into
        // scope A's OWN sink, and scope B's sink was never touched. Under the old bug, every scope's
        // middleware was the SAME object wired to whichever sink got captured first - this would either
        // silently write into the wrong scope's sink, or (in the real host) into a MeshDispatchIdentity
        // belonging to an unrelated, already-completed request.
        Assert.Equal("user@example.com", sinkA.AuthenticatedEmail);
        Assert.Null(sinkB.AuthenticatedEmail);
    }
}
