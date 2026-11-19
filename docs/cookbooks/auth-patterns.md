# Authentication Patterns

Add OAuth2 bearer token (JWT) validation or HTTP Basic authentication to a Benzene service that has no security-terminating gateway in front of it, plus basic scope-based authorization.

## Problem Statement

Most Benzene services run behind something that already handles authentication — API Gateway with a Cognito/Lambda authorizer, an Azure Front Door with auth rules, a load balancer with OIDC. In that setup Benzene shouldn't duplicate that work, and it doesn't: there's nothing to configure to let API Gateway allow or deny a request. To know *who* the caller is, and check their scopes or roles in the function, see [Using an API Gateway authorizer](#using-an-api-gateway-authorizer).

But not every deployment has that in front of it — a self-hosted worker, a service exposed directly, or a deployment where the gateway's auth story isn't wired up yet. For those, you need authentication at the Benzene level itself, as opt-in middleware you add only where you need it.

This cookbook covers:
- **`Benzene.Auth.OAuth2`** — validating an inbound `Authorization: Bearer <token>` JWT against an identity provider's JWKS endpoint
- **`Benzene.Auth.Basic`** — RFC 7617 username/password authentication
- **`Benzene.Auth.Aws.ApiGateway`** — the caller from an API Gateway authorizer, without validating the token again
- **`RequireScope`** — scope-based authorization (OAuth2/JWT) once a caller is authenticated
- **`RequireRole` / `RequirePolicy` / `RequireAuthorization`** (`Benzene.Auth.Core`) — mechanism-agnostic role, named-policy, and resource-based authorization over any authenticated caller

