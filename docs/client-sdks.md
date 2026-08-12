# Client SDK Generation

Because every Benzene handler declares its contract — a topic and typed request/response — Benzene
can generate a strongly-typed C# **client SDK** for a service. Callers get a typed client instead
of hand-assembling messages, and the client stays in sync with the service's handlers.

The generator lives in the `Benzene.CodeGen.Client` package and produces a
`{Service}ServiceClient` class (implementing an `I{Service}ServiceClient` interface) with one
`…Async` method per handler.

## How it works

Generation runs off a service description called an `EventServiceDocument` — the same model behind
the [OpenAPI/AsyncAPI spec](spec.md). You can build that document two ways:

- **From the handler assembly directly** (reflection), at build time.
- **From a running service's [`spec` endpoint](spec.md)**, so you can generate a client from a
  deployed service without referencing its code.

## Generating from a handler assembly

Given handlers in an assembly:

```csharp
[Message("hello:world")]
public class HelloWorldMessageHandler : IMessageHandler<HelloWorldMessage, HelloWorldResponse>
{
    public Task<IBenzeneResult<HelloWorldResponse>> HandleAsync(HelloWorldMessage message)
        => BenzeneResult.Ok(new HelloWorldResponse { Message = $"Hello {message.Name}" }).AsTask();
}
```

Build the service document and run the SDK builder:

```csharp
using Benzene.CodeGen.Client;
using Benzene.Core.MessageHandlers;
using Benzene.Schema.OpenApi.EventService;

// 1. Discover the handlers and turn them into a service document
var definitions = new ReflectionMessageHandlersFinder(typeof(HelloWorldMessageHandler).Assembly)
    .FindDefinitions();
var document = definitions.ToEventServiceDocument();

// 2. Generate the client SDK
var sdkBuilder = new MessageClientSdkBuilder(
    serviceName: "HelloWorld",
    baseNamespace: "Benzene.Examples.Clients");

var codeFiles = sdkBuilder.BuildCodeFiles(document);

// 3. Write the generated files out
foreach (var file in codeFiles)   // each ICodeFile has a Name and Lines
{
    File.WriteAllLines(file.Name, file.Lines);
}
```

This produces `HelloWorldServiceClient.cs` containing a `HelloWorldServiceClient` with a
`HelloWorldAsync(HelloWorldMessage message)` method (plus a `HealthCheckAsync()` and header-aware
overloads).

## Using the generated client

The generated client takes an `IBenzeneMessageSender` (from `Benzene.Clients`) in its constructor
and returns results as `IBenzeneResult<T>` — the same result model your handlers use:

```csharp
public class HelloWorldServiceClient : IHelloWorldServiceClient
{
    public HelloWorldServiceClient(IBenzeneMessageSender sender) { /* generated */ }

    public Task<IBenzeneResult<HelloWorldResponse>> HelloWorldAsync(HelloWorldMessage message) { /* generated */ }
    public Task<IBenzeneResult<HelloWorldResponse>> HelloWorldAsync(HelloWorldMessage message, IDictionary<string, string> headers) { /* generated */ }
}
```

