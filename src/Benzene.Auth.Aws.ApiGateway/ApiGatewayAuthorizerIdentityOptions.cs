using System.Security.Claims;

namespace Benzene.Auth.Aws.ApiGateway;

/// <summary>Options for <c>UseApiGatewayAuthorizerIdentity(...)</c>.</summary>
public class ApiGatewayAuthorizerIdentityOptions
{
    /// <summary>
    /// The claim that names the caller (<see cref="ClaimsIdentity.Name"/>). Default <c>sub</c>. When the
    /// authorizer supplies no such claim, the principal ID (a Lambda authorizer's <c>principalId</c>) or
    /// the IAM user ARN names the caller instead.
    /// </summary>
    public string NameClaim { get; set; } = "sub";

    /// <summary>
    /// Claims whose values are the caller's roles, mapped to <see cref="ClaimTypes.Role"/> so
    /// <c>RequireRole</c> and <c>IsInRole</c> see them. Default none; for Cognito, <c>cognito:groups</c>.
    /// A list value is split: <c>admin,user</c>, <c>[admin user]</c> (how HTTP APIs deliver it) and a JSON
    /// array all give two roles.
    /// </summary>
    public IList<string> RoleClaims { get; } = new List<string>();

    /// <summary>The identity's authentication type, which makes it authenticated. Default <c>ApiGatewayAuthorizer</c>.</summary>
    public string AuthenticationType { get; set; } = "ApiGatewayAuthorizer";
}
