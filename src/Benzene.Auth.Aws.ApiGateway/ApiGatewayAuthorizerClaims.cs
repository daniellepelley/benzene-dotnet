using System.Security.Claims;
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;

namespace Benzene.Auth.Aws.ApiGateway;

/// <summary>
/// Builds the caller's <see cref="ClaimsPrincipal"/> from an API Gateway event's authorizer context, or
/// returns <c>null</c> when the event carries none (no authorizer on the route, or a direct invoke).
/// </summary>
/// <remarks>
/// <para><b>REST API (payload 1.0), <c>requestContext.authorizer</c>:</b> a Cognito user-pool
/// authorizer's <c>claims</c> become claims of the same type; a Lambda authorizer's context values
/// become claims (numbers and booleans as their JSON text); <c>principalId</c> becomes
/// <see cref="ClaimTypes.NameIdentifier"/>.</para>
/// <para><b>HTTP API (payload 2.0), <c>requestContext.authorizer</c>:</b> a JWT authorizer's claims
/// become claims, and its <c>scopes</c> one space-separated <c>scope</c> claim (which
/// <c>ScopeClaims</c>/<c>RequireScope</c> read); a Lambda authorizer's context values become claims;
/// an IAM authorizer's user ARN becomes <see cref="ClaimTypes.NameIdentifier"/>, with
/// <c>userArn</c>/<c>callerId</c>/<c>accountId</c> claims.</para>
/// </remarks>
public static class ApiGatewayAuthorizerClaims
{
    private const string ClaimsKey = "claims";
    private const string PrincipalIdKey = "principalId";

    // Keys API Gateway itself adds to a Lambda authorizer's context: not identity.
    private static readonly HashSet<string> NotIdentity = new(StringComparer.OrdinalIgnoreCase) { "integrationLatency" };

    /// <summary>The caller from a REST API (payload 1.0) event, or <c>null</c> if it has no authorizer context.</summary>
    public static ClaimsPrincipal? FromV1(APIGatewayProxyRequest request, ApiGatewayAuthorizerIdentityOptions options)
    {
        var authorizer = request.RequestContext?.Authorizer;
        if (authorizer == null || authorizer.Count == 0)
        {
            return null;
        }

        var claims = new List<Claim>();
        if (authorizer.Claims is { Count: > 0 } cognitoClaims)
        {
            foreach (var (type, value) in cognitoClaims)
            {
                claims.Add(new Claim(type, value ?? string.Empty));
            }
        }

        foreach (var (key, value) in authorizer)
        {
            if (string.Equals(key, ClaimsKey, StringComparison.OrdinalIgnoreCase) || NotIdentity.Contains(key))
            {
                continue;
            }

            var text = Text(value);
            if (text == null)
            {
                continue;
            }

            claims.Add(string.Equals(key, PrincipalIdKey, StringComparison.OrdinalIgnoreCase)
                ? new Claim(ClaimTypes.NameIdentifier, text)
                : new Claim(key, text));
        }

        return Build(claims, options);
    }

    /// <summary>The caller from an HTTP API (payload 2.0) event, or <c>null</c> if it has no authorizer context.</summary>
    public static ClaimsPrincipal? FromV2(APIGatewayHttpApiV2ProxyRequest request, ApiGatewayAuthorizerIdentityOptions options)
    {
        var authorizer = request.RequestContext?.Authorizer;
        if (authorizer == null)
        {
            return null;
        }

        var claims = new List<Claim>();
        if (authorizer.Jwt != null)
        {
            foreach (var (type, value) in authorizer.Jwt.Claims ?? new Dictionary<string, string>())
            {
                claims.Add(new Claim(type, value ?? string.Empty));
            }

            if (authorizer.Jwt.Scopes is { Length: > 0 } scopes)
            {
                claims.Add(new Claim("scope", string.Join(' ', scopes)));
            }
        }

        if (authorizer.Lambda != null)
        {
            foreach (var (key, value) in authorizer.Lambda)
            {
                var text = Text(value);
                if (text != null && !NotIdentity.Contains(key))
                {
                    claims.Add(new Claim(key, text));
                }
            }
        }

        if (authorizer.IAM is { } iam)
        {
            AddIfPresent(claims, ClaimTypes.NameIdentifier, iam.UserARN ?? iam.CallerId);
            AddIfPresent(claims, "userArn", iam.UserARN);
            AddIfPresent(claims, "callerId", iam.CallerId);
            AddIfPresent(claims, "accountId", iam.AccountId);
        }

        return Build(claims, options);
    }

    private static ClaimsPrincipal? Build(List<Claim> claims, ApiGatewayAuthorizerIdentityOptions options)
    {
        if (claims.Count == 0)
        {
            return null;
        }

        foreach (var roleClaim in options.RoleClaims)
        {
            foreach (var claim in claims.Where(c => string.Equals(c.Type, roleClaim, StringComparison.Ordinal)).ToList())
            {
                foreach (var role in SplitList(claim.Value))
                {
                    claims.Add(new Claim(ClaimTypes.Role, role));
                }
            }
        }

        // Name the caller by the configured claim, falling back to the principal ID / IAM ARN.
        var nameType = claims.Any(c => c.Type == options.NameClaim) ? options.NameClaim : ClaimTypes.NameIdentifier;
        var identity = new ClaimsIdentity(claims, options.AuthenticationType, nameType, ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Splits a role list as authorizers deliver it: <c>admin,user</c>, <c>[admin user]</c> (Cognito
    /// groups through an HTTP API JWT authorizer) and a JSON array <c>["admin","user"]</c>.
    /// </summary>
    public static IEnumerable<string> SplitList(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('[') && trimmed.Contains('"'))
        {
            try
            {
                return JsonSerializer.Deserialize<string[]>(trimmed)?.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
                       ?? Array.Empty<string>();
            }
            catch (JsonException)
            {
                // Looks like JSON but isn't: fall through and split it as text.
            }
        }

        return trimmed.Trim('[', ']')
            .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void AddIfPresent(List<Claim> claims, string type, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            claims.Add(new Claim(type, value));
        }
    }

    // The Lambda serializer leaves authorizer context values as JsonElements; a hand-built event may
    // hold plain values.
    private static string? Text(object? value)
    {
        return value switch
        {
            null => null,
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            JsonElement e => e.GetRawText(),
            bool b => b ? "true" : "false",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
        };
    }
}
