# Benzene.ClaimCheck

## What this package does
Offloading for outbound message payloads that would otherwise exceed a transport's size limit
(SQS/SNS/EventBridge 256 KB, Service Bus standard 256 KB, Azure Queue Storage 64 KB). This package
ships a middleware **pair**, not a client feature: `ClaimCheckOffloadMiddleware` runs on the outbound
`OutboundContext` route pipeline, serializes and measures the request, and - at or over a configurable
threshold - stores the serialized body in a pluggable `IClaimCheckStore` and replaces the request with
a tiny placeholder, carrying the store-issued reference on the `benzene-claim-check` wire header.
`ClaimCheckHydrateMiddleware<TContext>` runs on the receiving side's inbound transport pipeline, reads
that header, resolves the reference back through the (receiver's own) `IClaimCheckStore`, and replaces
the raw message body with the stored content before deserialization. See `work/archive/claim-check-plan-2026-08.md`
§1-§3 for the full design reasoning (why middleware and not a client/transport feature, the wire
shape, the store-abstraction contract, and the retention/failure posture) - it is the authority this
package implements.

Persistence is pluggable via `IClaimCheckStore`, mirroring `Benzene.Idempotency`'s shape. **This
package ships `InMemoryClaimCheckStore`** (single-process, for one host / tests / local development).
For a real deployment, use a durable, shared store: `Benzene.ClaimCheck.Aws.S3` and
`Benzene.ClaimCheck.Azure.Blob` are the sibling production stores (see `work/archive/claim-check-plan-2026-08.md`
Phases 4-5) - a payload offloaded by one instance must be resolvable by whichever instance receives
the message, which an in-process dictionary cannot do across a fleet.

## Key types
- `ClaimCheckOffloadMiddleware` (`IMiddleware<OutboundContext>`) - the outbound half. Serializes
  `context.Request` with the configured `ISerializer` (default `Benzene.Clients.JsonSerializer` - the
  same default every outbound transport converter uses), measures its UTF-8 byte count, and either
  lets the message through untouched (below `ThresholdBytes` and `AlwaysOffload` is off) or puts the
  serialized body into the store, stamps the reference onto the header, and replaces `context.Request`
  with a `ClaimCheckPlaceholder`.
- `ClaimCheckHydrateMiddleware<TContext>` (`IMiddleware<TContext>`) - the inbound half. Reads the
  claim-check header via `IMessageHeadersGetter<TContext>`; if absent, passes through without touching
  the store. If present, resolves it via `IClaimCheckStore.GetAsync` (a `null` result throws
  `ClaimCheckNotFoundException` - never a silent skip) and writes the resolved body back via
  `IMessageBodySetter<TContext>` (throws a descriptive `InvalidOperationException` naming the context
  type when no setter is registered for that transport - see `Benzene.Abstractions.Messages`'s
  `IMessageBodySetter<TContext>`, which exists today with zero implementations; the transport packages
  supply them, one 5-line setter per transport).
- Both middlewares resolve the scope's ambient `ICancellationTokenAccessor` (optional; `null` observes
  no cancellation) and pass its token into the `Get`/`PutAsync` call, read fresh at the point of use -
  so a wrapping `UseTimeout` or a real transport cancellation actually bounds the store call.
- `IClaimCheckStore` - the pluggable persistence contract: `PutAsync(body, context)` stores a
  serialized wire body and returns an opaque, store-issued reference in URI form
  (`scheme://location/key`); `GetAsync(reference)` resolves it, returning `null` for not-found/expired
  and **throwing** `ClaimCheckStoreMismatchException` for a reference outside the store's own
  configuration - a store must never fetch a reference it did not (or could not have) issued.
- `InMemoryClaimCheckStore` - default in-process store (dictionary + lock + TTL), issuing
  `memory://{topic}/{key}` references. Single-instance only, same caveat as
  `InMemoryIdempotencyStore`. Reclamation, without a background thread: `GetAsync` evicts an entry it
  finds expired at read time; `PutAsync` also sweeps every expired entry (at most once per
  `SweepInterval`, 1 minute) so a payload that is never read back at all - a fan-out sibling nobody
  consumes, an undelivered message - still gets reclaimed, bounding growth wherever it originates.
- `ClaimCheckOptions` - `ThresholdBytes` (default 192 KiB / 196,608 bytes), `AlwaysOffload`,
  `HeaderName` (default `ClaimCheckHeaders.ClaimCheck`), `Serializer` (`null` -&gt; `JsonSerializer`).
- `ClaimCheckHeaders.ClaimCheck` - the reserved default header name, `benzene-claim-check`.
- `ClaimCheckPlaceholder` - the offloaded message's tiny wire body: one field,
  `_benzeneClaimCheck`, named with the literal wire key (not a PascalCase property plus an
  attribute) so it survives every `ISerializer` implementation's naming policy unchanged - see the
  type's own remarks. The header is authoritative; a wired consumer never reads this body.
- `ClaimCheckPutContext` - carries the topic being sent on, for a store's key partitioning.
- `ClaimCheckNotFoundException` / `ClaimCheckStoreMismatchException` - the two failure modes above.
- `Extensions` - `UseClaimCheck(configure?)` (offload, on an `IMiddlewarePipelineBuilder<OutboundContext>`),
  `UseClaimCheck<TContext>(configure?)` (hydrate), and `AddInMemoryClaimCheckStore(ttl?)` (DI).

