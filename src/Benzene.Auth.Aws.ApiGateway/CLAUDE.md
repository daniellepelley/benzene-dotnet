# Benzene.Auth.Aws.ApiGateway

## What this package does
The caller's identity from an AWS API Gateway authorizer (FR-4 of the adopter feature requests).
`UseApiGatewayAuthorizerIdentity(...)` reads the event's `requestContext.authorizer` and sets
`Benzene.Auth.Core`'s `AuthenticationHolder.Principal`, so `RequireScope`, `RequireRole`,
`RequirePolicy` (and endpoint authorization) work without validating the token again in the function.

## Key types
- `Extensions.UseApiGatewayAuthorizerIdentity` - for REST APIs (`ApiGatewayContext`) and HTTP APIs
  (`ApiGatewayV2Context`).
- `ApiGatewayAuthorizerClaims` - the mapping:
  - **v1:** Cognito `claims` are copied as they are; a Lambda authorizer's context values become
    claims (JsonElement numbers and bools as their JSON text); `principalId` becomes `NameIdentifier`.
  - **v2:** JWT `claims` are copied, and `scopes` becomes one space-separated `scope` claim; Lambda
    context values become claims; IAM `userArn`/`callerId`/`accountId` become claims, with
    `NameIdentifier` from the ARN.
  - API Gateway's own `integrationLatency` key is skipped.
- `ApiGatewayAuthorizerIdentityOptions` - `NameClaim` (default `sub`, falling back to `NameIdentifier`),
  `RoleClaims` (e.g. `cognito:groups`; list values split: `a,b`, `[a b]`, JSON arrays),
  `AuthenticationType`.

## Important conventions
- **It trusts API Gateway.** Anything that can invoke the function another way (a function URL, a
  direct invoke) can forge the context. The docs and XML say so; keep it that way.
- No authorizer context means no caller; it never invents one.
- **The first authenticator wins:** a principal already set (e.g. by `UseOAuth2Bearer`) is kept.
- `APIGatewayCustomAuthorizerContext.PrincipalId` is obsolete in the SDK; read `principalId` through
  the dictionary, as `FromV1` does.
- Tests: `test/Benzene.Core.Test/Aws/ApiGateway/ApiGatewayAuthorizerIdentityTest.cs`, built from JSON
  events deserialized with the Lambda serializer Benzene's router uses.