Benzene provides the authorization *enforcement* primitives and pluggable policy/resource seams,
but no built-in adapter for a specific external policy engine — see
[What This Doesn't Cover](#what-this-doesnt-cover).

## Prerequisites

- A Benzene service hosted behind an HTTP transport (ASP.NET Core, AWS Lambda API Gateway, or Azure Functions HTTP trigger) — every package here targets `TContext : IHttpContext`, so it works on any of them the same way
- For `Benzene.Auth.OAuth2`: an identity provider that exposes either full OIDC discovery (Auth0, Cognito, Azure AD, Okta all do) or a bare JWKS document

## Installation

```bash
dotnet add package Benzene.Auth.OAuth2
# or, for Basic auth
dotnet add package Benzene.Auth.Basic
```

`Benzene.Auth.OAuth2` pulls in `Microsoft.IdentityModel.JsonWebTokens` and
`Microsoft.IdentityModel.Protocols.OpenIdConnect` — the same building blocks
`Microsoft.AspNetCore.Authentication.JwtBearer` uses internally, without the ASP.NET Core coupling,
so the same middleware works identically behind Lambda or Azure Functions. `Benzene.Auth.Basic`
has no third-party dependency.

## OAuth2 Bearer Token Validation

```csharp
using Benzene.Auth.OAuth2;

app.UseOAuth2Bearer(new OAuth2BearerOptions
{
    // Full OIDC discovery - most identity providers expose this. Use JwksUri instead for one
    // that only publishes a bare JWKS document (set exactly one of the two, never both).
    Authority = "https://your-tenant.auth0.com/.well-known/openid-configuration",

    // Required, no default - an empty list would accept tokens from any issuer/audience.
    ValidIssuers = new[] { "https://your-tenant.auth0.com/" },
    ValidAudiences = new[] { "your-api-identifier" },

    // Required, no default - see "Why ValidAlgorithms has no default" below.
    ValidAlgorithms = new[] { "RS256" },
});
```

Requests without a valid bearer token — missing header, malformed, expired, bad signature, wrong
issuer/audience/algorithm — are short-circuited with the `unauthorized` status (mapped to HTTP 401
by every Benzene HTTP transport). A successfully validated token's claims become available to
later middleware and handlers via `Benzene.Auth.Core.AuthenticationHolder.Principal`
(`System.Security.Claims.ClaimsPrincipal`) — no Benzene-specific principal type, just the BCL
shape every .NET auth library already produces.

### Provider-specific examples

**Auth0:**

```csharp
new OAuth2BearerOptions
{
    Authority = "https://your-tenant.auth0.com/.well-known/openid-configuration",
    ValidIssuers = new[] { "https://your-tenant.auth0.com/" },
    ValidAudiences = new[] { "https://your-api-identifier" },
    ValidAlgorithms = new[] { "RS256" },
}
```

**AWS Cognito:**

```csharp
new OAuth2BearerOptions
{
    Authority = "https://cognito-idp.{region}.amazonaws.com/{userPoolId}/.well-known/openid-configuration",
    ValidIssuers = new[] { "https://cognito-idp.{region}.amazonaws.com/{userPoolId}" },
    ValidAudiences = new[] { "{appClientId}" },
    ValidAlgorithms = new[] { "RS256" },
}
```

**Azure AD:**

```csharp
new OAuth2BearerOptions
{
    Authority = "https://login.microsoftonline.com/{tenantId}/v2.0/.well-known/openid-configuration",
    ValidIssuers = new[] { "https://login.microsoftonline.com/{tenantId}/v2.0" },
    ValidAudiences = new[] { "{applicationIdUri or clientId}" },
    ValidAlgorithms = new[] { "RS256" },
}
```

Any OIDC-compliant provider works the same way — point `Authority` at its discovery document and
fill in the issuer/audience/algorithm your provider issues.

### Why `ValidAlgorithms` has no default

Every option above is required with no permissive fallback, but `ValidAlgorithms` specifically
guards against a well-known class of attack: "algorithm confusion" (RFC 8725 §3.1). If a validator
trusted whatever `alg` a token's own header claimed, an attacker could take a service's RSA
*public* key (routinely not secret) and use it as an HMAC *secret* to forge an `HS256`-signed
token the validator would accept as if it were one of the service's real `RS256` tokens. Requiring
an explicit, non-empty allowlist is what closes that off — `UseOAuth2Bearer` throws
`ArgumentException` at pipeline wire-up if `ValidAlgorithms` (or `ValidIssuers`/`ValidAudiences`)
is empty, so a misconfigured pipeline fails at startup, not silently on the first request.

## Basic Authentication

For a simpler gate than full OAuth2 — a single service account, an internal admin surface:

```csharp
using Benzene.Auth.Basic;
using System.Security.Claims;

public class ServiceAccountValidator : IBasicAuthCredentialValidator
{
    public Task<ClaimsPrincipal?> ValidateAsync(string username, string password)
    {
        var expectedPassword = Environment.GetEnvironmentVariable("SERVICE_ACCOUNT_PASSWORD");
        if (username != "service-account" || password != expectedPassword)
        {
            return Task.FromResult<ClaimsPrincipal?>(null);
        }

        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, username) });
        return Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(identity));
    }
}
```

```csharp
app.UseBasicAuth(new ServiceAccountValidator());
```

`Benzene.Auth.Basic` ships no default `IBasicAuthCredentialValidator` implementation on purpose —
hardcoding a credential store in the package would be a footgun waiting to be deployed. Implement
it against whatever your service actually uses: an environment variable for a single service
account (above), a secrets manager, a user table. A missing/malformed `Authorization` header or a
validator returning `null` produces `unauthorized` (401) with a `WWW-Authenticate: Basic
realm="..."` challenge header, per RFC 7617 — this is what makes browsers and HTTP clients that
understand Basic auth actually prompt for credentials.

## Scope-Based Authorization

Once a caller is authenticated via `UseOAuth2Bearer`, require a scope before reaching a route:

```csharp
app.UseOAuth2Bearer(oauth2Options)
   .RequireScope("orders:write");
```

`RequireScope` reads both the `scope` claim (RFC 8693's convention — a single space-delimited
string) and the `scp` claim (Azure AD's convention — a space-delimited string *or* a JSON array,
depending on issuer) and normalizes them into one set, so it works the same regardless of which
convention your identity provider uses. Pass multiple scopes to require any one of them:

```csharp
app.UseOAuth2Bearer(oauth2Options)
   .RequireScope("orders:write", "orders:admin");   // either scope is sufficient
```

Two distinct failure statuses matter here, and `RequireScope` keeps them distinct on purpose:

| Situation | Status | Meaning |
|---|---|---|
| No `Authorization` header at all, or the token failed validation | `unauthorized` (401) | Caller not authenticated |
| A validly authenticated caller, but missing every required scope | `forbidden` (403) | Caller authenticated but not permitted |

Collapsing these into one status would leave an API consumer unable to tell "you're not logged in"
apart from "you're logged in but don't have permission" from the response code alone.

## Authorization: Roles, Policies & Resource Checks

Scopes are an OAuth2/JWT concept, so `RequireScope` lives in `Benzene.Auth.OAuth2`. For
authorization that reads off any authenticated caller — regardless of whether the principal came
from a bearer token, Basic auth, or a custom mechanism — `Benzene.Auth.Core` adds three
mechanism-agnostic checks. Benzene owns the *enforcement* (each short-circuits with `unauthorized`
when there's no caller and `forbidden` when the caller lacks permission, the same distinction
`RequireScope` keeps); *what a role/policy means* stays in your app.

All three compose the same way as `RequireScope`, and on any transport (not just HTTP) whose
pipeline sets a principal.

### Roles — `RequireRole`

```csharp
app.UseOAuth2Bearer(oauth2Options)
   .RequireRole("admin", "operator");   // any one role is sufficient
```

Roles are read from the caller's `ClaimsPrincipal.IsInRole` *and* the common role claim types
(`ClaimTypes.Role`, `role`, `roles`), with the `roles` claim also accepted as a JSON array — Azure
AD's app-roles shape — so it works whether your issuer emits one claim per role or a single array.

### Named or inline policies — `RequirePolicy`

A policy is a named rule over the principal. Define it inline, or register it once and reference it
by name:

```csharp
// Inline (sync or async predicate):
app.RequirePolicy("employees-only",
    principal => principal.HasClaim("department", "engineering"));

// Or register once, reference by name from many pipelines:
services.AddAuthorizationPolicy("mfa-required",
    principal => principal.HasClaim(c => c.Type == "amr" && c.Value == "mfa"));
// ...
app.RequirePolicy("mfa-required");
```

For anything more involved than a predicate, implement `IAuthorizationPolicy` (a `Name` plus
`Task<bool> IsSatisfiedAsync(ClaimsPrincipal)`) and pass the instance to `RequirePolicy` or register
it with `AddAuthorizationPolicy`.

### Resource-based checks — `RequireAuthorization`

When the decision depends on *which* resource is being acted on (ownership, tenant match), implement
`IAuthorizationHandler<TResource>` and map the message to the resource it targets:

```csharp
public class SameTenantHandler : IAuthorizationHandler<OrderResource>
{
    public Task<bool> IsAuthorizedAsync(ClaimsPrincipal principal, OrderResource resource)
        => Task.FromResult(principal.HasClaim("tenant", resource.Tenant));
}

services.AddScoped<IAuthorizationHandler<OrderResource>, SameTenantHandler>();
// ...
app.RequireAuthorization<AspNetContext, OrderResource>(
    ctx => new OrderResource(ctx.HttpContext.Request.Query["tenant"]));
```

The resource is derived from the transport **context** (topic, headers, a route/query value) because
authorization runs before the pipeline maps the request into a typed handler argument. When a
decision needs the fully-deserialized request, do that check inside the handler against the same
`AuthenticationHolder` principal instead.

## Protecting Only Some Routes

### Per handler: `UseEndpointAuthorization`

Put the requirement on the handler, and `UseEndpointAuthorization()` enforces it for that handler
alone. It works on every host and transport, Lambda included:

```csharp
using Benzene.Auth.Core.Endpoints;

[Message("tasks:list")]
[HttpEndpoint("GET", "/")]
[RequireScope("tasks.read")]
public class ListTasksHandler : IMessageHandler<ListTasks, TaskPage> { … }

[Message("tasks:create")]
[HttpEndpoint("POST", "/")]
[RequireScope("tasks.write")]
public class CreateTaskHandler : IMessageHandler<CreateTask, TaskDto> { … }

[Message("tasks:status")]
[HttpEndpoint("GET", "/status")]
[AllowAnonymous]
public class StatusHandler : IMessageHandler<Status, StatusDto> { … }
```

```csharp
eventPipeline.UseApiGatewayV2(api => api
    .UseApiGatewayAuthorizerIdentity()      // or UseOAuth2Bearer(...): who the caller is
    .UseEndpointAuthorization()             // what each handler needs
    .UseMessageHandlers());
```

- **Requirements:**
  - `[RequireScope(...)]` and `[RequireRole(...)]` take any one of the listed values.
  - `[RequirePolicy("name")]` uses a policy registered with `AddAuthorizationPolicy`.
  - Several attributes on one handler all apply.
  - `[AllowAnonymous]` overrides the rest.
- **Results:** no caller is 401, and a failed requirement is 403, as with the pipeline-wide checks.
- **It runs before the body is read.** A malformed body to a protected handler is 401, not 400. A
  route with no handler is still 404.
- **A handler with no attribute needs a caller.** A forgotten attribute fails closed. Turn this off
  with `o.RequireAuthenticatedByDefault = false`.
- **Benzene's own topics are anonymous:** the spec, ping, health, liveness and readiness topics
  (`o.AnonymousTopics`). Remove one to protect it: `o.AnonymousTopics.Remove(BenzeneTopic.Spec)`.
- **No attributes?** Declare requirements per topic instead:
  `o.ForTopic("tasks:create").RequireScope("tasks.write")`. They add to the handler's own
  attributes.
- **It applies wherever you add it,** `/benzene/invoke` included. A pipeline with no authenticator
  has no caller, so a queue pipeline should either leave it out or mark its handlers.

These are Benzene's own attributes (`Benzene.Auth.Core.Endpoints`), not ASP.NET Core's. A file that
also imports `Microsoft.AspNetCore.Authorization` needs to qualify `AllowAnonymous`.

### The whole pipeline

Every `Use*` call here is ordinary middleware — add it in front of the routes that need it, not
necessarily the whole pipeline. The straightforward way is to protect the entire pipeline (one
`UseHttp` call, `UseOAuth2Bearer`/`UseBasicAuth` first, `UseMessageHandlers()` last) when every
route in the service needs it.

If you need a genuine mix of public and protected routes in one ASP.NET Core app, isolate the
protected ones with a plain ASP.NET Core `app.Map(...)` branch, entered *before* any Benzene
pipeline runs — not two sibling `UseHttp` pipelines on the same branch. Benzene's message router is
unconditionally the terminal step of whichever pipeline it's wired into (it always answers, even
with `not-found`, and never falls through to a sibling pipeline once it's run), so two `UseHttp`
calls at the same level can't split routes between them — whichever one registers first ends up
answering every request its own router sees, recognized or not. `app.Map` sidesteps this
entirely: requests under the mapped prefix never reach the outer pipeline's router at all.

```csharp
// Public routes, as normal.
app.UseBenzene(benzene => benzene
    .UseHttp(asp => asp.UseMessageHandlers())
);

// Protected routes, isolated by URL prefix before Benzene ever sees the request.
app.Map("/admin", adminApp =>
{
    adminApp.UseRouting();
    adminApp.UseBenzene(benzene => benzene
        .UseHttp(asp => asp
            .UseOAuth2Bearer(oauth2Options)
            .RequireScope("admin")
            .UseMessageHandlers()   // discovers routes normally - Map already isolated this branch
        )
    );
    adminApp.UseEndpoints(endpoints => { });
});
```

One detail `Map` brings with it: it strips the matched prefix from the request path for
everything inside the branch, so a handler's `[HttpEndpoint("GET", "/admin/report")]` needs to
become `[HttpEndpoint("GET", "/report")]` once it's registered inside the `/admin` branch above.

`examples/Asp` (see below) demonstrates exactly this split.

## Try It: Running Demo

`examples/Asp` has a runnable demo wiring `UseOAuth2Bearer` and `RequireScope` against a
locally-generated fake JWKS server (no real identity provider needed to try it) — see that
project's `README.md` for exactly how to start it and mint a test token.

## Using an API Gateway authorizer

When API Gateway authenticates the caller (a JWT authorizer on an HTTP API, or a Cognito or Lambda
authorizer on a REST API), `Benzene.Auth.Aws.ApiGateway` reads what it decided and makes it the
caller:

```csharp
eventPipeline.UseApiGatewayV2(api => api
    .UseApiGatewayAuthorizerIdentity(o => o.RoleClaims.Add("cognito:groups"))
    .RequireScope("tasks.read")
    .UseMessageHandlers());
```

The same call works on `UseApiGateway` (REST APIs). The caller it builds:

| Authorizer | Claims | Name | Scopes |
|---|---|---|---|
| Cognito (REST API) | every Cognito claim | `sub` | the `scope` claim |
| JWT (HTTP API) | every JWT claim | `sub` | the authorizer's `scopes` |
| Lambda (either) | every context value (numbers and booleans as text) | `sub`, else (REST API) `principalId` | a `scope` value, if the authorizer returns one |
| IAM (HTTP API) | `userArn`, `callerId`, `accountId` | the user ARN | — |

`RoleClaims` names the claims that hold roles (`cognito:groups` for Cognito). `a,b`, `[a b]` and
JSON arrays all split into roles. `RequireScope`, `RequireRole` and `RequirePolicy` then work as they
do for a bearer token. An event with no authorizer context gets no caller, so those checks answer 401.
A caller another authenticator already set is kept: the first one wins.

**This trusts API Gateway.** It reads what the event says, and anything that can invoke the
function another way can say anything: a Lambda function URL, a direct `Invoke`, another trigger.
Allow only API Gateway to invoke the function, through its resource policy, and don't give it a
function URL.

## Security Notes

- **Don't log the `Authorization` header.** It carries a bearer token or Basic credentials in the
  clear (base64 isn't encryption) — make sure any request-logging middleware you add redacts it.
- **Protect the framework's own surfaces if you have no gateway.** `/benzene/spec` (the derived
  spec) and the reserved `benzene:spec` topic (the same contract document) can leak schema/topology to an
  unauthenticated caller. If your service has no gateway in front of it, compose the same
  authentication middleware in front of those paths — they're ordinary HTTP routes/topics like any
  other, so `UseOAuth2Bearer`/`UseBasicAuth` apply the same way.
- **`ValidAlgorithms` is not optional** — see [Why `ValidAlgorithms` has no default](#why-validalgorithms-has-no-default)
  above. Don't work around the `ArgumentException` by passing every algorithm your provider might
  ever use; pass exactly the ones it actually signs with.
- **A failed OAuth2 validation never reveals why.** Bad signature, expired, wrong issuer — all of
  them produce the same generic `unauthorized` detail to the caller, because a distinguishable
  failure reason in the response is an oracle an attacker can use to probe token shapes. If you
  need to debug a rejected token, check your service's logs (the real reason is logged
  server-side), not the HTTP response.

## What This Doesn't Cover

- **Integration with a specific external authorization-policy library or rules engine (OPA,
  Cedar, ASP.NET Core's `IAuthorizationService`).** Benzene provides the enforcement primitives —
  `RequireRole`/`RequirePolicy`/`RequireAuthorization` (see [Authorization: Roles, Policies &
  Resource Checks](#authorization-roles-policies--resource-checks) above) — and the pluggable
  `IAuthorizationPolicy`/`IAuthorizationHandler<TResource>` seams to plug such an engine into, but
  ships no adapter for any particular one. What a policy *means* stays in your app.
- **Issuing or refreshing tokens, or driving an OAuth2 login/consent flow.** `Benzene.Auth.OAuth2`
  only validates inbound bearer tokens; it's not an OAuth2 client or authorization server.
- **mTLS, session/cookie authentication, or SOAP/WS-Security** — not addressed by this feature.

## Further Reading

- [UseOAuth2Bearer](../common-middleware.md#useoauth2bearer), [UseBasicAuth](../common-middleware.md#usebasicauth), [RequireScope](../common-middleware.md#requirescope) in the Common Middleware reference
- [Message Results](../message-result.md) — the `unauthorized`/`forbidden` status vocabulary these packages return through
- `examples/Asp` — the runnable demo referenced above
