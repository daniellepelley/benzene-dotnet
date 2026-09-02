# Round 18 review — Validation, Serialization, Observability, gRPC

Scope: `Benzene.Abstractions.Validation`, `Benzene.FluentValidation`, `Benzene.DataAnnotations`,
`Benzene.JsonSchema`, `Benzene.Avro`, `Benzene.MessagePack`, `Benzene.NewtonsoftJson`, `Benzene.Xml`,
`Benzene.OpenTelemetry`, `Benzene.Grpc`, `Benzene.Grpc.AspNet`, `Benzene.Grpc.Client`,
`Benzene.Grpc.TestHelpers`, `Benzene.Grpc.Versioning`. Reviewed against `daniellepelley/benzene-dotnet`
`main` at `7f642b2`.

Read-only review — no source files were modified, no dotnet SDK is available in this environment (per
task constraints), so nothing below was compiled or executed. Every finding was traced by hand through
the real call chain (not just the local file), and each carries a concrete regression-test description
for a future round with CI access to add as a permanent test.

`work/outstanding-bugs.md` and both round-17 docs
(`review-round17-validation-serialization-2026-08.md`, `review-round17-grpc-healthchecks-2026-08.md`)
were read in full first. Round 17's two Avro findings (#1 missing `Schema.Type.Map` arm, #2
`NonNullBranch`'s 2-branch-only assumption) and both gRPC/health findings (#1 mid-stream exception
bypassing classification, #2 `BenzeneHealthCheckBridge` ignoring `IsNonCritical`) were **re-derived from
current source, not trusted from the commit message** — see "Re-verification of round 17" below. All
four are confirmed correctly fixed, with one important caveat: the mid-stream-exception fix's shape
turns out to have a sibling gap in the two *non*-streaming RPC shapes, which is this round's headline
finding.

## Headline finding

### `GrpcMethodHandler.HandleAsync`/`ClientStreamingAsync` — a response-conversion failure after a
successful pipeline run leaves a **misleading `benzene-status: ok` trailer** and an unclassified
`StatusCode.Unknown`, the same defect class round 17's #280 fixed for the *other* two RPC shapes