The generator also emits a sibling `HelloWorldServiceClientRouting.RequiredTopics` array, for
`ValidateOutboundRouting()`'s startup check — see [Validating routes at
startup](clients.md#validating-routes-at-startup-validateoutboundrouting).

Configure the underlying transport by routing each of the client's topics via
`AddOutboundRouting(...)` — for example `.UseSqs(...)`/`.UseSns(...)` from `Benzene.Clients.Aws` to
call the service via AWS, or an HTTP transport for calling it over HTTP. The generated client is
transport-agnostic; the outbound route registered for each topic decides how the message is
actually sent.

```csharp
var client = new HelloWorldServiceClient(sender);
var result = await client.HelloWorldAsync(new HelloWorldMessage { Name = "World" });
if (BenzeneResult.IsSuccess(result))
{
    Console.WriteLine(result.Payload.Message);
}
```

## Generating message handler stubs

The same package includes `MessageHandlerBuilder`, which generates handler *stubs* from a service
document — useful for scaffolding a new service from an existing contract (consumer-first
scaffolding: point it at a producer's published contract and get compilable handler stubs to fill
in, rather than hand-typing the `[Message(...)]` boilerplate):

```csharp
var handlerFiles = new MessageHandlerBuilder("MyService.Handlers").BuildCodeFiles(document);
```

From the CLI, this is `--output message-handlers`:

```bash
benzene build -file Orders.spec.json -output message-handlers -namespace MyService.Handlers -directory Generated/
```

## Two client shapes: whole-service vs. per-topic

`Benzene.CodeGen.Client` generates two different client shapes from the same
`EventServiceDocument`, both usable directly or via the `benzene` CLI's `build` command
(`--output client` / `--output topic-client`):

| | **`client`** (`MessageClientSdkBuilder`) | **`topic-client`** (`AtomicClientSdkBuilder`) |
|---|---|---|
| Shape | One `{Service}ServiceClient` class with one method per topic | One small, self-contained client class *per topic*, each in its own folder |
| `RequiredTopics` / contract hash | Covers every topic the client was generated for | Scoped to just that one topic (+ health check) |
| Best for | A consumer that calls most/all of a service's topics — one client, one thing to inject | A consumer that calls one or a handful of topics out of a larger service |

The coupling difference is the point of `topic-client`: `ValidateOutboundRouting()`'s startup check
and the client's contract hash are both driven by `RequiredTopics`, so a whole-service client's
consumer is coupled to *every* topic the service happens to expose, including ones it never calls —
an unrelated change to a topic it doesn't use still shows up as a hash change or a startup-check
failure. A per-topic client scopes both to the one topic it actually calls, so unrelated producer
changes neither drag in unused surface nor invalidate the client. The tradeoff is more types to
inject when a consumer genuinely does call most of a service — that's when `client` mode is the
better fit.

Generating a per-topic client directly:

```csharp
var atomicBuilder = new AtomicClientSdkBuilder(new ClientSdkOptions { Namespace = "Acme.Orders.Clients" });
var codeFiles = atomicBuilder.BuildCodeFiles(document);
// -> OrderCreate/OrderCreateServiceClient.cs (namespace Acme.Orders.Clients.OrderCreate), etc.
```

From the CLI:

```bash
benzene build -file Orders.spec.json -output topic-client -namespace Acme.Orders.Clients -directory Generated/
```

## Scoping generation with `--topics`

Both `client` and `topic-client` modes accept a `--topics <a,b,c>` comma-delimited include-list (or,
programmatically, `ClientSdkOptions.Topics`) that limits generation to exactly those topics — the
minimal-coupling-surface option for a consumer that only calls a handful of a service's topics.
Naming a topic scopes it consistently everywhere: in `client` mode, only the named topics get
methods on the class and interface, and only they appear in `RequiredTopics`; in `topic-client`
mode, only the named topics get their own per-topic client at all. `benzene:healthcheck` never needs
naming — the client's `HealthCheckAsync()` method and its `RequiredTopics` entry are always emitted.
A topic named in `--topics` that the document doesn't have fails the build (a non-zero exit naming
the document's actual topics), rather than silently generating a client that's missing what you
asked for.

Reserved Benzene utility topics (`benzene:spec`, `benzene:mesh`, …) are excluded by default in both
modes, so a generated client only covers a service's domain surface unless you opt in
(`ClientSdkOptions.IncludeReservedTopics = true`; there is no CLI flag for this yet).

```bash
# Only these two topics: one client, methods/interface/RequiredTopics scoped to exactly them.
benzene build -file Orders.spec.json -output client -service-name Orders \
  -topics "order:create,order:cancel" -directory Generated/

# The same include-list on topic-client: exactly two per-topic clients, nothing else.
benzene build -file Orders.spec.json -output topic-client -namespace Acme.Orders.Clients \
  -topics "order:create,order:cancel" -directory Generated/
```

## Controlling the generated namespace with `--namespace`

By default the generated namespace is derived from `--lambda-name` or `--service-name` (see
[Generating from a deployed service](#generating-from-a-deployed-service) below). `--namespace`
overrides that: given, it is used *exactly* — no magic suffix — across the client class, its
interface and its DTOs alike (programmatically, `ClientSdkOptions.Namespace`). In `topic-client`
mode it's the *root*: each per-topic client still lands in its own namespace one level below it
(`{Namespace}.{ClientName}`), since every atomic client is self-contained.

## Generating from a deployed service

To generate a client from a service you don't have the source for, fetch its `EventServiceDocument`
from the running service's [`spec` endpoint](spec.md) (the service must have
[`UseSpec()`](reference/middleware.md#usespecstring-topic--spec) in its pipeline), then feed that
document into `MessageClientSdkBuilder` exactly as above. The
[`Benzene.CodeGen.Cli`](reference/packages.md#code-generation--tooling) tool wraps this flow for
command-line use — see the two shapes above, plus `--file`/`--url`/`--mesh` for where the spec comes
from (Phase 1's build artifact, a running service's spec endpoint, or a mesh manifest, all offline
of any deployed AWS Lambda).

## Further Reading

- [OpenAPI Specification](spec.md) - the `spec` endpoint the document comes from
- [Package Reference](reference/packages.md#code-generation--tooling) - the code-generation packages
- [Package Reference: outbound clients](reference/packages.md#outbound-messaging-clients) - transports the client sends over
- [Message Handlers](message-handlers.md) - the contracts the SDK is generated from
