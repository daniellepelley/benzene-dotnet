using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Benzene.Abstractions;
using Benzene.Auth.Aws.ApiGateway;
using Benzene.Auth.Core;
using Benzene.Auth.Core.Endpoints;
using Benzene.Aws.Lambda.ApiGateway;
using Benzene.Aws.Lambda.ApiGateway.TestHelpers;
using Benzene.Aws.Lambda.Core;
using Benzene.Aws.Lambda.Core.TestHelpers;
using Benzene.Core.MessageHandlers;
using Benzene.Http.BenzeneMessage;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Examples;
using Benzene.Testing;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Benzene.Test.Http;

// FR-5 (adopter feature requests): per-endpoint authorization from handler attributes, checked before the
// handler is chosen and before the body is read. The caller comes from an API Gateway authorizer (FR-4).
public class EndpointAuthorizationTest
{
    private static AwsLambdaBenzeneTestHost Host(Action<EndpointAuthorizationOptions> configure = null)
    {
        return new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services
                .ConfigureServiceCollection()
                .UsingBenzene(x => x.AddAuthorizationPolicy("same-tenant", p => p.FindFirst("tenant")?.Value == "t1")))
            .Configure(app => app.UseApiGateway(apiGateway => apiGateway
                .UseApiGatewayAuthorizerIdentity(o => o.RoleClaims.Add("cognito:groups"))
                .UseEndpointAuthorization(configure)
                .UseMessageHandlers()))
            .BuildHost();
    }

    // A REST API event; with scope set, it carries a Cognito authorizer context with that scope.
    private static JObject Request(string method, string path, string scope = null, string groups = null,
        string tenant = null, string body = "{}")
    {
        var request = new JObject
        {
            ["httpMethod"] = method,
            ["path"] = path,
            ["headers"] = new JObject { ["x-correlation-id"] = Guid.NewGuid().ToString(), ["content-type"] = "application/json" },
            ["body"] = body,
            ["requestContext"] = new JObject()
        };
        if (scope != null)
        {
            var claims = new JObject { ["sub"] = "user-1", ["scope"] = scope };
            if (groups != null)
            {
                claims["cognito:groups"] = groups;
            }

            if (tenant != null)
            {
                claims["tenant"] = tenant;
            }

            request["requestContext"]!["authorizer"] = new JObject { ["claims"] = claims };
        }

        return request;
    }

    private static async Task<int> Status(AwsLambdaBenzeneTestHost host, JObject request)
    {
        return (await host.SendEventAsync<APIGatewayProxyResponse>(request)).StatusCode;
    }

    [Fact]
    public async Task EachHandlerIsCheckedAgainstItsOwnScope()
    {
        var host = Host();

        Assert.Equal(200, await Status(host, Request("GET", Defaults.AuthReadPath, scope: "tasks.read")));
        Assert.Equal(403, await Status(host, Request("POST", Defaults.AuthWritePath, scope: "tasks.read")));
        Assert.Equal(200, await Status(host, Request("POST", Defaults.AuthWritePath, scope: "tasks.write")));
    }

    [Fact]
    public async Task AnyOfTheListedScopesIsEnough()
    {
        Assert.Equal(200, await Status(Host(), Request("GET", Defaults.AuthReadPath, scope: "tasks.admin")));
    }

    [Fact]
    public async Task NoCaller_Is401()
    {
        Assert.Equal(401, await Status(Host(), Request("GET", Defaults.AuthReadPath)));
    }

    [Fact]
    public async Task AllowAnonymous_PassesWithNoCaller()
    {
        Assert.Equal(200, await Status(Host(), Request("GET", Defaults.AuthOpenPath)));
    }

    [Fact]
    public async Task AHandlerWithNoAttribute_NeedsACallerByDefault()
    {
        var host = Host();

        Assert.Equal(401, await Status(host, Request("GET", Defaults.AuthPlainPath)));
        Assert.Equal(200, await Status(host, Request("GET", Defaults.AuthPlainPath, scope: "anything")));
    }

    [Fact]
    public async Task WithTheDefaultOff_AHandlerWithNoAttributeIsOpen()
    {
        Assert.Equal(200, await Status(Host(o => o.RequireAuthenticatedByDefault = false), Request("GET", Defaults.AuthPlainPath)));
    }

    [Fact]
    public async Task EveryAttributeApplies()
    {
        var host = Host();

        Assert.Equal(403, await Status(host, Request("GET", Defaults.AuthAdminPath, scope: "tasks.read")));
        Assert.Equal(200, await Status(host, Request("GET", Defaults.AuthAdminPath, scope: "tasks.read", groups: "admin,user")));
    }

    [Fact]
    public async Task ANamedPolicy()
    {
        var host = Host();

        Assert.Equal(403, await Status(host, Request("GET", Defaults.AuthPolicyPath, scope: "x", tenant: "t2")));
        Assert.Equal(200, await Status(host, Request("GET", Defaults.AuthPolicyPath, scope: "x", tenant: "t1")));
    }

    [Fact]
    public async Task AMalformedBodyToAProtectedEndpoint_Is401NotBadRequest()
    {
        Assert.Equal(401, await Status(Host(), Request("POST", Defaults.AuthWritePath, body: "{not json")));
    }

    [Fact]
    public async Task AnUnknownRoute_StillAnswers404()
    {
        Assert.Equal(404, await Status(Host(), Request("GET", "/no-such-route")));
    }

    [Fact]
    public async Task ForTopic_AddsToTheHandlersRequirements()
    {
        var host = Host(o => o.ForTopic("auth:open").RequireScope("never-granted"));

        // [AllowAnonymous] on the handler still wins.
        Assert.Equal(200, await Status(host, Request("GET", Defaults.AuthOpenPath)));

        var strict = Host(o => o.ForTopic("auth:read").RequireRole("admin"));
        Assert.Equal(403, await Status(strict, Request("GET", Defaults.AuthReadPath, scope: "tasks.read")));
    }

    [Fact]
    public void ReservedTopicsAreAnonymousByDefault()
    {
        var options = new EndpointAuthorizationOptions();

        Assert.Contains(BenzeneTopic.Spec, options.AnonymousTopics);
        Assert.Contains(BenzeneTopic.Ping, options.AnonymousTopics);
        Assert.Contains(BenzeneTopic.HealthCheck, options.AnonymousTopics);
        Assert.Contains(BenzeneTopic.Liveness, options.AnonymousTopics);
        Assert.Contains(BenzeneTopic.Readiness, options.AnonymousTopics);
    }

    [Fact]
    public void RequirementsAreReadFromAttributes()
    {
        var requirements = EndpointRequirements.For(typeof(AuthAdminHandler));

        Assert.False(requirements.AllowAnonymous);
        Assert.Equal(new[] { "tasks.read" }, Assert.Single(requirements.Scopes));
        Assert.Equal(new[] { "admin" }, Assert.Single(requirements.Roles));
        Assert.True(EndpointRequirements.For(typeof(AuthOpenHandler)).AllowAnonymous);
        Assert.True(EndpointRequirements.For(typeof(AuthPlainHandler)).IsEmpty);
    }

    [Fact]
    public async Task OverTheEnvelope_TheSameRequirementsApply()
    {
        // The envelope pipeline has no authenticator here, so there is no caller.
        var host = new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app.UseApiGateway(apiGateway => apiGateway
                .UseBenzeneMessage(new BenzeneMessageHttpOptions(), messageApp => messageApp
                    .UseEndpointAuthorization()
                    .UseMessageHandlers())))
            .BuildHost();

        var protectedTopic = await host.SendApiGatewayAsync(HttpBuilder.Create("POST", "/benzene/invoke",
            new { topic = "auth:read", headers = new Dictionary<string, string>(), body = "{}" }));
        var openTopic = await host.SendApiGatewayAsync(HttpBuilder.Create("POST", "/benzene/invoke",
            new { topic = "auth:open", headers = new Dictionary<string, string>(), body = "{}" }));

        Assert.Contains("\"statusCode\":\"unauthorized\"", protectedTopic.Body);
        Assert.Contains("\"statusCode\":\"ok\"", openTopic.Body);
    }
}
