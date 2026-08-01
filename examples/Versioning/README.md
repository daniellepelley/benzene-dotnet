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
the example is **chaining**: only the *adjacent upcasts* are declared (the framework synthesises the
field-drop downcasts) —

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

## Wiring — one call

[`StartUp`](Benzene.Examples.Versioning/StartUp.cs) wires all of Mechanism B with a single
`AddPayloadVersioning(...)` in `ConfigureServices`: it declares the four transports (`ForContext<…>()`),
the three versions, and just the two upcasts. From that it derives the schema sets, synthesises the
downcasts, **validates the caster graph at startup** (a missing path throws at deploy, not on the first
message), and enables the casting decorators for each transport. It's order-independent — the AWS
transports register their default request mapper with `TryAdd`, so the decorators win wherever the
transport is wired — so `Configure` just calls `app.UseAwsLambda(...)` with no follow-up casting call to
remember. See the [Message Payload Versioning cookbook](../../docs/cookbooks/message-versioning.md) for the
API in full, including the lower-level primitives `AddPayloadVersioning` wraps.

## Run it

```bash
dotnet test examples/Versioning/Benzene.Examples.Versioning.sln
```

The example is also wired into CI's `examples-build` job (built as part of `Benzene.Examples.sln` and
its tests run in the "Test in-memory examples" step), so a `src/` change that breaks it fails the build.

Everything is in-memory — no AWS account, no localstack. `BenzeneTestHost.Create<StartUp>()` boots the
real `StartUp` the same way a deployed Lambda would, and each test pushes a native transport event
through the front door.
