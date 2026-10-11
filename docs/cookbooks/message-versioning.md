# Message Payload Versioning

Evolve a topic's payload without breaking existing producers. Benzene gives you two tools for this, and
they compose: **route to a version-specific handler**, or keep **one** handler and **cast** older/newer
payloads around it (with multi-step version chains handled for you).

The version is metadata, never part of the body — it travels in the `benzene-version` header (a message
attribute on SQS/SNS, a header on HTTP and the envelope). A message with no version is treated as the
topic's default. The language-neutral rules live in the
[versioning specification](https://github.com/daniellepelley/Benzene/blob/main/docs/specification/versioning.md);
this cookbook is the .NET how-to.

> **Runnable, tested reference.** Everything here is demonstrated end to end in
> [`examples/Versioning`](../../examples/Versioning) — one AWS Lambda `StartUp` over four transports, with
> 11 tests that boot the real startup via `BenzeneTestHost`. It's built and its tests run in CI
> (`examples-build`), so it stays honest. Read it alongside this page.

## When to use which

| | Mechanism A — handler-version dispatch | Mechanism B — transparent casting |
|---|---|---|
| **Shape** | Two versions are genuinely different logic | Newer versions mostly *add* fields |
| **You write** | One handler per version | One handler, on the newest schema |
| **The framework** | Routes by version to the right handler | Up/down-casts the payload around the handler |
| **Reach for it when** | v2 does something v1 doesn't | v2 is v1 plus a field, and you don't want to fork the handler |

You can use both in one service, on different topics — they don't interfere (a topic with no casters is
never cast; a topic with one handler is never version-routed).

## Mechanism A — route to a version-specific handler

Give each handler the same topic but a different version, via the second argument of `[Message]`:

```csharp
[Message("order:create", "v1", IsDefault = true)]
public class CreateOrderV1Handler : IMessageHandler<V1.CreateOrder, V1.OrderAccepted>
{
    public Task<IBenzeneResult<V1.OrderAccepted>> HandleAsync(V1.CreateOrder request) { /* ... */ }
}

[Message("order:create", "v2")]
public class CreateOrderV2Handler : IMessageHandler<V2.CreateOrder, V2.OrderAccepted>
{
    public Task<IBenzeneResult<V2.OrderAccepted>> HandleAsync(V2.CreateOrder request) { /* ... */ }
}
```

The router reads the incoming version with `IMessageVersionGetter<TContext>` — the default
`HeaderMessageVersionGetter` reads **one** header, `benzene-version` — and hands it to `IVersionSelector`.
The default selector applies the spec's rule (core-concepts §2):

- `benzene-version: v1` → the V1 handler (an exact match).
- `benzene-version: v2` → the V2 handler.
- `benzene-version: v9` → `not-found`, naming the topic and `v9`. There is no "nearest" or "highest
  available" fallback: serving another version's behaviour to a caller that named a version is the failure
  versioning exists to prevent.
- **no version** → the topic's **default handler**: an unversioned handler if there is one, else the one
  declared `IsDefault = true` (above: V1, the oldest shape the topic still accepts, so producers written
  before versioning keep working). With neither, `not-found` — and a start-up warning names the topic.

That's all the wiring: register the handlers' assembly (`AddMessageHandlers(...)`) as usual. No caster
config is involved. Registering explicitly instead of by attribute, the same declaration is
`router.AddMessageHandler<CreateOrderV1Handler, V1.CreateOrder, V1.OrderAccepted>("order:create", "v1", isDefault: true)`.

**One header, no fallback list.** A producer that sends a plain `version` or `x-version` header for its own
purposes (an API or SDK version) is not sending a Benzene version: those are ordinary headers and select
nothing. To use a different name, rename the header in the one place every reserved name lives —
`services.AddMessageVersionHeaderName("schema-version")`, or an `IBenzeneWireNames` registration carrying
it — and the configured name *replaces* `benzene-version`. On HTTP, a `/v{version}` route segment
(`AddHttpVersioning()`) supplies the version exactly as if the header had been sent.

If you want the old "exact, else the highest registered version" behaviour, it is still available as an
explicit opt-in: register `HighestAvailableVersionSelector` as the `IVersionSelector`.