**Severity: high.** Round 17 (finding #1 in `review-round17-grpc-healthchecks-2026-08.md`) found and
this repo already fixed (`#280`, confirmed below) that a server-streaming/duplex handler's mid-drain
exception used to bypass Benzene's error classification and leave a stale `benzene-status: ok` trailer,
because the trailer was written *before* the stream was actually drained. The fix moved that write to
*after* `GrpcStreamAdapter.WriteAll` finishes, and wrapped the drain in a try/catch that classifies the
exception the same way a unary handler's exception is classified.

That fix is correct as far as it goes — but it only touches the **streaming** call sites
(`WriteStreamAsync`, `src/Benzene.Grpc/GrpcMethodHandler.cs:172-194`). The **unary** and
**client-streaming** shapes have an exactly analogous "write the response after the pipeline succeeds"
step that the fix never touched, and it has the identical hazard:

```csharp
// src/Benzene.Grpc/GrpcMethodHandler.cs:30-45 (HandleAsync, unary — ClientStreamingAsync at 62-78 is byte-identical in shape)
public async Task<TResponse> HandleAsync<TRequest, TResponse>(TRequest request, ServerCallContext context)
{
    var grpcContext = new GrpcContext<TRequest, TResponse>(_grpcMethodDefinition.Topic, context, request);
    using var resolver = _serviceResolverFactory.CreateScope();

    await RunPipelineAsync(grpcContext, context, resolver);   // <- writes "benzene-status: ok" trailer HERE (see below)

    if (grpcContext.Response is TResponse typed)
    {
        return typed;
    }

    return resolver.GetService<IGrpcMessageAdapter>().ConvertResponse<TResponse>(grpcContext.ResponsePayload);
    // ^ can throw AFTER the trailer above was already staged, and this call is NOT inside any try/catch
}
```

`RunPipelineAsync` (`GrpcMethodHandler.cs:114-157`) is called with `deferSuccessTrailer` defaulted to
`false` for both non-streaming shapes, so on a successful pipeline result it writes the trailer
unconditionally, immediately, before returning:

```csharp
// GrpcMethodHandler.cs:140-142
if (!(deferSuccessTrailer && statusCode == StatusCode.OK))
{
    grpcContext.ResponseTrailers.Add("benzene-status", status ?? "Unknown");
}
```

`grpcContext.ResponseTrailers` is `CallContext.ResponseTrailers` directly (`GrpcContext.cs:36`) — a
`Metadata` collection grpc-dotnet doesn't transmit until the call actually ends, whether that's a normal
return *or* an unhandled exception. Round 17's own live-server proof for the streaming case already
established empirically that grpc-dotnet **does** flush whatever is staged in that collection even when
the call ends via an uncaught exception (`"DIAG StatusCode=Unknown Detail='Exception was thrown by
handler.' trailers=[benzene-status=ok]"` — see that doc's finding #1). The mechanism is identical here:
once `RunPipelineAsync` returns having written the `ok` trailer, `HandleAsync` calls
`IGrpcMessageAdapter.ConvertResponse<TResponse>(...)` completely unguarded — no try/catch anywhere
between here and grpc-dotnet's own generic exception handler in `BenzeneInterceptor.UnaryServerHandler`
(which also has no try/catch, `src/Benzene.Grpc/BenzeneInterceptor.cs:27-37`).

`ConvertResponse` is not a theoretical throw site — it is the documented, first-class path for *any*
handler that declares a POCO response instead of the protobuf type directly (`GrpcMessageHandlerResultSetter.SetResultAsync`
routes a non-`TResponse`-typed payload to `ResponsePayload`, not `Response` — `src/Benzene.Grpc/GrpcMessageHandlerResultSetter.cs:13`
via `GrpcContext<TRequest,TResponse>.ResponseAsObject`'s setter, `GrpcContext.cs:74-83`), which this
package's own `CLAUDE.md` calls out as one of the two supported, equally-first-class handler shapes
("A handler may declare the protobuf type directly (zero-copy) or a POCO (JSON-bridged) for either
side"). Two concrete, independently reachable ways `ConvertResponse` throws mid-conversion, **after**
the ok trailer is already staged:

1. **A genuine POCO/protobuf schema mismatch.** `ProtobufJsonGrpcMessageAdapter.ConvertResponse`
   (`src/Benzene.Grpc/Serialization/ProtobufJsonGrpcMessageAdapter.cs:63-90`) serializes the payload with
   plain `System.Text.Json`, then parses that JSON against the target protobuf `MessageDescriptor` via
   `JsonParser.Default.Parse(json, descriptor)`. This already has a dedicated, isolated unit test proving
   it throws (`ConvertResponse_WhenTargetIsNotAProtobufMessage_ThrowsBenzeneException`,
   `test/Benzene.Grpc.Test/Serialization/ProtobufJsonGrpcMessageAdapterTest.cs:145-150`) — what's never
   been exercised is what happens when this throw occurs **through the real `GrpcMethodHandler.HandleAsync`
   after a successful pipeline run**, which is exactly this gap. A real-world trigger: a service upgrades
   its response POCO's shape (adds/renames a field) without updating the paired `.proto`/generated type in
   lockstep — an easy mistake precisely because the JSON bridge means there's no compile-time check tying
   the two together.
2. **`double`/`float` `NaN`/`Infinity` payload values.** `System.Text.Json.JsonSerializer.Serialize` cannot
   serialize `double.NaN`/`PositiveInfinity`/`NegativeInfinity` unless `JsonNumberHandling.AllowNamedFloatingPointLiterals`
   is set on the options — and `ProtobufJsonGrpcMessageAdapter.SerializeOptions`
   (`ProtobufJsonGrpcMessageAdapter.cs:21-25`) sets no such flag, so `JsonSerializer.Serialize(payload,
   SerializeOptions)` throws for any POCO response carrying such a value. Protobuf's own `double`/`float`
   wire types explicitly support `NaN`/`Infinity` (the canonical proto3 JSON mapping spells them as the
   string literals `"NaN"`/`"Infinity"`), so this is a real round-trippable value the adapter simply can't
   carry through its own JSON intermediate — an easy hit for any analytics/ratio-shaped field (e.g. a
   `0.0/0.0` "conversion rate" with no denominator yet).

Either throw propagates through `HandleAsync`/`ClientStreamingAsync` unclassified — no
`ArgumentException`→`ValidationError`/`TimeoutException`→`Timeout` translation (the
`MessageHandler<TRequest,TResponse>` classification already ran and succeeded earlier, before
`ConvertResponse` is even reached), no `IGrpcStatusCodeMapper` mapping, no `AddRichErrorDetails`
`grpc-status-details-bin` trailer — straight to grpc-dotnet's generic fallback,
`StatusCode.Unknown`/`"Exception was thrown by handler."`, with the stale `benzene-status: ok` trailer
already attached from the successful pipeline run moments earlier.

**Compounding wrinkle on the Benzene-to-Benzene client side.** `DefaultGrpcStatusReverseMapper.Map`
(`src/Benzene.Grpc.Client/DefaultGrpcStatusReverseMapper.cs:42-51`) deliberately *prefers* the
`benzene-status` trailer verbatim over the actual `StatusCode` when present — the documented, correct
behaviour for the normal case where several Benzene statuses legitimately collapse to `StatusCode.OK` on
the wire. Here it means a `GrpcBenzeneMessageClient` caller reading this failed call gets back
`BenzeneResult.Set<TResponse>("ok", errors)` (`GrpcBenzeneMessageClient.cs:94`) — a result whose
`.Status` string literally reads `"ok"` while `.IsSuccessful` correctly stays `false` (the
`IReadOnlyList<BenzeneError>`-taking `ServiceBenzeneResultInternal` constructor hardcodes
`isSuccessful: false` regardless of the status string passed in, `src/Benzene.Results/BenzeneResult.cs:429-433`
— so this does **not** silently flip the call to "successful", but it does hand back an internally
inconsistent result: `Status == BenzeneResultStatus.Ok` on a result that is, and correctly reports itself
as, a failure). Any caller pattern-matching on `.Status` string rather than `.IsSuccessful` (a plausible
thing to do, since `Status` is the framework's own richer classification for everything *else*) would
misread this as success.

**Why this is the same class of bug round 17 already ruled a bug, not a new judgement call:** #280's own
fix and its doc comment (`GrpcMethodHandler.cs:159-171`) explicitly reasons about exactly this hazard —
"lets a mid-stream handler exception still land a truthful trailer instead of the success one
`RunPipelineAsync` would otherwise have written before the handler's iterator ever ran" — for the
streaming shapes. The identical reasoning applies verbatim to the unary/client-streaming shapes' own
"the trailer is written before the thing that can still fail" step; it just wasn't in scope for that fix.
This is also precisely the "four RPC shapes... handling errors differently... not documented as
intentional" angle this round's assignment called out: streaming and non-streaming calls now visibly
diverge on exactly this failure mode, and nothing documents that split as deliberate.

**Regression test to add** (mirroring `GrpcNullResponseHostingTest.cs`'s existing pattern — a real
`TestServer` + generated `GrpcChannel` client, not a unit-level `ConvertResponse` call, so the whole
interceptor/pipeline/wire path is exercised):
```csharp
// A handler that returns a POCO the target protobuf response type can't structurally accept
// (or one whose double field is NaN), driven through a real host:
using var call = client.EchoAsync(new EchoRequest { Name = "x" });
var ex = await Assert.ThrowsAsync<RpcException>(() => call.ResponseAsync);
Assert.Equal(StatusCode.Unknown, ex.StatusCode);           // no classification reached — the bug
// and, read from the raw trailers the server actually sent:
Assert.Equal("ok", call.GetTrailers().GetValue("benzene-status")); // the misleading part
```

**Suggested fix shape (not implemented — read-only review):** the same shape #280 already used for
streaming — defer the trailer write past the point where the response can still fail to materialize.
Concretely, move the `ConvertResponse` call (and the `grpcContext.Response is TResponse typed` check)
*inside* `RunPipelineAsync`, before the trailer is written, so a conversion failure there is classified
through the same `ClassifyStreamException`-style path (or reuses it directly) and the trailer reflects
the call's *actual* outcome. `ClientStreamingAsync` needs the identical treatment since it shares the
exact same post-pipeline conversion shape.

## Re-verification of round 17's fixes (re-derived from source, not trusted from commit messages)

### `Benzene.Avro` — both fixes hold up

- **`Schema.Type.Map` (round 17 finding #1):** `AvroDatumConverter.ToDatum`/`FromDatum` both now have an
  explicit `Schema.Type.Map` arm (`ToMap`/`FromMap`, `AvroDatumConverter.cs:50-51`/`119-161`/`197-198`/`264-315`).
  Traced both directions by hand: `ToMap` rejects a non-string-keyed CLR dictionary target up front (not
  just on first offending entry) via reflection over the concrete value's declared `IDictionary<,>`
  interface, and separately guards each entry's actual key at iteration time — correct, since a
  `Dictionary<object,object>` populated only with string keys at runtime would otherwise slip past a
  static-type-only check. `FromMap` resolves the CLR target's value type through a shared helper that
  handles `Dictionary<string,V>`, `IDictionary<string,V>`, and `IReadOnlyDictionary<string,V>`, correctly
  rejecting non-string-keyed targets by construction rather than by null propagation. Values recurse
  through `ToDatum`/`FromDatum` in both directions, so the round-17 "record-within-array-within-map"
  failure case is now structurally handled the same way `ToRecord`/`ToArray` handle their own nesting.
- **`NonNullBranch`'s 2-branch-only assumption (round 17 finding #2):** replaced by
  `ResolveWriteBranch`/`ResolveReadBranch` (`AvroDatumConverter.cs:360-465`). Write-side resolution tries
  an exact-shape match first (`IsNaturalMatch`), then a lossless-widening match (`IsWideningMatch`, e.g. an
  `int` value against a union offering only `"long"`), then falls back to the first declared branch only
  for a genuinely ambiguous case (two branches sharing the same CLR shape, e.g. two record branches for
  different POCOs with identical properties) — documented as a known, narrow approximation rather than
  silently mis-picking a scalar/collection branch the old code would have. Read-side resolution
  (`ResolveReadBranch`/`MatchesDatum`) matches by the *datum's* actual runtime type as already produced by
  `GenericDatumReader` (boxed `int`/`long`/`float`/`double`/`bool`, `byte[]`, `string`,
  `IDictionary`-for-map, `IEnumerable`-not-`IDictionary`-for-array, `GenericRecord` with a
  `Fullname` check for record), which is unambiguous by construction since the reader already resolved
  the wire's actual branch. Traced the boolean/long round-trip case from round 17's own repro by hand
  against the new code: `true` now naturally-matches the `Boolean` branch on write and `datum is bool` on
  read — the value and its CLR type both survive intact, unlike the old always-first-branch behaviour.
  One residual, explicitly-documented gap: a union branch of native Avro `enum` (not a CLR-enum-mapped
  `"string"` branch — a hand-authored `{"type":"enum",...}` schema branch) has no `MatchesDatum`/
  `IsNaturalMatch` arm and would fall back to the first-declared-branch behaviour for that one branch
  shape; this isn't listed anywhere in `Benzene.Avro/CLAUDE.md`'s supported-shapes section (which only
  documents primitives/records/arrays/maps for the multi-branch case), so it reads as a narrow,
  undocumented scope edge rather than a fresh, actionable bug — flagging here for visibility rather than
  as a new finding.
- **Recursion depth on deserialize — checked as a fresh angle this round** (the assignment specifically
  asked to check every serializer here against the class of bug round 15 found in the versioning caster
  builder, `Benzene.Core.Versioning.CasterFuncBuilder`, #226): `AvroDatumConverter.FromDatum` itself has
  **no** depth parameter or check at all (contrast `ToDatum`, which threads `depth`/`maxDepth` through
  every recursive call and throws `AvroPayloadTooDeepException` — `AvroDatumConverter.cs:35-40`). This
  looked, on first read, like exactly the same "guarded on write, unguarded on read" shape round 15 found.
  It is not a bug: the guard already exists one layer down, in `BoundedBinaryDecoder`
  (`src/Benzene.Avro/BoundedBinaryDecoder.cs`), which counts every `ReadArrayStart`/`ReadMapStart`/
  `ReadUnionIndex` the underlying `GenericDatumReader` observes across the whole payload and throws
  `AvroPayloadTooDeepException` once `AvroOptions.MaxDepth` (default 500) is exceeded — **before**
  `GenericDatumReader` ever materializes a `GenericRecord`/array/map datum tree deep enough for
  `AvroDatumConverter.FromDatum`'s own (otherwise-unguarded) recursion to reach a dangerous depth. Traced
  why this is complete, not partial, coverage: the reflection schema generator always wraps a nested
  record field in a `["null", RecordSchema]` union (`AvroSchemaGenerator.cs:36-46`), so any
  reflection-schema self-reference recurses through `ReadUnionIndex` (guarded) before it can recurse
  through a record; a bare, non-nullable, directly-self-referential *explicit*-schema record field (no
  union/array/map wrapper) is not guarded by this mechanism, but is also not constructible as valid wire
  data in the first place — Avro's binary encoding has no per-record length prefix or terminator, so a
  record field typed as itself with no escape hatch has no way to encode a finite instance at all,
  independent of Benzene. No bug found; the design note in `AvroOptions.MaxDepth`'s doc comment
  (`AvroOptions.cs:40-54`) already describes this mechanism accurately.

### `Benzene.Grpc`/`Benzene.Grpc.AspNet` — both round-17 findings hold up

- **Finding #1 (mid-stream exception, streaming shapes) — fixed as `#280`.** Verified the fix shape
  directly: `GrpcMethodHandler.ServerStreamingAsync`/`DuplexStreamingAsync` now call `RunPipelineAsync`
  with `deferSuccessTrailer: true`, and `WriteStreamAsync` (`GrpcMethodHandler.cs:172-194`) wraps the
  drain in a try/catch that writes the trailer only *after* `GrpcStreamAdapter.WriteAll` completes (success
  path) or classifies the exception via `ClassifyStreamException` and writes the correct trailer/rich error
  details before throwing (failure path) — this is the correct fix shape, and it's what motivated this
  round's headline finding once the same "trailer-before-thing-that-can-fail" pattern was checked against
  the two shapes the fix didn't touch.
- **Finding #2 (`BenzeneHealthCheckBridge` ignoring `IsNonCritical`) — fixed as `#281`.**
  `BenzeneHealthCheckBridge.CheckHealthAsync` now calls `ApplyNonCriticalDowngrade`
  (`src/Benzene.Grpc.AspNet/BenzeneHealthCheckBridge.cs:118-123`) per result before deciding the
  aggregate, downgrading a `Failed`+`IsNonCritical`+`!IsPersistent` result to `Warning` — matching
  `HealthCheckProcessor.RunTimedAsync`'s rule exactly (duplicated, not shared, per that package's
  deliberate non-dependency on the full `Benzene.HealthChecks` pipeline package, as documented). Traced
  the aggregate decision (`CheckHealthAsync:92-102`) against the downgraded `effectiveStatuses`, not the
  raw per-check results — correct.

### `Benzene.Grpc.Versioning` — inherits round 15's `CasterFuncBuilder` fix, no independent risk

The assignment's recursion-guard prompt specifically named "the versioning caster builder" (round 15,
`#226`, `Benzene.Core.Versioning.CasterFuncBuilder` — an uncatchable `StackOverflowException` on a
self-referential/mutually-recursive versioned DTO, fixed via a two-phase build-then-memoize with a
forwarder delegate for the in-flight cell). `Benzene.Grpc.Versioning`'s only file
(`GrpcPayloadVersioningExtensions.cs`) does not reimplement any casting logic — it calls
`AddPayloadVersioning`/`UsePayloadVersionRequestCasting<GrpcContext, GrpcRequestMapper>()`, both from
`Benzene.Core.Versioning`, and only re-points the request-side decorator at `GrpcRequestMapper` instead
of the default serializer mapper. There is no separate caster-building code path here to carry the same
bug independently — it inherits `#226`'s fix directly. No bug found.

## Areas investigated with no bug found

### `Benzene.OpenTelemetry` — too thin to own the round-15 flush bug

The assignment asked to verify round 15's trace-exporter flush fix (a steady low-traffic trickle never
time-flushing) holds. That fix lives in `HttpMeshTraceExporter`
(`src/Benzene.Mesh.Wire/IMeshTraceExporter.cs`), a different package entirely, out of this round's
territory (mesh is other reviewers' remit). `Benzene.OpenTelemetry` itself
(`src/Benzene.OpenTelemetry/DependencyInjectionExtensions.cs`) is two `AddSource`/`AddMeter` extension
methods, nothing else — it registers Benzene's `ActivitySource`/`Meter` as sources for a caller-supplied
`TracerProviderBuilder`/`MeterProviderBuilder` and owns no batching/flush/export logic of its own; all of
that is the OpenTelemetry SDK's own `BatchActivityExportProcessor`/exporter machinery, entirely outside
this package. There is nothing here that could reproduce or regress the round-15 defect class. No bug
found; confirmed this package is not the site the assignment's flush-behaviour prompt was actually
pointing at.

### `Benzene.MessagePack` — clean

`MessagePackSerializer`'s default constructor applies `MessagePackSecurity.UntrustedData` explicitly
(depth cap + collision-resistant hashing), with the custom-options constructor's doc comment calling out
in detail that `MessagePackSecurity.TrustedData` is MessagePack-CSharp's own default and would silently
reintroduce the DoS exposure if a caller builds options the "obvious" way
(`MessagePackSerializerOptions.Standard.WithResolver(...)`) instead of starting from the parameterless
constructor. This is exactly the depth-guard/hash-collision hardening this round's recursion angle was
checking for, already present and already the *documented* right way to avoid the footgun. No further
issue found in the (thin) wrapper code itself — it delegates entirely to MessagePack-CSharp's own,
well-audited binary codec.

### `Benzene.Xml` — clean, and the round-15-class recursion guard is present and correctly scoped

`DepthGuardedXmlReader` (`src/Benzene.Xml/DepthGuardedXmlReader.cs`) guards exactly the recursion vector
`System.Xml.Serialization.XmlSerializer`'s generated deserializer actually uses (one CLR stack frame per
nested element), checked against the wrapped `XmlReader`'s own BCL-correct `Depth` on every element-start
`Read()`. Serialization is correctly left unguarded (not attacker-controlled, per the package's own
documented rationale) — verified this by construction (a service authors and controls its own response
DTOs; a request DTO is what a caller controls). DTD prohibition (`DtdProcessing.Prohibit` +
`XmlResolver = null`) and the BOM-stripping fix are both present and correctly ordered (BOM stripped
before the depth-guarded reader is even constructed, so it doesn't count as spurious nesting). No bug
found.

### `Benzene.NewtonsoftJson` — not re-litigated beyond a spot-check

Round 17 already pushed hard here (the `JsonConvert.DefaultSettings`-merge question, `TypeNameHandling`)
and came back clean with an empirical repro rather than a documentation read. Spot-checked
`JsonSerializer.cs` again for the numeric-precision/overflow angle this round's assignment specifically
calls out: all four members delegate straight to `Newtonsoft.Json.JsonConvert`/`JsonSerializer`, which
handles `long`/`ulong`/`decimal` precision and overflow the same way the wider .NET ecosystem already
relies on Json.NET to — nothing Benzene-specific layered on top that could introduce a fresh precision
bug. No new finding.

### `Benzene.FluentValidation`/`Benzene.DataAnnotations`/`Benzene.JsonSchema`/`Benzene.Abstractions.Validation`

Round 17 covered the inbound `ValidationMiddleware` cascade/rule-set/throw-vs-fail angles for both
FluentValidation and DataAnnotations in depth and found nothing; this round additionally read
`ValidationClientMiddleware`/`ValidationClientMiddlewareBuilder` (the **outbound** FluentValidation
counterpart, not explicitly covered by round 17's writeup) and `FluentValidationSchemaBuilder`
(round-trips FluentValidation rules to `IValidationSchema`s for OpenAPI generation) — both are
straightforward, symmetrical with their already-reviewed inbound siblings (same `Field`/`Code`
population rule, same `IValidationStatusMapper`-absent default), and the schema builder's rule-name
switch (`FluentValidationSchemaBuilder.GetRule`) degrades unknown validator names to `null`/dropped
rather than throwing, which is the documented-safe behaviour. `Benzene.Abstractions.Validation` is
interfaces/constants only, nothing to review. `Benzene.JsonSchema` was re-read but not re-tested beyond
round 17's own empirical `$ref`-cycle/version-catalogue work; nothing new found on this pass.

## Bottom line

One new, concrete, high-severity finding: `GrpcMethodHandler.HandleAsync`/`ClientStreamingAsync` share
the exact defect class round 17's own `#280` fix targeted (a response-materialization failure after a
successful pipeline run leaves a stale, misleading `benzene-status: ok` trailer and an unclassified
`StatusCode.Unknown`), just in the two RPC shapes that fix didn't touch — both independently reachable
via the package's own documented POCO-response bridging feature, not exotic misuse. Round 17's four
prior findings (both Avro, both gRPC/health) were re-derived from current source rather than trusted, and
all four fixes are correct and complete, including a deliberately-adversarial check of Avro's
deserialize-side recursion guard against the round-15 caster-builder bug class, which held up.
`Benzene.Grpc.Versioning` was confirmed to carry no independent risk of the round-15 caster bug (it
inherits the fix). `Benzene.OpenTelemetry` was confirmed to be too thin (two extension methods, no
batching/flush logic of its own) to be the site of the round-15 flush bug the assignment referenced — that
lives in a different package outside this round's territory. `Benzene.MessagePack`, `Benzene.Xml`,
`Benzene.NewtonsoftJson`, and the validation packages were all pushed on the specific angles assigned
(numeric precision/overflow, null-vs-default-vs-absent, recursion, outbound validation) and came back
clean.
