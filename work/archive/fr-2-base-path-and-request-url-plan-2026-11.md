> ARCHIVED 2026-11-19: actioned in `81361e4`; the design shipped as written. Not done: an end-to-end
> ASP.NET Core pipeline test (its pipeline test is commented out; the PathBase move is unit-tested),
> and folding the OIDC package's internal `RequestUrl` into `IRequestUrl`.

# FR-2 — Base path and the request URL (plan)

**Status: done, 2026-11-19** (`81361e4`). The request is FR-2 of the adopter feature requests,
[`Benzene/work/adopter-feature-requests.md`](https://github.com/daniellepelley/Benzene/blob/main/work/adopter-feature-requests.md).
No spec change. Code references are to `main` at `605225f`.

**Goal.**
- A service mounted at `/tasks` behind a custom-domain mapping declares `[HttpEndpoint("GET", "/{id}")]`
  and calls `UseBasePath("/tasks")`, instead of repeating the prefix on every route.
- A handler that needs an absolute link (a `Location`, a `next` page) injects `IRequestUrl` and calls
  `Absolute("/123")`, without touching raw API Gateway types.

## 1. What happens today (verified)

**There is no single path the hosts share.** Each host's topic getter, request enricher and version
getter read the raw request themselves:

| Host | Readers |
|---|---|
| API Gateway v1 | `ApiGatewayProxyRequest.Path`: topic getter `:32`, enricher `:40`, version getter `:28`, HTTP request adapter `:50`, log context, and the v1 `UseHealthCheck` exact match (`DependencyInjectionExtensions.cs:204`) |
| API Gateway v2 | `ApiGatewayV2Context.Path` (`RequestContext.Http.Path`) |
| ASP.NET Core and Azure Functions | `Request.Path`, never `PathBase` |

Everything else goes through `IHttpRequestAdapter<TContext>.Map`, which rebuilds an `HttpRequest`
from the raw request on every call: CORS, `/benzene/invoke`, MCP, the spec and mesh UIs, OIDC.

All of them read the same mutable request, so **rewriting the raw path once, early in the
pipeline, is seen by every reader**. The Azure example already strips `/api` that way
(`examples/Azure/Benzene.Examples.Azure/StartUp.cs:32`, `.OnRequest("strip-api", …)`). ASP.NET Core's
own `app.Map` moves a prefix into `PathBase` the same way.

**No base-path option exists.** There is nothing called `RoutePrefix` or `StripPrefix`, and no
production code uses `PathBase`.

**No request-URL abstraction exists.** `HttpRequest` has only method, path and headers. The one
URL builder is internal to the OIDC package: `Benzene.Mesh.Auth.Oidc/RequestUrl.cs` builds
`scheme://host` from `Host` and `X-Forwarded-Proto`, defaults to https, and honours a configured
`PublicBaseUrl`.

## 2. Design

**A host seam, `IHttpRequestLocation<TContext>`** (in `Benzene.Http`), with three members:
- `GetPath(context)`;
- `SetBasePath(context, basePath, path)`, which records the stripped prefix and rewrites the path;
- `GetScheme(context)`, the host's own scheme where it knows one, else `null`.

Each host registers it in its own DI extension:

| Host | Implementation |
|---|---|
| v1 | rewrites `ApiGatewayProxyRequest.Path` |
| v2 | rewrites `RequestContext.Http.Path` and `RawPath` |
| ASP.NET Core and Azure Functions | append the prefix to `Request.PathBase` and set `Request.Path` to the remainder, as `app.Map` does, and report `Request.Scheme` |

**`UseBasePath("/tasks")` and `UseRequestUrl(options)`,** generic middleware in `Benzene.Http`, placed
first in the HTTP pipeline (before CORS, which matches routes too).
- A request under the base path (`/tasks`, `/tasks/…`, matched ignoring case) has the prefix stripped.
- A request outside it answers `not-found`, through the transport's `IMessageHandlerResultSetter`, the
  way `TimeoutMiddleware` writes `timeout`.
- `UseBasePath(x)` is `UseRequestUrl(o => o.BasePath = x)`.

**`IRequestUrl`,** a scoped holder the middleware fills (the "scoped DI state, not the context" rule in
`Benzene.Abstractions.Middleware/CLAUDE.md`):
- `Scheme`, `Host`, `BasePath`, `Path`, and `BaseUrl` (`scheme://host` + base path);
- `Absolute(relative)` returns a `Uri`.

Where the values come from:

| Value | Source, in order |
|---|---|
| Host | `options.PublicBaseUrl` (scheme and host, as in OIDC); then the `X-Forwarded-Host` header, **only when `TrustForwardedHost` is set**; then `Host` |
| Scheme | `PublicBaseUrl`; then `X-Forwarded-Proto` (API Gateway sets it); then the host's own scheme; then `https` |

Injecting `IRequestUrl` without `UseRequestUrl`/`UseBasePath` in the pipeline throws a clear
`InvalidOperationException` naming the missing call, rather than returning empty values.

**API Gateway stages are not stripped automatically.** Whether a stage appears in the path depends on
the domain and the API type, and the repo has no verified event showing it. A stage that does appear
is just another prefix: `UseBasePath("/prod/tasks")`. The docs say so.

**The OIDC package's internal `RequestUrl`** stays as it is for now. It has its own options
(`PublicBaseUrl`), and changing it is out of scope.

## 3. Steps

1. `IHttpRequestLocation<TContext>` and the four host implementations, registered in `AddApiGateway`,
   `AddApiGatewayV2`, the ASP.NET Core extension and the Azure Functions extension.
2. `RequestUrlOptions`, `IRequestUrl` and its scoped holder, and `BasePathMiddleware<TContext>`, with
   the `UseBasePath`/`UseRequestUrl` extensions.
3. Tests:
   - **Unit:** prefix matching, including `/tasks` against `/tasksX`, case, and a trailing slash;
     `Absolute` with and without `PublicBaseUrl`; the forwarded-host trust switch.
   - **End to end on v1, v2, ASP.NET Core and Azure Functions:**
     - `GET /tasks/example` reaches the `/example` handler;
     - `GET /other` is 404;
     - a handler injecting `IRequestUrl` builds `https://api.example.com/tasks/123`.
4. Docs:
   - `docs/getting-started-aws.md`: the API Gateway section's "Path" bullet, and the troubleshooting
     entry.
   - `docs/azure-functions.md`: `UseBasePath("/api")` as the alternative to the `OnRequest` strip.
   - Package `CLAUDE.md` files, capability matrix, changelog.
5. Records: mark FR-2 done in the adopter feature requests, and archive this plan.

## 4. Risks

- **Placement.** `UseBasePath` must come before anything that reads the path. Middleware placed ahead
  of it sees the full path, and CORS ahead of it would match no routes. The docs say "first", and
  the XML doc does too.
- **The v1 `UseHealthCheck` exact match** is registered as its own pipeline branch. A health check
  placed before `UseBasePath` matches the full path; one placed after matches the stripped path.
  Either works if it's consistent.
- **ASP.NET Core** users already have `app.Map`. `UseBasePath` there is for parity and for
  `IRequestUrl`, not a replacement.
