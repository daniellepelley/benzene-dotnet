using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Serialization.SystemTextJson;
using Benzene.Auth.Aws.ApiGateway;
using Benzene.Auth.Core;
using Benzene.Auth.OAuth2;
using Benzene.Aws.Lambda.ApiGateway;
using Benzene.Aws.Lambda.ApiGateway.TestHelpers;
using Benzene.Aws.Lambda.Core;
using Benzene.Aws.Lambda.Core.TestHelpers;
using Benzene.Core.MessageHandlers;
using Benzene.Test.Examples;
using Xunit;

namespace Benzene.Test.Aws.ApiGateway;

// FR-4 (adopter feature requests): the caller from an API Gateway authorizer's context. The events are
// JSON deserialized with the same DefaultLambdaJsonSerializer Benzene's Lambda router uses, so the
// authorizer context has the shapes a real invocation gives it (JsonElement values on v1).
public class ApiGatewayAuthorizerIdentityTest
{
    private static readonly DefaultLambdaJsonSerializer Serializer = new();

    private static T Event<T>(string json)
    {
        return Serializer.Deserialize<T>(new MemoryStream(Encoding.UTF8.GetBytes(json)));
    }

    // The test host re-serializes an event object with Newtonsoft.Json, which can't write the
    // JsonElement values a v1 authorizer context holds after deserialization. A real invocation
    // delivers the JSON itself; sending it as a JObject reproduces that exactly.
    private static Newtonsoft.Json.Linq.JObject Raw(string json)
    {
        return Newtonsoft.Json.Linq.JObject.Parse(json);
    }

    private const string V1Cognito = """
        {"httpMethod":"GET","path":"/header-echo","headers":{"x-correlation-id":"c"},
         "requestContext":{"authorizer":{"claims":{"sub":"user-1","email":"a@example.com",
           "cognito:groups":"admin,user","scope":"tasks.read"}}}}
        """;

    private const string V1Lambda = """
        {"httpMethod":"GET","path":"/header-echo","headers":{"x-correlation-id":"c"},
         "requestContext":{"authorizer":{"principalId":"user-2","tenant":"t1","count":3,"flag":true,
           "integrationLatency":12}}}
        """;

    private const string V1NoAuthorizer = """
        {"httpMethod":"GET","path":"/header-echo","headers":{"x-correlation-id":"c"},"requestContext":{}}
        """;

    private const string V2Jwt = """
        {"version":"2.0","rawPath":"/header-echo","headers":{"x-correlation-id":"c"},
         "requestContext":{"http":{"method":"GET","path":"/header-echo"},
           "authorizer":{"jwt":{"claims":{"sub":"user-3","cognito:groups":"[admin user]"},
             "scopes":["tasks.read","tasks.write"]}}}}
        """;

    private const string V2Lambda = """
        {"version":"2.0","rawPath":"/header-echo","headers":{"x-correlation-id":"c"},
         "requestContext":{"http":{"method":"GET","path":"/header-echo"},
           "authorizer":{"lambda":{"sub":"user-4","tenant":"t2"}}}}
        """;

    private const string V2Iam = """
        {"version":"2.0","rawPath":"/header-echo","headers":{"x-correlation-id":"c"},
         "requestContext":{"http":{"method":"GET","path":"/header-echo"},
           "authorizer":{"iam":{"userArn":"arn:aws:iam::111122223333:user/u","callerId":"AIDA1","accountId":"111122223333"}}}}
        """;

    private static ApiGatewayAuthorizerIdentityOptions Cognito()
    {
        var options = new ApiGatewayAuthorizerIdentityOptions();
        options.RoleClaims.Add("cognito:groups");
        return options;
    }

    [Fact]
    public void V1_Cognito_ClaimsNameRolesAndScopes()
    {
        var principal = ApiGatewayAuthorizerClaims.FromV1(Event<APIGatewayProxyRequest>(V1Cognito), Cognito());

        Assert.NotNull(principal);
        Assert.True(principal.Identity!.IsAuthenticated);
        Assert.Equal("user-1", principal.Identity.Name);
        Assert.Equal("a@example.com", principal.FindFirst("email")!.Value);
        Assert.True(principal.IsInRole("admin"));
        Assert.True(principal.IsInRole("user"));
        Assert.Contains("tasks.read", ScopeClaims.GetGrantedScopes(principal));
    }

