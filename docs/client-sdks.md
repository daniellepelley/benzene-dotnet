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
`HelloWorldAsync(HelloWorldMessage message)` method (plus a header-aware overload) and a `HashCode`
property carrying the hash of the contract it was generated against.

### The contract hash algorithm

`HashCode` is the language-neutral `contractHash` pinned by the cross-language Benzene spec
([`contract-document.md` §6](https://github.com/daniellepelley/Benzene/blob/main/docs/specification/contract-document.md)):
`"sha256:" + lowercase-hex(sha256(canonicalJSON(normalize(document))))`, with `canonicalJSON` being
RFC 8785 (JCS). It's computed by `Benzene.CodeGen.Core.ContractHash` and, on the provider side, by
`Benzene.HealthChecks.Schema.SchemaHealthCheck` — both apply the same domain projection (reserved
`benzene:*` topics excluded, §5.1), so a consumer's generated `HashCode` and a live service's
published hash are directly comparable for the same service. This is a change from the hash values
Benzene generated before this document was written (a non-portable, .NET-serializer-specific
HMAC-SHA256 hash) — see the migration note below.

**Migrating:** hash *values* changed once, for every service, when a Benzene version adopting this
algorithm shipped. Regenerating a client and redeploying the service it targets together produces
matching hashes as before; in a fleet where clients and services upgrade independently, a client
generated against the old algorithm shows one contract-drift *warning* (not a failure — see
[Contract testing](cookbooks/contract-testing.md#mechanism-1--runtime-contract-drift-check)) against
an upgraded service, until it is regenerated. No other generated-client behavior changed.

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

## Registering the client with DI

You don't have to write the registration — the generator emits it. Alongside each client comes a
`{Service}ServiceClientRegistration.cs` with one extension method:

```csharp
// generated
public static class HelloWorldServiceClientRegistration
{
    public static IBenzeneServiceContainer AddHelloWorldServiceClient(this IBenzeneServiceContainer container)
    {
        // Scoped, not singleton: AddOutboundRouting registers IBenzeneMessageSender
        // scoped, so a singleton client would be a captive dependency.
        return container.AddScoped<IHelloWorldServiceClient, HelloWorldServiceClient>();
    }
}
```

Two deliberate choices in there:

- **It extends `IBenzeneServiceContainer`, not `IServiceCollection`.** That is Benzene's own
  container abstraction, so the registration works whatever container is underneath — Autofac,
  `Microsoft.Extensions.DependencyInjection`, anything else. If Benzene is doing the DI, an extension
  on Microsoft's `IServiceCollection` would be useless to a consumer on Autofac. It also means the
  generated code needs no package it didn't already need: `Benzene.Abstractions` is already there for
  `IBenzeneResult`.
- **The lifetime is `Scoped`, and that's not a preference.** `AddOutboundRouting` registers
  `IBenzeneMessageSender` scoped, so a singleton client would capture a scoped dependency. Getting
  that wrong by hand is exactly the footgun this removes.

Call it wherever you configure the container — under `UsingBenzene` when hosting on
`Microsoft.Extensions.DependencyInjection`:

```csharp
services.UsingBenzene(x => x.AddHelloWorldServiceClient());
```

In `topic-client` mode you get **both** shapes: each per-topic client folder carries its own
`Add{Topic}ServiceClient()` (so dropping in a single client folder for a single topic brings its
registration with it, which is the whole point of a self-contained atomic client), plus one
`{Service}ClientsRegistration.cs` at the root whose `Add{Service}Clients()` calls every per-topic
extension — a single line for a consumer that takes several topics off the same service. The
aggregate is named from `--service-name`; without one, only the per-client extensions are emitted.

```csharp
services.UsingBenzene(x => x.AddPaymentsClients());          // all of them
services.UsingBenzene(x => x.AddPaymentsCaptureServiceClient()); // or just the one topic
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
| `RequiredTopics` / contract hash | Covers every topic the client was generated for | Scoped to just that one topic |
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
mode, only the named topics get their own per-topic client at all.
A topic named in `--topics` that the document doesn't have fails the build (a non-zero exit naming
the document's actual topics), rather than silently generating a client that's missing what you
asked for.

### Generated clients cover domain topics only

Benzene's reserved endpoints (`benzene:spec`, `benzene:mesh`, `benzene:healthcheck`, …) are
deliberately kept separate from a service's domain surface: they are framework plumbing, answered by
framework middleware, and a consumer calls them — if at all — through the mesh or its monitoring, not
through a typed domain client. So **no `benzene:*` topic is ever generated into a client**: no
method, no interface member, and above all no `RequiredTopics` entry. They are excluded by default in
both modes, and you can opt a non-health reserved topic back in programmatically
(`ClientSdkOptions.IncludeReservedTopics = true`, or by naming it in `Topics`; there is no CLI flag
for this yet).

`benzene:healthcheck` used to be the exception — every generated client implemented `IHasHealthCheck`,
emitted a `HealthCheckAsync()`, and listed `benzene:healthcheck` in `RequiredTopics` unconditionally.
That last part broke adoption outright: `AddOutboundRouting` registers the outbound-routing start-up
check, which enforces by default, so **any** service that adopted **any** generated client failed to
start until it invented an outbound route for a topic it never meant to call. The health check is now
simply not generated, and a generated client does **not** implement `IHasHealthCheck`.

Nothing is lost by that, because a downstream health call needs no generated code in the first place:
its payload is standard and known up front (fixed by the libraries), unlike domain payloads, which
differ per service and are the reason domain clients are generated at all. Calling a downstream's
health check is a *health-check* concern — like pinging a database or a queue — so it lives in
`Benzene.Clients.HealthChecks` as `AddServiceCheck(...)`, built on the library's own
`ServiceHealthCheckClient`. Pass the generated client's `HashCode` when you also want contract-drift
reporting:

```csharp
app.UseContractsCheck(x => x
    .AddServiceCheck("Payments", new PaymentsServiceClient(sender).HashCode));
```

That check sends `benzene:healthcheck`, so the consumer registers an outbound route for it — now an
explicit opt-in per dependency rather than something forced on every consumer of a generated client.
See [Contract testing](cookbooks/contract-testing.md#mechanism-1--runtime-contract-drift-check) and
[Kubernetes health checks](kubernetes-health-checks.md#client--contract-drift-checks-belong-in-neither-probe).

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

## One-line MSBuild integration

Everything above assumes you run `benzene build` yourself, by hand, whenever a contract changes.
`Benzene.CodeGen.Build` removes that step: commit the producer's `.spec.json` file into your repo
(the same way you'd commit a `.proto` file or an OpenAPI document — Phase 1's
[`Benzene.Descriptor`](reference/packages.md#code-generation--tooling) emits one on every producer
build, or run `benzene spec` and save its output), add one item, and the client regenerates and
compiles automatically:

```xml
<ItemGroup>
  <BenzeneServiceContract Include="contracts/orders.spec.json"
                           Mode="topic-client"
                           ServiceName="Orders"
                           Namespace="Acme.Orders.Clients"
                           Topics="order:create,order:cancel" />
</ItemGroup>
```

`Mode` (default `topic-client`), `ServiceName` (default: the file's own stem) and the optional
`Namespace`/`Topics` map 1:1 onto the `-output`/`-service-name`/`-namespace`/`-topics` flags shown
above — this is the exact same `benzene build -file` flow, just run automatically before every
`CoreCompile` instead of by hand. Regeneration is incremental (an unchanged contract is skipped on
the next build, ordinary MSBuild `Inputs`/`Outputs`, nothing bespoke) and a broken contract **fails
the build** with the CLI's own error message, rather than reporting a silent green build with a stale
or missing client.

See [`examples/CodeGen/Benzene.Examples.CodeGen.Contracts.Consumer`](../examples/CodeGen/Benzene.Examples.CodeGen.Contracts.Consumer)
for a complete, building example, and `src/Benzene.CodeGen.Build/README.md` for the full attribute
reference and how to point it at the CLI another way (a local tool manifest, or running it from
source).

## Further Reading

- [OpenAPI Specification](spec.md) - the `spec` endpoint the document comes from
- [Package Reference](reference/packages.md#code-generation--tooling) - the code-generation packages
- [Package Reference: outbound clients](reference/packages.md#outbound-messaging-clients) - transports the client sends over
- [Message Handlers](message-handlers.md) - the contracts the SDK is generated from
- [Contract Artifacts](contract-artifacts.md) - `Benzene.Descriptor`, the producer-side tool that emits the `.spec.json` file this section's `<BenzeneServiceContract>` item consumes
