# MCP: expose handlers as AI tools

`Benzene.Mcp` is the [Model Context Protocol](https://modelcontextprotocol.io) transport binding.
It lets an AI client (a desktop assistant, an IDE, an agent) call your service's message handlers as
MCP **tools**, over Streamable HTTP on any Benzene HTTP host or over stdio on the self-hosted worker.

A tool call is dispatched through the **same BenzeneMessage pipeline** every other transport uses.
The middleware you composed, validation, the mesh's usage and issue feeds, and the start-up checks
all see it, exactly as they see the same topic arriving over HTTP or a queue. The package
standardizes the protocol and the dispatch. **Tool design stays in your application.**

## Mount it on an HTTP host

```csharp
using Benzene.Mcp;
using Benzene.Mcp.Hosting;

public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration) => app
    .UseAwsLambda(aws => aws
        .UseApiGateway(api => api
            .UseMcp(mcp => mcp
                    .ServerInfo("orders", "1.0")
                    .Instructions("Look an order up before changing it.")
                    .Tool<GetOrderRequest>("get_order", "Fetches one order by id.", "order:get")
                    .Tool<CancelOrderRequest>("cancel_order", "Cancels an order that has not shipped.", "order:cancel", writes: true),
                pipeline => pipeline.UseMessageHandlers())
            .UseMessageHandlers()));
```

That answers `POST /mcp` on the same API the rest of the service already deploys. The same call
works on `UseHttp(...)` (Azure Functions, ASP.NET Core) and the self-hosted HTTP worker, because the
middleware drives the transport-neutral request/response adapters, like the BenzeneMessage HTTP endpoint.

Put authentication middleware **in front of it**. The tools reach the pipeline like any other
transport; an unauthenticated MCP endpoint is an unauthenticated service.

## Or over stdio

```csharp
new BenzeneWorkerBuilder(container)
    .UseMcpStdio(
        mcp => mcp.ServerInfo("orders", "1.0").Tool<GetOrderRequest>("get_order", "Fetches one order by id.", "order:get"),
        pipeline => pipeline.UseMessageHandlers());
```

One JSON-RPC message per line in on standard input, one per line out on standard output. Nothing
else in the process may write to standard output. The worker's own remarks go to standard error.

## Two kinds of tool

**Topic-bound.** `Tool<TRequest>(name, description, topic)` or `Tool(name, description, topic, schema)`.
The arguments object becomes the message body, the handler for the topic answers, and the response
envelope becomes the tool result: a success is the body as text, a failure is a refusal the model can
read (`"not-found: No such order."`) built from the problem document's `detail`.

**Custom.** `Tool(name, description, schema, invoke)`. A body you write, for the tools that are
shaped like jobs rather than single handlers. It receives the call (name, arguments, session id) and
an `McpToolContext` with the request's DI scope and an `IMcpMessageDispatcher`, so it can ask the
pipeline as many questions as the job needs:

```csharp
.Tool("month_end", "Closes the month: posts accruals then locks the period.",
    McpSchema.Object(("period", McpSchema.Text("The month, yyyy-MM."), true)),
    async (call, context, ct) =>
    {
        var period = McpArgs.RequiredText(call.Arguments, "period");
        var accruals = await context.Dispatcher.AskAsync("ledger:post-accruals", new JsonObject { ["period"] = period }, cancellationToken: ct);
        if (accruals.IsError) return accruals;
        return await context.Dispatcher.AskAsync("ledger:lock", new JsonObject { ["period"] = period }, cancellationToken: ct);
    },
    writes: true)
```

Do not generate one tool per handler. A service with a hundred topics should not put a hundred
schemas in front of a model. Pick the jobs a model will be asked to do, name them for the model, and
write descriptions it can decide from. `Tool<TRequest>` is a shorthand for a schema, not a policy.

## Schemas and arguments

`McpSchema` builds the JSON Schema a tool advertises: `Object`, `Text`, `Date` (adds "Written
yyyy-MM-dd."), `Number`, `Integer`, `Flag`, `Id`, `ListOf`, `Choice`. `McpSchema.For<T>()` derives
one from a request type: camelCase names or `[JsonPropertyName]`, `[Description]` for the text the
model reads, `[Required]` / `[JsonRequired]` / `required` for the required list, `[JsonIgnore]` honoured.

`McpArgs` reads arguments strictly. A model writes them, so a date arrives as "March 2026" and a
number as a string. Each helper refuses with a sentence naming what was expected, and the refusal
reaches the model as a tool result it can correct, rather than a coercion that records the wrong
date. The coercions it does make are the ones where the request was unambiguous: `"true"` as a
string, `"£1,234.00"`, a comma-separated list.

## What the model sees when things go wrong

| What happened | What the client gets |
|---|---|
| Handler returned a failure result | Tool result with `isError: true`: `"{status}: {detail}"` |
| Tool body threw `McpRequestException` | Tool result with `isError: true`: the message |
| Tool body threw an exception you mapped with `OnToolFailure` | Tool result with `isError: true`: your text |
| Tool body threw anything else | JSON-RPC `-32603` with a short reference; the detail is logged under that reference and never sent |
| Unknown tool, missing tool name | JSON-RPC `-32602` naming the problem (the protocol's rule: a stale tool list is a client fault, not something the model can reword) |
| Unknown method, bad JSON | JSON-RPC `-32601` / `-32700` |

Map your domain's own "no" with `OnToolFailure`, so a locked period or a missing permission reaches
the model as an answer it can act on:

```csharp
.OnToolFailure(e => e is LedgerRefusedException ? McpToolResult.Refused(e.Message) : null)
```

## Sessions

Over HTTP the middleware issues a `Mcp-Session-Id` on `initialize` and echoes a valid one back on
every later request; tools receive it as `McpToolCall.SessionId` and topic-bound dispatches carry it
as the `mcp-session` header. The server keeps no session state. What the id means (a chosen tenant,
a conversation's working set) is the application's, keyed on the id. Over stdio there is no session
id: the process is the session.

## Gating writes

Every tool advertises `readOnlyHint` / `idempotentHint` (the inverse of `writes`) and
`destructiveHint`, so a client can put the tools that write behind an approval. When your own
authorizer needs the same answer before a call runs, read the tool a request names with
`McpRequestInfo.TryParse(body)` and its `Writes` from the registered `McpToolCatalog`. There is no
second list of which tools write to get wrong.

## Start-up check

`McpToolTopicStartUpCheck` (`mcp-tool-topics`) fails start-up when a topic-bound tool names a topic
no registered handler answers, naming the tool and the topic. A tool that advertised itself to a
model and then answered `not-found` on every call would be worse than a crash at start-up.

## What this binding does not do

- No server-to-client stream: `GET` on the path is answered `405`. Tools answer requests; they do not push.
- No resources or prompts, only tools. Add them when a real client needs them.
- No session store, no OAuth server. Compose the auth middleware you already use in front of the endpoint.

## See Also

- [Unified Hosting Model](hosting.md)
- [Message Handlers](message-handlers.md)
- [Middleware](middleware.md)
- [Transport bindings (specification)](https://benzene.app/docs/specification/transport-bindings.html)
