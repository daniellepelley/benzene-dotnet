# FR-4 — Caller identity from the API Gateway authorizer (plan)

**Status: plan for review, 2026-11-19. Decisions D1–D3 below need the owner's call before any
code.** The request is FR-4 of the adopter feature requests,
[`Benzene/work/adopter-feature-requests.md`](https://github.com/daniellepelley/Benzene/blob/main/work/adopter-feature-requests.md).
No spec change. Code references are to `main` at `031b7d8`.

**Goal.** API Gateway has already authenticated the caller: a JWT authorizer on an HTTP API, or a
Cognito or Lambda authorizer on a REST API. Benzene turns the authorizer's context into the caller's
`ClaimsPrincipal`, so `RequireScope`, `RequireRole`, `RequirePolicy` and FR-5's endpoint attributes
work unchanged, without validating the token a second time in the function.

## 1. What happens today (verified)

**Benzene only knows a caller it authenticated itself.**
- `UseOAuth2Bearer` validates a JWT and sets `AuthenticationHolder.Principal`
  (`src/Benzene.Auth.OAuth2/OAuth2BearerMiddleware.cs`). `UseBasicAuth` does the same for Basic
  credentials.
- `AuthenticationHolder` (`src/Benzene.Auth.Core/AuthenticationHolder.cs:27`) is a scoped holder,
  following the context-purity rule.
- Every check reads it:
  - `RequireRole` uses `ClaimTypes.Role`, `role` and `roles`.
  - `RequirePolicy` and `RequireAuthorization` take the principal as it is.
  - `RequireScope` reads `scope`, split on spaces, plus `scp`, split on spaces or as a JSON array,
    through the internal `ScopeClaims`.
- A missing principal is 401 and a failed check is 403 (`AuthResults`).

**Nothing reads the event's authorizer context.** No code reads `RequestContext.Authorizer`,
`Identity` or `RequestId`. The 2026-07 cloud review recorded it
(`work/archive/cloud-review-2026-07/03-aws-apigateway.md:31-35`, "requestContext (authorizer claims,
identity, stage) not surfaced to handlers", Medium). It recommended an enricher or adapter path,
not context markers. Nothing has closed it since.

**What the SDK (`Amazon.Lambda.APIGatewayEvents` 2.6.0) carries:**
- **v1:** `RequestContext.Authorizer` is `APIGatewayCustomAuthorizerContext`. It has `PrincipalId`,
  the Lambda authorizer's context values, and `Claims` (Cognito user pools).
- **v2:** `RequestContext.Authorizer` is `AuthorizerDescription`, with:
  - `Jwt.Claims` and `Jwt.Scopes`;
  - `Lambda`, the Lambda authorizer's context;
  - `IAM`: `UserARN`, `CallerId`, `AccountId`, ….

Property **types** were not confirmed by reflection. Step 1 confirms them against real event JSON
before anything relies on them.

**The docs** (`docs/cookbooks/auth-patterns.md:7`) say that with an API Gateway authorizer "there's
nothing to configure". That is true for allowing or denying a request. It isn't true for who the
caller is or what scopes they have.

## 2. Design

**`UseApiGatewayAuthorizerIdentity(options)`,** on v1 and v2 pipelines. It reads the event's
authorizer context and, if there is one, sets `AuthenticationHolder.Principal`.

**v1:**
- each Cognito claim becomes a claim of the same type;
- a Lambda authorizer's context values become claims (string, number and bool, as strings);
- `principalId` becomes `ClaimTypes.NameIdentifier`.

**v2:**
- each JWT claim becomes a claim;
- `Jwt.Scopes` becomes one space-separated `scope` claim, which `ScopeClaims` already reads;
- Lambda context values become claims;
- IAM `UserARN`/`CallerId` become name claims.

**Options:**
- `NameClaim` (default `sub`).
- `RoleClaims` (default none; for Cognito, `cognito:groups`), mapped to `ClaimTypes.Role`, with a
  list value split. Cognito delivers groups as a bracketed or comma-joined string; step 1 pins the
  exact form from real events.
- `AuthenticationType` (default `"ApiGatewayAuthorizer"`), so `Identity.IsAuthenticated` is true.

**Behaviour:**
- With no authorizer context (no authorizer on the route, or a direct invoke), no principal is set,
  and the checks answer 401 as today.
- An already-set principal is not replaced (D3).

**Trust is the whole point, so the docs say it plainly.** This trusts API Gateway. A function that
can be reached another way can be sent a forged authorizer context: a Lambda function URL, a direct
`Invoke`, or another trigger. The cookbook gets a "Using an API Gateway authorizer" section with
that warning, and with the resource policy to deny anything else.

## 3. Decisions for the owner

- **D1 — Package.**
  - Putting the middleware in `Benzene.Aws.Lambda.ApiGateway` makes that package depend on
    `Benzene.Auth.Core`.
  - **Recommended:** a new package, `Benzene.Auth.Aws.ApiGateway`, feature-first per the
    AGENTS.md naming rule for cross-cutting families. It references both, and the ApiGateway package
    gains no dependency.
- **D2 — `ScopeClaims` visibility.** It is `internal` to `Benzene.Auth.OAuth2`. FR-4 needs none of
  it (it emits a `scope` claim that `ScopeClaims` already reads), but FR-5's `[RequireScope]` does.
  **Recommended:** move `ScopeClaims` to `Benzene.Auth.Core` as a public `ScopeClaims.GetGrantedScopes`.
  `RequireScope` then stops being OAuth2-specific: scopes can come from any authenticator.
- **D3 — An existing principal.** If `UseOAuth2Bearer` also ran and set one, keep it or replace it?
  **Recommended:** keep it, and document "the first authenticator wins". Running both is a
  configuration smell, but it shouldn't silently change who the caller is.

## 4. Steps

1. **Confirm the SDK types.** Write fixtures in
   `test/Benzene.Core.Test/Aws/ApiGateway/Examples/` from AWS's documented sample events:
   - a v1 Cognito event;
   - a v1 Lambda-authorizer event;
   - a v2 JWT event;
   - a v2 Lambda-authorizer event;
   - a v2 IAM event.

   Deserialize each with the SDK's own serializer, and assert the shapes the mapping reads.
2. **Middleware and options** in the package chosen under D1, with `UseApiGatewayAuthorizerIdentity`
   for `ApiGatewayContext` and `ApiGatewayV2Context`.
3. **Tests, per fixture:**
   - the principal's name, roles and scopes;
   - `RequireScope("tasks.read")` passes with the scope, is 403 without it, and is 401 with no
     authorizer context;
   - an existing principal is kept.
4. **Docs:**
   - `docs/cookbooks/auth-patterns.md`: the new section and the trust warning;
   - `docs/getting-started-aws.md`: remove "a caller identity taken from an API Gateway authorizer's
     context" from the "Not built yet" list;
   - the package `CLAUDE.md`, capability matrix and changelog.
5. **Records:** FR-4 done in the adopter feature requests, and archive this plan.
