> ARCHIVED 2026-11-19: actioned in `8fcf5dd`, with the owner's decisions D1-D4 as recommended. Not done:
> OpenAPI `security` per operation (step 5), left to FR-13; and honouring ASP.NET Core's
> `[AllowAnonymous]` by name (optional under D4).

# FR-5 — Per-endpoint authorization (plan)

**Status: done, 2026-11-19** (`8fcf5dd`); D1-D4 decided as recommended. The request is FR-5 of the adopter feature requests,
[`Benzene/work/adopter-feature-requests.md`](https://github.com/daniellepelley/Benzene/blob/main/work/adopter-feature-requests.md).
It depends on FR-4 for a caller from an API Gateway authorizer, though not for one from
`UseOAuth2Bearer`. No spec change. Code references are to `main` at `031b7d8`.

**Goal.** Different handlers in one pipeline need different scopes (`tasks.read` for `GET`,
`tasks.write` for `POST`), a few are anonymous, and forgetting an attribute fails closed.

```csharp
[Message("tasks:create")]
[HttpEndpoint("POST", "/")]
[RequireScope("tasks.write")]
public class CreateTaskHandler : IMessageHandler<CreateTask, TaskDto> { … }

[AllowAnonymous]
public class HealthHandler : … { … }
```

## 1. What happens today (verified)

**Authorization is pipeline-wide.**
- `RequireScope`, `RequireRole`, `RequirePolicy` and `RequireAuthorization` are middleware that run
  for every message in the pipeline.
- `[HttpEndpoint]` carries only method and path.
- The documented way to protect some routes is an ASP.NET Core `app.Map` branch
  (`docs/cookbooks/auth-patterns.md:251-290`), which Lambda has no equivalent of.

**There are two places a check could run.**
- **The per-handler pipeline** (`UseMessageHandlers(router => router.Use…)`, where
  DataAnnotations validation runs).
  - It knows the handler type (`IMessageHandlerContext.HandlerType`).
  - **But it runs after the request body is deserialized:** a malformed body to a protected endpoint
    answers 400 before any auth check.
  - **It is empty** unless the `router =>` overload is used: nothing registers an
    `IHandlerMiddlewareBuilder` in DI, and `Benzene.CloudService` uses the plain overloads.
  - A check placed there would fail open in exactly the pipelines that never opted in.
- **An outer middleware before `UseMessageHandlers`,** resolving topic → handler definition the way
  `RateLimitingMiddleware` (`:161-168`) and `JsonSchemaMiddleware` (`:130-131`) already do.
  - This runs before deserialization, on every transport.
  - It has the definition's `HandlerType` to read attributes from.

**Attribute reads exist** (`[Message]`, `[HttpEndpoint]`, `ValidationStatusAttribute`), but none is
cached per type.

## 2. Design

**Attributes, in `Benzene.Auth.Core`:**

| Attribute | Means |
|---|---|
| `[RequireScope(params string[] anyOf)]` | needs one of these scopes |
| `[RequireRole(params string[] anyOf)]` | needs one of these roles |
| `[RequirePolicy(string name)]` | needs this named policy to pass |
| `[AllowAnonymous]` | needs no caller |

Several attributes on one handler all apply (AND). Within one attribute, any of the listed values
passes (OR), matching the middleware forms.

**`UseEndpointAuthorization(options)`,** an outer middleware placed after the authenticators
(`UseOAuth2Bearer`, `UseApiGatewayAuthorizerIdentity`) and before `UseMessageHandlers`:
1. Get the topic, then the handler definition.
   - A topic with no handler passes through, and the router answers 404.
2. Read the handler type's requirements from a per-type cache.
3. `[AllowAnonymous]` passes.
4. No caller is 401, through `AuthResults.UnauthorizedAsync`, as today.
5. A failed requirement is 403, through `AuthResults.ForbiddenAsync`.
6. Otherwise, continue.

Checks reuse the existing code: `RoleClaims`, `ScopeClaims` (moved per FR-4 D2), and the by-name
policy lookup.

**The registration-time form,** for handlers you'd rather not annotate:
`options.ForTopic("tasks:create").RequireScope("tasks.write")`. It is merged with the attributes.

**Reserved topics** (`benzene:spec`, `benzene:ping`, health, liveness, readiness): see D3.

**Every transport.** The middleware runs on whatever pipeline it's placed in: HTTP, the
`/benzene/invoke` envelope, or a queue. A transport with no caller (SQS) fails closed under D2's
default, so a queue pipeline either doesn't add it, or marks its handlers.

## 3. Decisions for the owner

- **D1 — Where it runs.** **Recommended:** the outer middleware above. It authorizes before parsing
  the body, so the answer is 401, not 400, and it works without the per-handler pipeline. The
  per-handler alternative would fail open wherever that pipeline is empty.
- **D2 — The default for a handler with no attribute.**
  - **Recommended:** `RequireAuthenticatedByDefault = true` when `UseEndpointAuthorization` is in
    the pipeline. A forgotten attribute then means "authenticated caller", never "anyone".
  - `false` means unannotated handlers are open, and only annotated ones are checked.
- **D3 — Reserved topics.** The spec, ping and health topics have no handler class to annotate.
  - **Recommended:** anonymous by default, listed in `options.AnonymousTopics`, which starts with
    them and can be cleared. A service that wants its spec private removes `benzene:spec`.
- **D4 — Attribute names.** `[AllowAnonymous]` and `[Authorize]` exist in ASP.NET Core
  (`Microsoft.AspNetCore.Authorization`).
  - **Recommended:** Benzene's own `Benzene.Auth.Core.AllowAnonymousAttribute`, `RequireScope` and
    friends. Reusing ASP.NET Core's would add a framework reference to `Benzene.Auth.Core`, which
    Lambda packages don't otherwise need.
  - Optionally, also honour ASP.NET Core's `[AllowAnonymous]`, by type name, so a handler shared
    with ASP.NET Core code needn't carry both.

## 4. Steps

1. **Attributes and a per-type requirement cache** (`ConcurrentDictionary<Type, EndpointRequirements>`)
   in `Benzene.Auth.Core`. Move `ScopeClaims` there (FR-4 D2).
2. **`EndpointAuthorizationMiddleware<TContext>`** and `UseEndpointAuthorization`, with options
   (`RequireAuthenticatedByDefault`, `AnonymousTopics`, `ForTopic(...)`).
3. **Tests, unit and per host** (v1, v2, ASP.NET Core, Azure Functions, the envelope):
   - two handlers with different scopes in one pipeline: each passes and fails on its own scope;
   - no caller is 401; `[AllowAnonymous]` passes with no caller;
   - an unannotated handler needs a caller under D2;
   - a malformed body to a protected endpoint is 401, not 400;
   - a reserved topic is anonymous by default;
   - an unknown topic still 404s;
   - the registration-time form merges with attributes.
4. **Docs.**
   - `docs/cookbooks/auth-patterns.md`: rewrite "Protecting Only Some Routes" around the
     attributes, keeping `app.Map` as the ASP.NET Core alternative.
   - `docs/getting-started-aws.md`: remove "authorization per endpoint" from "Not built yet".
   - The package `CLAUDE.md`, capability matrix and changelog.
5. **OpenAPI (FR-13):** emit `security` per operation from the same cache
   (`OpenApiDocumentBuilder.CreateOpenApiOperation`, `:105-123`). Do this here or with FR-13.
6. **Records:** FR-5 done in the adopter feature requests, and archive this plan.
