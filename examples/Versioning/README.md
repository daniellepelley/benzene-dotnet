# Benzene Versioning Example

A runnable, tested demonstration of **payload schema versioning**
([docs/specification/versioning.md](../../docs/specification/versioning.md)) — one AWS Lambda `StartUp`
hosting four transports (the BenzeneMessage envelope, API Gateway, SQS and SNS), dogfooding **both**
versioning axes Benzene ships.

The version always travels as metadata — the `benzene-version` header (a message attribute on SQS/SNS,
a header on API Gateway and the envelope) — never inside the payload body. A message with no version
signal is treated as the topic's default.

## Mechanism A — handler-version dispatch (`order:create`)

Two genuinely different request shapes, two handlers, no casting: the incoming version picks the handler.

| Version | Handler | Request shape |
|---|---|---|
| `v1` | [`CreateOrderV1MessageHandler`](Benzene.Examples.Versioning/Handlers/CreateOrderV1MessageHandler.cs) | flat `CustomerName` |
| `v2` | [`CreateOrderV2MessageHandler`](Benzene.Examples.Versioning/Handlers/CreateOrderV2MessageHandler.cs) | `FirstName`/`LastName` + `Currency` |

Registered with `[Message("order:create", "v1")]` / `[Message("order:create", "v2")]`. When a producer
sends **no** version, `IVersionSelector` falls back to the highest registered version (`v2`) — so V2 is
the topic's default handler. Proven end to end over the envelope, SQS and SNS in
[`HandlerVersionRoutingTests`](Benzene.Examples.Versioning.Tests/HandlerVersionRoutingTests.cs).

## Mechanism B — transparent payload casting with **caster chaining** (`inventory:adjust`)

Three payload versions of one type, but only **one** handler — written against the newest, V3. Older
producers are cast to V3 transparently, and the response cast back to the caller's version. The point of
the example is **chaining**: only the *adjacent* casters are registered —

```
V1 ⇄ V2 ⇄ V3          (there is deliberately NO direct V1 ⇄ V3 caster)
```

— so a **V1** request is upcast by composing **V1→V2→V3**, and its response downcast **V3→V2→V1**.
`SchemaCastDefinitionsExpander` finds and composes the hops (breadth-first) at startup.

Each version adds one field, seeded by the hop that introduces it:

| Version | Field it adds | Seeded by |
|---|---|---|
| V2 | `WarehouseId` (`"wh-main"`) | the V1→V2 upcaster |
| V3 | `Reason` (`"unspecified"`) | the V2→V3 upcaster |

The single V3 handler echoes both into a `Trace` field present in every version, so it survives the
downcast — which is how a plain **V1** request/response can prove *both* hops ran (see
[`CasterChainingTests`](Benzene.Examples.Versioning.Tests/CasterChainingTests.cs), covering the V1→V2→V3
chain + V3→V2→V1 downcast over the envelope, API Gateway and SQS, plus the single-hop, no-cast, and
no-version-bypass cases).

## Wiring note — casting decorators go in `Configure`, after the transports

Casting is enabled per transport with `UsePayloadVersionCasting<TContext>()`. Each AWS transport
(`AddApiGateway`/`AddSqs`/`AddSns`) registers its own `IRequestMapper<TContext>` with a **last-wins**
`AddScoped` when it is wired in `Configure`. If the casting decorators were registered earlier (in
`ConfigureServices`) the transport would overwrite them and the request upcast would silently not run.
So [`StartUp`](Benzene.Examples.Versioning/StartUp.cs) registers the **caster definitions** in
`ConfigureServices` but enables the **decorators** via `app.Register(...)` in `Configure`, *after*
`app.UseAwsLambda(...)` — the final, winning registration. (The BenzeneMessage envelope's mapper is
`TryAdd`, so it isn't affected either way; the AWS event transports are.)

## Run it

The examples are not part of the main CI gate; build and test this one directly:

```bash
dotnet test examples/Versioning/Benzene.Examples.Versioning.sln
```

Everything is in-memory — no AWS account, no localstack. `BenzeneTestHost.Create<StartUp>()` boots the
real `StartUp` the same way a deployed Lambda would, and each test pushes a native transport event
through the front door.
