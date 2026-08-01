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
[Message("order:create", "v1")]
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

The router reads the incoming version with `IMessageVersionGetter<TContext>` (the default
`HeaderMessageVersionGetter` scans `benzene-version` → `version` → `x-version`) and hands it to
`IVersionSelector`. The default selector returns the handler for the **exact** version if one exists,
otherwise the **highest** registered version. So:

- `benzene-version: v1` → the V1 handler.
- `benzene-version: v2` → the V2 handler.
- **no version** → the highest registered version (`v2`) — i.e. the newest handler is the topic's default.

That's all the wiring: register the handlers' assembly (`AddMessageHandlers(...)`) as usual. No caster
config is involved.

## Mechanism B — one handler, transparent casting (with chaining)

Keep a single handler on the newest schema and let Benzene cast older payloads up to it, and the response
back down to the caller's version — all from one call, `AddPayloadVersioning`:

```csharp
services.UsingBenzene(x => x
    .AddBenzene()
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
- **Is order-independent.** It enables the casting decorators for each `ForContext` here in `ConfigureServices`; the AWS transports register their default request mapper with `TryAdd`, so these decorators win no matter when the transport is wired in `Configure`. There is no post-transport call to remember, and no way to half-wire it.

<details>
<summary>Lower-level API (advanced / non-standard hosts)</summary>

`AddPayloadVersioning` wraps three primitives you can use directly if you need to:
`RegisterSchemaCastDefinitions` (register each `ISchemaCaster`), `RegisterPayloadSchemaVersions` (build the
`ISchemaCasters` aggregate), and `UsePayloadVersionCasting<TContext>()` (wrap the per-context mappers).
Two caveats the one-call API handles for you: `RegisterPayloadSchemaVersions` expands the caster graph
**lazily**, so a missing path throws on the first message rather than at startup; and
`UsePayloadVersionCasting<TContext>()` must be the **last** registration of `IRequestMapper<TContext>`, so
on a host whose transport registers that mapper with a last-wins `AddScoped` you must call it *after* the
transport (e.g. via `app.Register(...)` in `Configure`). Prefer `AddPayloadVersioning` unless you have a
reason not to.

</details>

### The "default version" for a cast topic

With **no** `benzene-version`, the casting decorator delegates straight through: the body is deserialized
directly as the handler's own (newest) type, no caster runs. So the handler's declared request type *is*
the canonical/default version — `ToSchemas` names the version(s) the handler understands.

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
