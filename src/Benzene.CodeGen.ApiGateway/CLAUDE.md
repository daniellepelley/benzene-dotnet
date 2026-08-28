# Benzene.CodeGen.ApiGateway

## What this package does
Generates an AWS API Gateway extended-OpenAPI document (`openApi.yaml`, wrapped in
`# AUTOGEN START/END` markers) from a Benzene `EventServiceDocument`. For each HTTP-mapped topic it
emits the path, verbs, a CORS `OPTIONS` mock integration, security headers, and the
`x-amazon-apigateway-integration` VTL request/response templates that proxy API Gateway requests
into a Lambda-backed Benzene service.

## Key types
- `ApiGatewayBuilderV1 : ICodeBuilder<EventServiceDocument>` — the generator.
  `new ApiGatewayBuilderV1(string url)` uses generic defaults; `new ApiGatewayBuilderV1(ApiGatewayOptions)`
  configures it.
- `YamlLiteral` — escapes a user-authored value (a topic, or a tag/path derived from an HTTP-mapped
  path) for safe embedding as a YAML scalar: wraps it in single quotes, doubling any embedded single
  quote (the standard YAML single-quoted-scalar rule — no backslash escapes needed, unlike a
  double-quoted scalar). Applied to every `summary:`, `tags:` sequence item, and the path mapping key
  itself (round 14-15, #212/#263) — a `"` in a topic used to break the double-quoted `summary:`
  scalar, and a `:` in a path segment survived `CreateTag`'s title-casing into an invalid unquoted
  sequence item.
- `ApiGatewayOptions` — everything that must not be hard-coded per deployment:
  - `Url` — the backend integration URI token (emitted as `#{Url}#` for downstream substitution).
  - `AuthorizerName` — the custom authorizer applied to secured operations (in addition to `api_key`).
    **Null by default** → `api_key` only, no custom authorizer.
  - `UnauthenticatedTopics` — topics exempt from the custom authorizer (still `api_key`). Empty by default.
  - `AllowedHeaders` — the CORS `Access-Control-Allow-Headers` value. Minimal generic default
    (`Authorization,Content-Type,X-Api-Key`); add app-specific headers (e.g. `X-Tenant-Id`) here.
  - `IdentityHeaders` — extra request headers injected into the Lambda integration template, mapping a
    header name to a VTL value (typically authorizer-context claims, e.g.
    `["x-user-id"] = "$context.authorizer.userid"`). Empty by default.

## Important: no company coupling
This generator was originally hard-coded for one deployment (an Okta authorizer named "Elements",
a `PlatformTenantId`/licenses/subscriptions claim model, and `user:signup`/`user:migrate` as public
topics). That is all gone — those are now `ApiGatewayOptions` inputs with generic, empty defaults, so
the default output is company-free. Keep it that way: new deployment-specific values belong in
`ApiGatewayOptions`, never as string literals in the builder.

The CORS origin whitelisting still emits `#cors_allowed_origins#` / `#cors_localhost#` placeholder
tokens for a downstream token-substitution step — a templating convention, not a company value.

## Duplicate-route detection is case-folded on Method only
`BuildCodeFiles`'s duplicate-route guard groups on `{ Method.ToLowerInvariant(), Path }` — Path stays
raw (a path is emitted verbatim as the YAML mapping key, so two differently-cased paths really are
two different keys), but Method is case-folded because `BuildVerb` always emits the verb
lower-cased. Two topics mapped to `"GET"` and `"get"` for the same path used to pass this check
uncaught and then collide as two identical `get:` keys under that path (round 14-15, #211) — mirrors
`Benzene.Http.Routing.ReflectionHttpEndpointFinder`'s own case-folded duplicate-route check for the
identical concern.

## Tests
- `test/Benzene.Core.Test/Autogen/CodeGen/ApiGateway/LambdaOpenApiBuilderTest.cs` — golden-file output
  (`Examples/GetUser.yaml`, `Examples/RbacTest.yaml`) for the default (company-free) output, plus
  option tests proving `AuthorizerName`/`UnauthenticatedTopics`/`IdentityHeaders`/`AllowedHeaders`
  apply and the default injects no authorizer or identity claims. Also: the case-folded duplicate-route
  case (#211) and adversarial-topic/path content (quote, colon) proving `YamlLiteral` keeps the
  generated YAML valid (#212/#263). `YamlLiteralTest.cs` unit-tests the helper directly. The golden
  files reflect `YamlLiteral`'s single-quoted output (`summary: 'user:get'`, `- 'Rbac User'`,
  `'/rbac/user/{id}':`) rather than the old unquoted/double-quoted forms — a real behavior change
  (still valid, equivalent YAML), not just a formatting nit.
