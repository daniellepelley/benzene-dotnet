# Benzene.Mcp

## What this package does
The Model Context Protocol (MCP) transport binding: exposes an application's message handlers to AI
clients as MCP **tools**, over Streamable HTTP (any Benzene HTTP host: API Gateway, Azure Functions,
ASP.NET Core, self-host) or stdio (the self-hosted worker). A tool call is dispatched through the
**same BenzeneMessage pipeline** every other transport uses, so middleware, validation, the mesh's
usage/issue feeds and the start-up checks all see it. This package standardizes the protocol and the
dispatch; **tool design stays in the application** (see "Do NOT").

## Key types
- `McpToolDefinition` - one tool: `Name`, `Description`, `InputSchema` (JSON Schema `JsonObject`),
  `Writes`/`Destructive` (advertised as `readOnlyHint`/`idempotentHint`/`destructiveHint`), and
  **exactly one** of `Topic` (+`Version`) or `Invoke`:
  - **topic-bound** (`McpToolDefinition.ForTopic`) - the arguments object is the message body, dispatched
    to the topic; the response envelope is mapped by `McpResults.ToToolResult`.
  - **custom** (`McpToolDefinition.Custom` / `McpToolInvoker`) - a body the app wrote, receiving
    `McpToolCall` (name, arguments, session id) and `McpToolContext` (request scope + `IMcpMessageDispatcher`).
- `McpBuilder` - `AtPath`, `ServerInfo`, `Instructions`, `Tool(...)` overloads (incl. `Tool<TRequest>` deriving
  the schema), `OnToolFailure(Func<Exception, McpToolResult?>)` (map a domain exception to a refusal),
  `Log`. `Build()` validates names (`^[A-Za-z0-9_-]{1,64}$`, unique), descriptions and the topic/body rule.
- `McpServer` - the protocol (JSON-RPC 2.0): `initialize` (version negotiation, `serverInfo`, `instructions`),
  `tools/list`, `tools/call`, `ping`; notifications return `null` (send nothing). Unknown methods →
  `-32601`; `McpRequestException` while reading a request (no tool name, unknown tool) → `-32602`; inside a tool body → a refused
  tool result (`isError: true`); anything else → logged under a short reference, `-32603` with only the reference.
- `IMcpMessageDispatcher` / `McpMessageDispatcher` - one message through the pipeline in a fresh DI
  scope (`BenzeneMessageApplication`); sends `benzene-version` when set, `mcp-tool` for topic-bound calls,
  `mcp-session` when the transport has a session. `AskAsync` maps the envelope to a tool result.
- `McpResults.ToToolResult` - success → body text (or the status when empty); failure →
  `"{status}: {detail | errors[].message | message | title | body}"` refusal.
- `McpArgs` - strict argument reading (`RequiredText`, `OptionalDate` (yyyy-MM-dd only), `OptionalDecimal`
  ("£1,234.00" accepted), `OptionalFlag` ("true" as a string accepted), `OptionalChoice` (enforces the enum),
  `Objects`, `OptionalList`); every refusal is a `McpRequestException` naming what was expected.
- `McpSchema` - `Object/Text/Date/Number/Integer/Flag/Id/ListOf/Choice` builders and `For<T>()` (camelCase,
  `[JsonPropertyName]`, `[Description]`, `[Required]`/`[JsonRequired]`/`required`, `[JsonIgnore]`; nested
  objects, arrays, dictionaries, enums, dates, GUIDs; depth/cycle guarded; `additionalProperties: false`).
- `McpToolCatalog` - singleton of the advertised tools, for middleware ahead of the endpoint (an authorizer
  reading `Writes`); `McpRequestInfo.TryParse(body)` reads the method/tool name off a request first.
- `McpToolTopicStartUpCheck` (`IStartUpCheck` "mcp-tool-topics") - every topic-bound tool's topic has a handler
  (`IMessageHandlerDefinitionLookUp.FindHandler`); throws `McpToolWithoutHandlerException` naming tool → topic.
- `Hosting/McpHttpMiddleware<TContext>` + `UseMcp(configure, pipeline)` - Streamable HTTP mount, mirrors
  `BenzeneMessageHttpMiddleware`: POST on the path → answer (200, or 202 + empty body for a notification);
  GET/DELETE on the path → 405 `Allow: POST`; other paths fall through. `Mcp-Session-Id`: a fresh 32-hex id on
  `initialize`, otherwise the client's (validated) id echoed; passed to tools as `McpToolCall.SessionId`.
  Cancellation from `ICancellationTokenAccessor`; logging via `ILoggerFactory` ("Benzene.Mcp") unless `Log` set.
- `Hosting/McpStdioWorker` + `UseMcpStdio(configure, pipeline, input?, output?)` - `IBenzeneWorker`: one message
  per line; one DI scope per message; nothing but answers on the output stream; remarks go to the log (stderr).

## Conventions
- The MCP JSON is `System.Text.Json.Nodes`; the envelope into the pipeline is a `BenzeneMessageRequest`
  whose `Body` is the arguments object's JSON.
- The spec entry is `docs/specification/transport-bindings.md` §2 "MCP" in the Benzene (cross-language) repo;
  the .NET doc is `docs/mcp.md`.

## Do NOT
- Do not generate one tool per handler or auto-expose every topic. Tools are shaped like the jobs a model
  is asked to do; the application picks and names them. `Tool<TRequest>` is a shorthand for a schema, not a
  policy.
- Do not add session state to the server; `Mcp-Session-Id` is issued/echoed here and *interpreted* by the app.
- Do not write to the stdio output stream from anywhere but `McpStdioWorker`.
- Do not add a NuGet dependency (the official MCP SDK included) without asking - the protocol surface a tool
  server needs is a page, and owning it is what keeps the Lambda route from having to be an ASP.NET app.
