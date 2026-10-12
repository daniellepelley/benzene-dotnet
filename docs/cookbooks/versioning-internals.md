# Versioning internals

How `Benzene.Core.Versioning` and handler-version dispatch are built — the reading side of the
[message versioning cookbook](message-versioning.md). The behaviour itself is specified once, for
every language, in the spec's [versioning document](https://github.com/daniellepelley/Benzene/blob/main/docs/specification/versioning.md) and
[core-concepts §2](https://github.com/daniellepelley/Benzene/blob/main/docs/specification/core-concepts.md#2-topic); this page is the .NET implementation of it.
Section references of the form "§2.2" or "§4.4" are to the spec's earlier `versioning.md`, from
which this page was moved (spec Phase 2, 2026-10-11).

## Reading the version: `IMessageVersionGetter<TContext>`

Extracting the version is a **new, replaceable extension point**, following the same
producer/consumer, default/override shape every existing convention uses (the [design principles](https://github.com/daniellepelley/Benzene/blob/main/docs/guides/design-principles.md) §4):

```csharp
namespace Benzene.Abstractions.Messages.Mappers;

public interface IMessageVersionGetter<TContext>
{
    // Empty/null means "no version signalled" (§2.2), not an error.
    string? GetVersion(TContext context);
}
```

This is deliberately the same shape as the already-shipped `IMessageTopicGetter<TContext>`
(`Benzene.Abstractions.Messages.Mappers`) — same namespace, same one-method extraction contract,
same "null means absent, not an error" rule. Every transport binding registers a default
implementation (HTTP: route parameter, then the header; every other transport: the header),
replaceable exactly like a topic getter is.

The header name (§2.1) is an option on the default implementation, not a hard-coded constant, so
an application can rename it without writing a new `IMessageVersionGetter<TContext>`, while still
being free to replace the whole getter (e.g. for a version signal that isn't a header or route
parameter at all). Because the name is an **application-wide** contract — the same regardless of
transport, unlike a transport's topic attribute/property key — it is set in **one place** that
every transport's getter resolves at message-handle time, rather than per transport. Because every
transport already registers an `IMessageHeadersGetter<TContext>` mapping its native metadata onto
the flat header dictionary (wire-contracts.md §2), one generic `HeaderMessageVersionGetter<TContext>`
built against that (not the native transport type) serves as the default for every transport
except HTTP — which layers the route-parameter check in front of the same header read.

**This closes a real gap in the current implementation, independent of which mechanism (§3 or §4)
a service adopts**: every existing `IMessageTopicGetter<TContext>` implementation constructs
`new Topic(id)` — never `new Topic(id, version)` — so `ITopic.Version` is always empty coming off
the wire today, even though `IVersionSelector`/`MessageAttribute`'s dispatch-by-version machinery
(§3) has been fully wired on the lookup side since it shipped. Nothing currently populates the
producer side of that contract. `IMessageVersionGetter<TContext>` is that missing producer;
`MessageRouter<TContext>` combines its output with the existing topic getter's output into one
`ITopic(id, version)` before calling `IMessageHandlerDefinitionLookUp.FindHandler`, which is the
one-line change §3 needs.

## Sugar: a casting handler

A team that wants only *one* real implementation (the latest) but still needs to accept an older
producer can get there today, with **zero framework changes**, by writing a small V1 handler that
upcasts and delegates:

```csharp
[Message("order:create", "V1")]
public class CreateOrderV1MessageHandler : IMessageHandler<CreateOrderRequestV1, CreateOrderResponseV1>
{
    private readonly CreateOrderV2MessageHandler _v2; // or a shared internal service either calls

    public async Task<IBenzeneResult<CreateOrderResponseV1>> HandleAsync(CreateOrderRequestV1 request, ...)
    {
        var v2Request = _caster.Cast(request); // ICaster<V1,V2> from Benzene.Core.Versioning (§4.4)
        var v2Result = await _v2.HandleAsync(v2Request, ...);
        return v2Result.Map(_downcaster.Cast); // ICaster<V2,V1>
    }
}
```

This is application-level composition over already-shipped pieces
(`Benzene.Core.Versioning.CasterBuilder.CasterFactory<TFrom,TTo>` for the cast, ordinary DI for the
delegation) — it needs no new abstraction in the message-handlers packages at all, which is why
it's framed as sugar rather than a third mechanism: it is mechanism A, with the duplicate code
shrunk to one small forwarding handler per retired version. **This is the recommended starting
point** for a team not yet ready to adopt §4's fully transparent casting, and MAY be documented as
a cookbook (`docs/cookbooks/`) rather than framework code once implemented.

## Payload casting: how the decorators work

**Status: implemented** (`Benzene.Core.Versioning`, opt-in per transport via
`UsePayloadVersionCasting<TContext>()`). One handler serves a topic, written
against the **latest** schema version. Producers on older (or newer) versions are transparently
upcast (or downcast) at the edges of the pipeline; the handler never sees any version but its own.

**Name the versions' schemas.** The usual layout keeps one type name in a per-version namespace
(`Model.V1.TakePaymentRequest`, `Model.V2.TakePaymentRequest`). Both reach the contract document, and
its component names are simple type names (contract-document §4.1), so the two collide — a
start-up failure under `UseBenzeneCloudService`, which checks the document before the first message.
Declare a distinct name on each: `[SchemaName("TakePaymentRequestV1")]` (see [spec.md](../spec.md)).

### Request path

Hooks into `IRequestMapper<TContext>` (`Benzene.Core.MessageHandlers.Request`), which already sits
exactly at the right seam: `MessageRouter<TContext>` resolves the topic and handler definition
first (so `messageHandlerDefinition.RequestType` — the canonical/latest shape — is known) *before*
`DeferredRequestMapper<TContext>.GetRequest<TRequest>()` calls
`IRequestMapper<TContext>.GetBody<TRequest>(context)` to materialize the request. A casting request
mapper is a **decorator** around the existing one:

1. Read the incoming version via `IMessageVersionGetter<TContext>` (§2.3) and the topic via the
   existing `IMessageTopicGetter<TContext>`.
2. If no version was signalled, or the topic has no registered schema casters, delegate straight to
   the inner `IRequestMapper<TContext>` unchanged — **zero overhead, zero behavior change** for any
   topic that doesn't opt in (the [design principles](https://github.com/daniellepelley/Benzene/blob/main/docs/guides/design-principles.md)' "never require it" rule).
3. Otherwise, look up the caster for `(incomingVersion, canonicalVersion, topic)` via the
   already-shipped `ISchemaCasters.GetSchemaCaster(...)` (`Benzene.Core.Versioning.Schemas`). The
   returned `ISchemaCaster.FromType` **is** the incoming payload's CLR shape — no separate
   version-to-type registry is needed; it falls out of the casters already registered for the topic.
4. Deserialize the raw body as `FromType` using the **negotiated `ISerializer`** (not a JSON-specific
   path — `ISerializer.Deserialize(Type, string)`, or `IPayloadSerializer.Deserialize(Type,
   ReadOnlySpan<byte>)` on the byte-oriented path `RequestMapper<TContext>` already prefers when
   available). This is what makes the mechanism serializer-agnostic: MessagePack, XML, and Avro all
   already implement `ISerializer`/`IPayloadSerializer` with the same `Type`-parameterized shape.
5. Invoke the caster (`ICaster<TFrom,TTo>.Cast`, dispatched via reflection the same way the
   existing `PayloadDeserializer`/`SchemaCastDefinitionsExpander` already do) to upcast into
   `TRequest`, and return that.

### Long-lived version back-catalogs and shortcut casters

A service supporting many still-live versions (e.g. currently on V5 but still accepting producers
as old as V1) does not need a direct V1→V5 caster registered, and does not always chain step by
step through every intervening version either. `ISchemaCasters.GetSchemaCaster(...)` is backed by
`SchemaCastDefinitionsExpander`, already shipped and unchanged by this proposal (§4.4), which
resolves any requested `(from, to, topic)` pair by:

1. Reusing a directly-registered caster for that exact pair if one exists.
2. Otherwise, finding the shortest path (fewest composed casters) between the two versions over
   whatever casters *are* registered for the topic, via breadth-first search
   (`SchemaCastDefinitionsExpander.GetChain`), and composing them with `CompositeCaster<TFrom,
   TIntermediate,TTo>`.

Because it is breadth-first, a **shortcut caster is automatically preferred over a longer chain
through intermediate versions whenever both exist** — exactly the "V1→V3 direct, so use V1→V3,
V3→V4, V4→V5 instead of V1→V2→V3→V4→V5" scenario this design needs to support: if V1→V2,
V2→V3, V3→V4, V4→V5, **and** V1→V3 are all registered, resolving V1→V5 composes `[V1→V3 (direct),
V3→V4, V4→V5]` (3 casters), never revisiting V3 via the longer V1→V2→V3 route, because BFS marks a
version visited — and therefore never reconsiders it — the first time any edge reaches it, which
for V3 happens on the direct edge in the same BFS layer the V1→V2 edge is explored (both are
one hop from V1). **No change is needed for this to work — it is already exhaustively covered by
the existing `SchemaCastDefinitionsExpanderTest` shortcut-preference test.** Registering fewer
shortcut casters still works (the full step-by-step chain is the fallback), and registering *more*
shortcuts only ever shortens future chains — there is no scenario where adding a shortcut caster
makes an existing resolution worse or ambiguous, since exact-pair reuse (step 1 above) and shortest-
path composition (step 2) are both deterministic given a fixed set of registered casters.

### Response path

Symmetric, and hooks into `IResponsePayloadMapper<TContext>`
(`Benzene.Core.MessageHandlers.Response`) the same way — again a decorator, not a replacement:

1. The handler has already produced its result in the **canonical** response type
   (`messageHandlerDefinition.ResponseType`) — the handler is never aware any casting happens.
2. Read the version again via `IMessageVersionGetter<TContext>` against the same `context` the
   decorator already has (no cross-request-to-response state needed — the getter is a pure,
   idempotent read of the immutable context, called twice rather than threaded through, keeping this
   simpler than the `PresetTopicHolder` pattern `Benzene.Core.MessageHandlers` uses elsewhere for a
   genuinely different problem — a topic *override*, not a repeatable read).
3. **Default: symmetric versioning** — respond in the same version the request declared, so a V1
   producer always gets a V1 response back without needing a separate "Accept-Version" negotiation.
   This default MUST be overridable (spec versioning §5) for services that want
   asymmetric negotiation; the override point is replacing this decorator's registration.
4. Downcast the canonical payload via `ISchemaCasters.TryGetSchemaCaster(topic, canonicalType,
   requestedVersion, out caster)` (reverse direction from §4.1) and serialize the result as
   `caster.ToType` with the negotiated `ISerializer` — again format-agnostic.

### As implemented

- **Opt-in per transport**: `services.UsePayloadVersionCasting<TContext>()` wraps that context's
  `IRequestMapper<TContext>`/`IResponsePayloadMapper<TContext>` with `CastingRequestMapper<TContext>`
  / `CastingResponsePayloadMapper<TContext>`. Call it **after** the transport's own registration
  (`UseHttp`/`AddSqs`/… + `AddMessageHandlers`), so the closed decorator registrations win, and pair
  it with `RegisterSchemaCastDefinitions` + `RegisterPayloadSchemaVersions`.
- **Type-keyed lookup, not a version-string pair**: neither decorator ever knows both version
  strings — the request side has `(topic, incomingVersion, TRequest)`, the response side
  `(topic, ResponseType, requestedVersion)`. Two `TryGetSchemaCaster` overloads on `ISchemaCasters`
  match on one version string + one CLR `Type`, backed by O(1) indexes built once on the singleton.
  This is also what resolves the "multiple canonical versions per topic" open question below:
  matching by the handler's actual request/response `Type` sidesteps ever needing a canonical
  version string.
- **Register both directions.** The upcast (request) and downcast (response) are *different* casters:
  V1→V2 does not give you V2→V1. `RegisterPayloadSchemaVersions`'s expander only generates the
  `FromSchemas → ToSchemas` direction, so **symmetric response casting requires the reverse pairs to
  exist too** — the simplest way is to list every live version in *both* `FromSchemas` and
  `ToSchemas` (the expander then composes every needed pair, up and down, reusing direct casters and
  chaining where none exists). A topic that only ever upcasts requests and doesn't downcast responses
  needs only the forward direction.
- **Bespoke request mappers (gRPC)**: `UsePayloadVersionCasting<TContext>` wraps the framework-default
  `MultiSerializerOptionsRequestMapper<TContext>` on the request side, which is not gRPC's real mapper.
  A transport with a bespoke request mapper re-points the request side at *its own* mapper via
  `UsePayloadVersionRequestCasting<TContext, TInnerRequestMapper>()`; the decorator still reads the wire
  body through that mapper (so protobuf-JSON bridging runs) before upcasting. For gRPC this is packaged
  as `Benzene.Grpc.Versioning`'s `AddGrpcPayloadVersioning(...)` — same caster-declaration surface as
  `AddPayloadVersioning`, request side only (gRPC writes its response straight to protobuf via its result
  setter, so there is no response payload mapper to downcast). The response side of the serializer-based
  transports wraps the universal `DefaultResponsePayloadMapper<TContext>` and is unaffected.

### Degradation

Per the spec's degradation rule (core-concepts §11), this capability's requirement and degradation:

| Requires | Why | Degradation when declined |
|---|---|---|
| `Benzene.Core.Versioning` schema casters registered for the topic (§4.4) | The decorators no-op without a registered `ISchemaCasters` entry for `(topic, incoming/target version)` | The request/response mapper decorators pass through unchanged — behaves exactly as an unversioned topic; not an error |
| `IMessageVersionGetter<TContext>` returning a real signal | Casting only ever triggers for a version that differs from the canonical one | A topic with no version signalled always takes the canonical path — the same "absent means default" rule as §2.2 |