This version-augmentation (combining the topic with `IMessageVersionGetter<TContext>` before handler
lookup) is centralized behind one shared helper, `GetVersionedTopic` on `IMessageTopicGetter<TContext>`
(`Benzene.Abstractions.MessageHandlers.Mappers`) — `MessageRouter` and every other built-in consumer
that resolves a handler by topic (`Benzene.JsonSchema`'s request validation, `Benzene.Diagnostics`'
`ActivityMiddlewareDecorator`/`UseBenzeneEnrichment`, `Benzene.Aws.Lambda.XRay`'s subsegment tagging)
call it, so a multi-version topic is validated and traced against the version the request actually
declares, never the topic's default handler by accident. A custom middleware that
looks up a handler by topic should call the same helper rather than re-deriving the combination.

## Mechanism B — one handler, transparent casting (with chaining)

Keep a single handler on the newest schema and let Benzene cast older payloads up to it, and the response
back down to the caller's version — all from one call, `AddPayloadVersioning`:

```csharp
services.UsingBenzene(x => x
    .AddMessageHandlers(typeof(StartUp).Assembly)
    .AddContextItems()
    .AddPayloadVersioning(versioning => versioning
        // the transports this service should cast for
        .ForContext<BenzeneMessageContext>()
        .ForContext<ApiGatewayContext>()
        .ForContext<SqsMessageContext>()
        .ForContext<SnsRecordContext>()
        .Topic("inventory:adjust", topic => topic
            .Version<V1.InventoryAdjustment>("v1")
            .Version<V2.InventoryAdjustment>("v2")
            .Version<V3.InventoryAdjustment>("v3")
            // only the UPcasts; seed the field each version introduced
            .Upcast<V1.InventoryAdjustment, V2.InventoryAdjustment>(f => f.RegisterInitValue(o => o.WarehouseId, "wh-main"))
            .Upcast<V2.InventoryAdjustment, V3.InventoryAdjustment>(f => f.RegisterInitValue(o => o.Reason, "unspecified")))));
```

That one call:

- **Declares each version once** (name ↔ CLR type) and derives the from/to schema sets from them, so there is nothing to keep in sync.
- **Needs only the adjacent upcasts.** There is deliberately no direct `v1 → v3` caster: when a v1 payload arrives for the v3 handler, the framework composes the path **v1 → v2 → v3** (breadth-first) — that's **caster chaining** — and each hop seeds its field, so the handler sees a payload with both `WarehouseId` (from the v1→v2 hop) and `Reason` (from the v2→v3 hop) populated, even though the v1 producer sent neither.
- **Synthesises the downcasts.** A field-drop `v3 → v2 → v1` downcaster is derived from each upcast, so old clients get responses in their own shape without you writing the reverse casters. Supply an explicit `.Downcast<TNew, TOld>(...)` only when the older version needs a field *re-derived* rather than simply dropped.
- **Validates eagerly, at startup.** The caster graph is expanded when `AddPayloadVersioning` runs, so a missing conversion path — or a version declared with no reachable caster — throws *then*, at deploy time, not on the first message in production.
- **Is order-independent.** It enables the casting decorators for each `ForContext` here in `ConfigureServices`; every built-in transport registers its default request mapper with `TryAdd`, so these decorators win no matter when the transport is wired in `Configure`. There is no post-transport call to remember, and no way to half-wire it.

<details>
<summary>Lower-level API (advanced / non-standard hosts)</summary>

`AddPayloadVersioning` wraps three primitives you can use directly if you need to:
`RegisterSchemaCastDefinitions` (register each `ISchemaCaster`), `RegisterPayloadSchemaVersions` (build the
`ISchemaCasters` aggregate), and `UsePayloadVersionCasting<TContext>()` (wrap the per-context mappers).
Two caveats the one-call API handles for you: `RegisterPayloadSchemaVersions` expands the caster graph
**lazily**, so a missing path throws on the first message rather than at startup; and
`UsePayloadVersionCasting<TContext>()` must be the **winning** registration of `IRequestMapper<TContext>`.
Every built-in transport registers that mapper with `TryAdd`, so calling it in `ConfigureServices` wins;
only a custom host that registers its mapper with a last-wins `AddScoped` needs the call placed *after*
the transport (e.g. via `app.Register(...)` in `Configure`). Prefer `AddPayloadVersioning` unless you have
a reason not to.

</details>

### The "default version" for a cast topic

With **no** `benzene-version`, the casting decorator delegates straight through: the body is deserialized
directly as the handler's own (newest) type, no caster runs. So the handler's declared request type *is*
the canonical/default version — `ToSchemas` names the version(s) the handler understands.

### How a versioned message reaches the one handler

The `inventory:adjust` handler above is **unversioned**, yet a message carrying `benzene-version: v1` still
reaches it — not because a lone handler takes every version (it doesn't: a version the service cannot
handle is `not-found`), but by step 2 of version selection: when no handler is registered for exactly
`v1`, the handler whose request type `v1` can be **cast** to is chosen. `AddPayloadVersioning` exposes its
casts to the selector (`IPayloadVersionCasts`), so `v1`/`v2` reach the V3 handler through their casts and
`v3` reaches it because it names the handler's own type. When one version can be cast to several handlers,
the topic's default handler wins if it is among them; otherwise the handler with the greatest version
(ordinal comparison, unversioned lowest) — a deterministic tie-break that prefers casting forward over
casting back, and that the start-up checks log so you can see it.

## Testing it

Boot the real `StartUp` and set the version like any other header — a plain string:

```csharp
var host = new AwsLambdaBenzeneTestHost(BenzeneTestHost.Create<StartUp>().BuildAwsLambdaHost());

var request = new BenzeneMessageRequest
{
    Topic = "inventory:adjust",
    Body = JsonConvert.SerializeObject(new V1.InventoryAdjustment { Sku = "ABC", Delta = 5 }),
    Headers = new Dictionary<string, string> { { "benzene-version", "v1" } }
};

var response = await host.SendEventAsync<BenzeneMessageResponse>(request);
// response.Body is v1-shaped (downcast); a value the v1→v2→v3 upcast seeded proves the chain ran.
```

On the client side, `SendMessageAsync(topic, message, version: "v1")` (or `headers.WithVersion("v1")`)
sets the same `benzene-version` header for you. See
[`examples/Versioning`'s tests](../../examples/Versioning/Benzene.Examples.Versioning.Tests) for the full
set: routing over the envelope/SQS/SNS, and chaining over the envelope/API Gateway/SQS, plus the
single-hop, no-cast, and no-version-bypass cases.

## Further reading

- [`examples/Versioning`](../../examples/Versioning) — the runnable, CI-tested worked example this page follows
- [Versioning specification](https://github.com/daniellepelley/Benzene/blob/main/docs/specification/versioning.md) — the language-neutral contract
- [Attributes Reference](../reference/attributes.md) — the `[Message(topic, version)]` attribute
- [Contract Testing (schema compatibility)](contract-testing.md) — catch a breaking payload change *before* it ships, complementing versioning's ability to absorb a compatible one