## Usage
```csharp
// DI: register a store once.
services.AddInMemoryClaimCheckStore(TimeSpan.FromHours(24));   // or a durable store package

// Outbound: last non-terminal step of the route, before the transport converter.
routing.Route("payments:capture", pipeline => pipeline
    .UseCorrelationId()
    .UseClaimCheck()          // offload
    .UseSqs(queueUrl));

// Inbound: after the observability prelude, before UseMessageHandlers.
aws.UseSqs(sqs => Observe(sqs)
    .UseClaimCheck()          // hydrate - TContext inferred as SqsMessageContext
    .UseMessageHandlers(handlers, ...));
```

## Serializer-consistency caveat
The offload middleware's serializer must match the transport converter's serializer for that same
route. Both default to `Benzene.Clients.JsonSerializer`; a route that passes a custom `ISerializer` to
its converter (`UseSqs(url, mySerializer)`) must pass the same instance to
`UseClaimCheck(o => o.Serializer = mySerializer)`, or the bytes measured and stored will not be what
the converter would actually have produced for the un-offloaded message. This is a real, load-bearing
coupling, documented rather than hidden - hoisting serialization out of every outbound converter so it
disappears is a real future refactor, out of scope here.

## Retention, fan-out, and failure honesty
- **No delete-on-consume.** SNS-style fan-out delivers one offloaded message to several independent
  consumers, and at-least-once transports redeliver; deleting at read time would starve siblings or
  make a retry permanently unhydratable. Retention is **TTL-based expiry owned by infrastructure** (an
  S3 lifecycle rule / Azure Blob lifecycle-management policy on the store's prefix) - the stores do not
  create that policy themselves, the same posture as `Benzene.Idempotency.DynamoDb`'s TTL requirement.
  Size the TTL to exceed the longest path from send to last possible consumption (queue retention plus
  any DLQ redrive window).
- **A missing or expired claim on receive fails loud**: `ClaimCheckNotFoundException` naming the
  reference, so the transport's normal failure semantics (nack -&gt; redelivery -&gt; DLQ) apply. There is
  no silent skip and no placeholder-processing.
- **Offload-then-send is not atomic.** The middleware puts to the store before calling `next()`. If the
  put fails, the middleware throws and the send never happens. If the put succeeds and the send then
  fails, the stored payload is orphaned until its TTL expires - documented honestly rather than
  pretending at two-phase commit.
- **At-rest posture defers to the store**: encryption is whatever the backing store defaults to (S3
  SSE-S3/KMS, Azure Storage SSE); access control is the bucket/container's IAM. This package does not
  build key management.

## Dependencies on other Benzene packages
- **Benzene.Abstractions** / **.Middleware** / **.Messages** - `IMiddleware`/pipeline builder,
  `IMessageHeadersGetter<TContext>`, `IMessageBodySetter<TContext>`, `ISerializer`.
- **Benzene.Clients** - `OutboundContext` (the outbound pipeline context this middleware runs on) and
  the default `JsonSerializer`.
- **Benzene.Core.Middleware** - the pipeline builder implementation extension methods bind to.

No cloud SDK dependency - by design (see `work/archive/claim-check-plan-2026-08.md` §2 for why the S3/Blob stores are
separate packages rather than folded in here).

## Conventions
- The claim check operates on the **serialized wire body** (the string a transport converter would
  send), never the typed message - that is what keeps it format-agnostic across `Benzene.Xml`,
  `Benzene.MessagePack`, and Avro; a store never needs to know what the payload "means".
- A custom `IClaimCheckStore.GetAsync` MUST throw `ClaimCheckStoreMismatchException` for a reference
  outside its own configuration (wrong scheme, or for a cloud store, a foreign bucket/container/
  prefix) rather than return `null` or attempt the fetch - this is a security boundary, not a
  not-found case.
- `IMessageBodySetter<TContext>` is resolved with `TryGetService`, not `GetService`: most transport
  pipelines never receive a claim-checked message, so requiring every pipeline to register a setter
  up front (even one that will never be used) would be an unnecessary hard dependency. The absence
  only surfaces - loudly, naming the context type - at the moment a message actually needs hydrating.

## Tests
- `test/Benzene.Core.Test/ClaimCheck/ClaimCheckOffloadMiddlewareTest.cs` - under/over threshold,
  `AlwaysOffload`, store failure propagation (and that `next()` never runs), custom serializer, header
  name override.
- `test/Benzene.Core.Test/ClaimCheck/ClaimCheckHydrateMiddlewareTest.cs` - no-header passthrough,
  header hydration, missing blob, no setter registered.
- `test/Benzene.Core.Test/ClaimCheck/InMemoryClaimCheckStoreTest.cs` - put/get round-trip, TTL expiry,
  foreign-scheme mismatch, independent keys, expired-entry eviction on `GetAsync`, `PutAsync`'s sweep
  (including entries never read back) and its once-per-`SweepInterval` gating.
- `test/Benzene.Core.Test/ClaimCheck/ClaimCheckRoundTripTest.cs` - offload through a real
  `MiddlewarePipelineBuilder<OutboundContext>`, hydrate through a real pipeline against a fake
  transport context, both wired via DI with `InMemoryClaimCheckStore` - the stored body a real
  `IClaimCheckStore.PutAsync` produced is exactly what the hydrated context ends up with.