    [Fact]
    public void V1_LambdaAuthorizer_ContextValuesAndPrincipalId()
    {
        var principal = ApiGatewayAuthorizerClaims.FromV1(Event<APIGatewayProxyRequest>(V1Lambda), new ApiGatewayAuthorizerIdentityOptions());

        Assert.NotNull(principal);
        Assert.Equal("user-2", principal.Identity!.Name);
        Assert.Equal("user-2", principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        Assert.Equal("t1", principal.FindFirst("tenant")!.Value);
        Assert.Equal("3", principal.FindFirst("count")!.Value);
        Assert.Equal("true", principal.FindFirst("flag")!.Value);
        Assert.Null(principal.FindFirst("integrationLatency"));
    }

    [Fact]
    public void V1_NoAuthorizerContext_IsNoCaller()
    {
        Assert.Null(ApiGatewayAuthorizerClaims.FromV1(Event<APIGatewayProxyRequest>(V1NoAuthorizer), new ApiGatewayAuthorizerIdentityOptions()));
    }

    [Fact]
    public void V2_Jwt_ClaimsScopesAndBracketedGroups()
    {
        var principal = ApiGatewayAuthorizerClaims.FromV2(Event<APIGatewayHttpApiV2ProxyRequest>(V2Jwt), Cognito());

        Assert.NotNull(principal);
        Assert.Equal("user-3", principal.Identity!.Name);
        Assert.True(principal.IsInRole("admin"));
        Assert.True(principal.IsInRole("user"));
        var scopes = ScopeClaims.GetGrantedScopes(principal);
        Assert.Contains("tasks.read", scopes);
        Assert.Contains("tasks.write", scopes);
    }

    [Fact]
    public void V2_LambdaAuthorizer_ContextValues()
    {
        var principal = ApiGatewayAuthorizerClaims.FromV2(Event<APIGatewayHttpApiV2ProxyRequest>(V2Lambda), new ApiGatewayAuthorizerIdentityOptions());

        Assert.Equal("user-4", principal!.Identity!.Name);
        Assert.Equal("t2", principal.FindFirst("tenant")!.Value);
    }

    [Fact]
    public void V2_Iam_NamedByTheUserArn()
    {
        var principal = ApiGatewayAuthorizerClaims.FromV2(Event<APIGatewayHttpApiV2ProxyRequest>(V2Iam), new ApiGatewayAuthorizerIdentityOptions());

        Assert.Equal("arn:aws:iam::111122223333:user/u", principal!.Identity!.Name);
        Assert.Equal("111122223333", principal.FindFirst("accountId")!.Value);
    }

    [Theory]
    [InlineData("admin,user")]
    [InlineData("[admin user]")]
    [InlineData("[\"admin\",\"user\"]")]
    [InlineData(" admin , user ")]
    public void SplitList(string value)
    {
        Assert.Equal(new[] { "admin", "user" }, ApiGatewayAuthorizerClaims.SplitList(value).ToArray());
    }

    private static AwsLambdaBenzeneTestHost V1Host(string scope)
    {
        return new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app.UseApiGateway(apiGateway => apiGateway
                .UseApiGatewayAuthorizerIdentity()
                .RequireScope(scope)
                .UseMessageHandlers()))
            .BuildHost();
    }

    [Fact]
    public async Task ApiGatewayV1_RequireScope_PassesWithTheAuthorizersScope()
    {
        var response = await V1Host("tasks.read").SendEventAsync<APIGatewayProxyResponse>(Raw(V1Cognito));

        Assert.Equal(201, response.StatusCode);
    }

    [Fact]
    public async Task ApiGatewayV1_RequireScope_MissingScope_Is403()
    {
        var response = await V1Host("tasks.write").SendEventAsync<APIGatewayProxyResponse>(Raw(V1Cognito));

        Assert.Equal(403, response.StatusCode);
    }

    [Fact]
    public async Task ApiGatewayV1_NoAuthorizerContext_Is401()
    {
        var response = await V1Host("tasks.read").SendEventAsync<APIGatewayProxyResponse>(Raw(V1NoAuthorizer));

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task ApiGatewayV2_RequireScope_PassesWithTheJwtScopes()
    {
        var host = new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app.UseApiGatewayV2(apiGateway => apiGateway
                .UseApiGatewayAuthorizerIdentity()
                .RequireScope("tasks.write")
                .UseMessageHandlers()))
            .BuildHost();

        var response = await host.SendEventAsync<APIGatewayHttpApiV2ProxyResponse>(Raw(V2Jwt));

        Assert.Equal(201, response.StatusCode);
    }

    [Fact]
    public async Task AnExistingCaller_IsKept()
    {
        ClaimsPrincipal seen = null;
        var host = new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app.UseApiGateway(apiGateway => apiGateway
                .Use(resolver => new Benzene.Core.Middleware.FuncWrapperMiddleware<ApiGatewayContext>("first", async (_, next) =>
                {
                    resolver.GetService<AuthenticationHolder>().Principal =
                        new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "from-bearer") }, "Bearer", "sub", ClaimTypes.Role));
                    await next();
                }))
                .UseApiGatewayAuthorizerIdentity()
                .Use(resolver => new Benzene.Core.Middleware.FuncWrapperMiddleware<ApiGatewayContext>("read", async (_, next) =>
                {
                    seen = resolver.GetService<AuthenticationHolder>().Principal;
                    await next();
                }))
                .UseMessageHandlers()))
            .BuildHost();

        await host.SendEventAsync<APIGatewayProxyResponse>(Raw(V1Cognito));

        Assert.Equal("from-bearer", seen.Identity!.Name);
    }
}
