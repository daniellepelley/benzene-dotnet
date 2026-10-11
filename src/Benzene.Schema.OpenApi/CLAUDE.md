# Benzene.Schema.OpenApi

## What this package does
Generates a machine-readable spec of a Benzene service's message contract from its registered
handlers, HTTP endpoints, and broadcast/sender message definitions, and serves it from a `spec`
topic. Despite the package name it produces **three** spec formats, selected per request:
- `benzene` - the language-neutral **Contract Document** (contract-document.md in the Benzene repo;
  the `EventServiceDocument` type): `schemaDialect`, the service's identity, `requests`, `events`,
  `sends`, `components`, both hashes. This is the **default** and the format `Benzene.Spec.Ui` renders.
- `openapi` - OpenAPI 3.0 (via `Microsoft.OpenApi`), for HTTP endpoints.
- `asyncapi` - AsyncAPI 3.0 (via `ByteBard.AsyncAPI.NET`), for topic/event channels.

Each format can be emitted as JSON or YAML. The package also includes a backward-compatibility gate
for the `benzene` contract.

## Key types/interfaces

### Serving the spec
- `Extensions.UseSpec<TContext>()` / `UseSpec<TContext>(string topic)` - registers `SpecMessageHandler`
  on the `spec` topic (`Constants.DefaultSpecTopic`).
- `SpecMessageHandler : IMessageHandler<SpecRequest, RawStringMessage>` - builds the spec and returns
  it as a raw string.
- `SpecRequest { string Type; string Format }` - `Type` selects `benzene`/`openapi`/`asyncapi`;
  `Format` selects `json`/`yaml`. Per wire-contracts §7 (spec-0.2.0): absent/empty body, `null` and `{}`
  mean `benzene`/`json`; both values compare case-insensitively; an **unsupported** type or format is
  answered `not-implemented` by `SpecMessageHandler`, never with a different document (it used to fall
  back to `benzene`/JSON).
- `SpecBuilder` - `Normalize(request)` (defaults + lower-casing), `IsSupported(request)` (the type is
  known and its builder implements `IProducesJson`/`IProducesYaml` for the format), `TryCreateSpec`, and
  `CreateSpec`/`CreateBuilder`, which now throw `NotSupportedException` for an unsupported type/format
  rather than silently building the contract document.
- `SpecCache` - process-lifetime memoization of the generated document, keyed by (type, format), so
  a spec request re-runs the full build (Swashbuckle schema generation over every request/response
  CLR type + example-payload generation + serialization) only on the first request for each
  combination. Registered as a singleton by `UseSpec`; `SpecMessageHandler` serves from it when
  present and falls back to a direct build otherwise. The spec is derived only from startup
  registrations (handlers/endpoints/transports/validation), so it's stable for the process lifetime -
  the same assumption the handler/endpoint finder caches already make. This is the fix for repeated
  polling of the `spec` endpoint (the mesh aggregator) rebuilding the whole document each time.
  Keyed on `SpecBuilder.Normalize(request)`, so every spelling of one document (`{}`, `benzene`/`json`,
  `YAML`/`yaml`) shares an entry. The finders (`IMessageHandlersFinder`/`IHttpEndpointFinder`) were already cached;
  this closes the remaining per-request rebuild one level up at the finished document.

### Test payloads (runtime, opt-in)
- `Extensions.UseTestPayloads<TContext>()` / `UseTestPayloads<TContext>(string topic)` - registers
  `TestPayloadsMessageHandler` on the `test-payloads` topic (`Constants.DefaultTestPayloadsTopic`).
  Opt-in like `UseSpec` (nothing exposed unless called); reveals no more than the `spec` topic
  already does.
- `TestPayloadsMessageHandler : IMessageHandler<TestPayloadsRequest, RawStringMessage>` - builds the
  service's `EventServiceDocument` (reusing `SpecBuilder.CreateBuilder(resolver, "benzene")`) and
  returns a `TestPayloadsBuilder` manifest. `TestPayloadsRequest.Topic` optionally filters to one topic.
- `TestPayloadsBuilder` - pure `EventServiceDocument → TestPayloadsManifest`/JSON: for each **domain**
  topic (reserved utility topics skipped) it generates the deterministic, validation-aware example
  body via the runtime-safe `Examples.ExamplePayloadBuilder` and wraps it in the portable
  BenzeneMessage envelope (`{ topic, headers, body }`) - the exact shape a caller POSTs to
  `/benzene/invoke` - plus the transports each topic is reachable on (`EventServiceDocument.Transports`
  + per-topic HTTP mappings). Deliberately AWS-free.
- `ITestPayloadDresser` / `TestPayloadDressingContext` - the **per-transport dressing seam** (decision
  1(c) of `work/archive/runtime-test-payloads-plan-2026-08.md`). `TestPayloadsBuilder` resolves every registered
  `ITestPayloadDresser` (via `IServiceResolver.GetServices`) and folds each one's output into a topic's
  `Payloads[transport]` alongside the always-present `benzene-message` entry, reusing the same
  serialized body so all transports agree. A dresser returns `null` to skip (host not wired for the
  transport, or an HTTP dresser on a non-HTTP topic). The core carries **no** dresser implementations
  and stays AWS-free; the SNS/SQS/API-Gateway dressers live in the opt-in **`Benzene.Aws.Lambda.TestPayloads`**
  package (`UseAwsTestPayloads()`), which references the `Amazon.Lambda.*Events` contracts so this core
  never does. Tests: `test/Benzene.Core.Test/Autogen/Schema/OpenApi/TestPayloads/TestPayloadsBuilderTest.cs`.

### Document builders (one per format)
- `OpenApi/OpenApiDocumentBuilder` - OpenAPI 3.0 (`SerializeAsJson/Yaml(OpenApiSpecVersion.OpenApi3_0)`).
  Builds paths/operations from HTTP endpoint definitions, request/response schemas, and a fixed set of
  error responses (400/401/403/404/422/500/503), served as `application/problem+json`, whose
  bodies reference `ProblemDetails` (`Benzene.Results`).
- `AsyncApi/AsyncApiDocumentBuilder` - AsyncAPI **3.0** (`AsyncApiVersion.AsyncApi3_0`, via
  `ByteBard.AsyncAPI.NET`; serializes as `3.1.0`, the latest 3.x patch). Emits **channels** (each with
  an `address` = the topic and a `messages` map) and top-level **operations** that reference them.
  **Operation perspective:** 3.0 names direction explicitly with `action` from the *application's* view —
  a handler **`receive`s** its request and its reply is modelled with the native **`reply`** object
  (pointing at a reply channel whose address is `<topic>:response` by default — configurable via
  `AsyncApiSpecOptions.ResponseTopicSuffix` / `Extensions.SetAsyncApiResponseTopicSuffix(...)`, defaulting
  to `AsyncApiDocumentBuilder.DefaultResponseTopicSuffix`); broadcast events and egress message-senders are
  things the app **`send`s**. This replaces 2.x's notoriously back-to-front `publish`/`subscribe` (see
  `work/archive/asyncapi-alignment-2026-08.md` for why the old output was inverted). Channel/operation/message **map keys
  are sanitized** to `^[A-Za-z0-9.\-_]+$` (3.0 requires it; the raw topic, which can contain `:`, is kept
  in the channel's `address`). The document also carries `id` (`urn:benzene:service:<title>`) and
  `defaultContentType` (`application/json`). A builder test (`Operations_UseTheCorrectAsyncApiPerspective`)
  pins the action/reply shape. **Note on the ByteBard reader:** its `AsyncApiOperationRules` "messages MUST
  be a subset of the referenced channel's messages" validation is a false positive that also rejects the
  spec's own request/reply example — Benzene's output parses and resolves fully; that one validation rule
  is not a signal.
- `EventService/EventServiceDocumentBuilder` - derives the `benzene` `EventServiceDocument` (spec 3a):
  `schemaDialect: "openapi-3.0"`; `service`/`serviceVersion`/`versionScheme`/`description` from
  `IApplicationInfo` (the single source - `AddApplicationInfo`, or `AddService(...)`); `requests` from
  handlers (never a `benzene:` topic; a `Void` response is **absent** = no payload; a `Void` request is
  `{}`), `events` from event declarations (`IConsumesBroadcastEventsDefinitions`, fed from
  `IMessageDefinitionFinder<IMessageDefinition>` = `ResponseEventCatalog`), `sends` from send
  declarations (`IConsumesMessageSenderDefinitions`, fed from
  `IMessageDefinitionFinder<IMessageSenderDefinition>` = a `MeshOutboundRegistry` registered by
  `AddSendDeclarations`); a (topic, version) in both events and sends throws from `Build()`. No example
  is synthesized any more - only an author-supplied `Example` is written (`ExamplePayloadBuilder` still
  serves `benzene:test-payloads`). HTTP mappings attach to every version of a topic.
- `EventService/EventServiceDocument` - the model. Serialization always writes `contractHash` and
  `registrationHash` computed over the document as written (`ContractHash`), so a projection
  (`With(...)`) carries its own; `DeclaredContractHash`/`DeclaredRegistrationHash` keep what a reader
  saw. `Send` is the `sends[]` entry (`Response == null` = one-way).
- `EventService/ContractHash` + `JsonCanonicalizer` (namespace `Benzene.CodeGen.Core`, type-forwarded
  from that assembly where they used to live) - contract-document.md §6's two hashes over the
  allow-listed, dereferenced, annotation-stripped projection; RFC 8785 + SHA-256. `NormalizeSchema` is
  the §6.2 steps 3-4 comparison form. Proven by `conformance/contract-hash-cases.json`.
- `EventService/EventServiceDocumentDeserializer` - reads a document; rejects an unknown
  `schemaDialect` (`UnknownSchemaDialectException`), ignores `benzene:` entries, reads a legacy
  (spec-0.2.0, `openapi`/`info`) document with a warning (`Warnings`, `onWarning`, `IsLegacyShape`).
- Builders implement the "consumer" seam interfaces in `Abstractions/` -
  `IConsumesApplicationInfo`, `IConsumesMessageHandlerDefinitions`, `IConsumesHttpEndpointDefinitions`,
  `IConsumesBroadcastEventsDefinitions`, `IConsumesMessageSenderDefinitions`,
  `IConsumesMessageEndpoint`, `IConsumesTransportsInfo` - and `SpecBuilder` feeds each builder only
  the definitions it consumes, resolved from DI (`IApplicationInfo`, `IMessageHandlersFinder`,
  `IHttpEndpointFinder`, `IMessageDefinitionFinder`, `IBenzeneMessageHttpEndpointInfo`,
  `ITransportsInfo`). Output flows through `IProducesJson`/`IProducesYaml`.
- `EventServiceDocument.Transports` (`string[]`, written only when non-empty) - every transport
  the host is wired to receive messages over, sourced from `ITransportsInfo` (every registered
  `ITransportInfo.Name`, aggregated at spec-build time - see `Benzene.Core.MessageHandlers`'s
  "Transport Info" section). Document-level, not per-topic: Benzene's topic routing has no
  per-topic transport filtering, so any wired non-HTTP transport reaches any registered topic
  uniformly - a per-topic list would just repeat this array on every request/event. HTTP stays
  the one per-topic exception, already captured by each `RequestResponse.HttpMappings` (needs an
  explicit `[HttpEndpoint]` attribute per handler). See `docs/spec.md`'s "Transport advertisement"
  section and `work/service-mesh-roadmap-1.0.md` §10.16-§10.17 for the design/implementation log.

### Schema building
- `ISchemaBuilder` / `SchemaBuilder` - generates `OpenApiSchema`s from CLR types using Swashbuckle's
  `SchemaGenerator` + a `System.Text.Json` contract resolver, and collects them into a components
  catalogue. The derivation rule of contract-document.md §4.2 is applied by
  `SupportNonNullableReferenceTypes` + `UseAllOfToExtendReferenceSchemas` + `Derivation/DerivationSchemaFilter`
  (nullable named type → `{type, nullable: true, allOf: [$ref]}`; no `additionalProperties` on records;
  `required` = every always-emitted property; enums typed; `format` kept). Component names come from
  `Derivation/ComponentNames` (declared `[SchemaName]` from `Benzene.Abstractions.Schema`, else the
  simple name, generic arguments first, invalid characters dropped) through the `SchemaIdSelector`; two
  different types claiming one name throw `ComponentNameCollisionException` (the builder re-throws it
  naming the topics). `ContractDocumentStartUpCheck` (`AddContractDocumentStartUpCheck()`, opt-in) derives
  the document at start-up so this fails before any message. `NullableReference.Unwrap` lets consumers
  (examples, codegen, docs) see through the wrapper. **DI seam:** `SpecBuilder` try-resolves `ISchemaBuilder` from the container before
  falling back to `new SchemaBuilder(...)`, so a custom builder (e.g. `SuppliedSchemaBuilder`)
  can replace reflection generation in the live spec. Register custom builders transient/scoped -
  a builder accumulates one document's components, so a singleton would leak across spec builds.
- `SchemaGenerationOptions` (+ `Extensions.SetSchemaGenerationOptions`) - opt-in inheritance
  (`allOf` base `$ref`) and polymorphism (`oneOf` + `discriminator`) rendering for generated
  schemas; off by default (flattened output unchanged). Subtype/discriminator resolution defaults
  to the models' own STJ `[JsonDerivedType]`/`[JsonPolymorphic]` attributes (`JsonPolymorphism`
  helper), so the contract matches the default runtime serializer's behavior; resolver hooks exist
  for unannotated hierarchies. Tests: `SchemaBuilderPolymorphismTest`.
- `SuppliedSchemaCatalog` / `SuppliedSchemaBuilder` (+ `Extensions.AddSuppliedSchemas`) -
  bring-your-own schema documents: the catalog maps CLR types to hand-authored schemas (loaded
  programmatically, from per-schema JSON, or from a `components.schemas`-shaped JSON object);
  the builder serves them as `$ref`s (registering the whole catalog on first use so
  cross-`$ref`s resolve) and falls back to reflection for unmapped types. See
  `work/archive/complex-payloads-byo-schema-plan-2026-08.md` for the full design/phases. Tests:
  `SuppliedSchemaBuilderTest`, `SpecSuppliedSchemasTest` (end-to-end through `UseSpec`).
- `OpenApiValidationSchemaBuilder : ISchemaBuilder` - decorates generated schemas with validation
  constraints pulled from a registered `IValidationSchemaBuilder` (e.g. `Benzene.FluentValidation`'s):
  `minLength`/`maxLength`, `pattern`, `enum` (IsOneOf), `required` (NotEmpty/NotNull), and `uuid`/
  `email` formats. `SpecBuilder` uses this automatically when an `IValidationSchemaBuilder` is
  registered. Looks the catalogued schema up by the id the inner builder returned (not raw
  `type.Name`), so generic wrappers (`MessageWrapper<Foo>` → `FooMessageWrapper`) get decorated too.
- `AsyncApi/Mapper` - maps `OpenApiSchema` onto ByteBard's AsyncAPI schema model; carries
  `oneOf`/`allOf`/`anyOf` (recursively, refs preserved), `discriminator` (property name), and
  `additionalProperties` in addition to the scalar/object facets, so polymorphic contracts
  survive into the AsyncAPI document.
- `JsonOpenApiSchemaBuilder` - builds an `OpenApiSchema` set from a sample JSON document.
- `OpenApiSchemaComparer` - structural diff between two `OpenApiSchema`s (type/format/properties).

### Backward-compatibility gate (`Compatibility/`)
- `SchemaCompatibility` - `Compare(baseline, current)` and `EnsureBackwardCompatible(...)` (throws
  `SchemaCompatibilityException` on breaking changes; JSON-string overloads deserialize a committed
  baseline). `benzene:` entries are ignored; a legacy-shape baseline throws one
  `LegacyBaselineException` (re-baseline) instead of being diffed. Supporting types: `SchemaCompatibilityComparer`, `SchemaCompatibilityReport`,
  `SchemaCompatibilityRules`, `SchemaChange`/`SchemaChangeKind`, `ChangeCompatibility`,
  `SchemaDirection`. Drop `EnsureBackwardCompatible` into a test to fail CI when the `benzene`
  contract stops being backward compatible with a baseline `spec.json`.

### Example payload generation (`Examples/`)
- `IExamplePayloadBuilder` / `ExamplePayloadBuilder` — builds a deterministic example payload
  (camelCased property-name → value dictionary) from an `OpenApiSchema`. Honours, in precedence
  order: caller-supplied known values (keyed by property path `order.customer.email`, falling back
  to bare name), the schema's own `example` → `default` → first `enum` value, then a fixed value
  per type/format (`uuid`, `date-time`, `date`, `email`, `uri`), sized/clamped into
  `minLength`/`maxLength`/`minimum`/`maximum` so generated examples pass the validation rules the
  spec advertises. `pattern` is not reverse-generated. Reference cycles terminate as `{}`/`[]`
  (ancestry-tracked, max reference depth 8). Deterministic by design — no randomness — so spec
  output and golden-file tests stay stable.
- `ISchemaGetter` / `SchemaGetter` — resolves `$ref` schemas against a schema catalogue.
- `OpenApiAnyConverter` — converts between plain .NET values and `IOpenApiAny` trees (used to
  honour schema examples and to build `benzene:test-payloads`; spec documents no longer embed generated examples).
- These types moved here from `Benzene.CodeGen.Core` (which now consumes them) so examples can be
  generated at runtime during spec builds, not just at codegen time. Tests:
  `test/Benzene.Core.Test/Autogen/Schema/OpenApi/Examples/ExamplePayloadBuilderTest.cs`
  (golden files alongside).

## When to use this package
- To expose a live, always-accurate spec of a service's topics/endpoints from its own handler
  registrations (`UseSpec`), consumed by `Benzene.Spec.Ui`, Swagger UI (openapi), or AsyncAPI tooling.
- To gate CI on backward-compatibility of the message contract (`SchemaCompatibility`).

## Dependencies on other Benzene packages
- **Benzene.Abstractions.Pipelines** / **Benzene.Core.MessageHandlers** / **Benzene.Core.Messages** /
  **Benzene.Core** - message-handler definitions, finders, `RawStringMessage`, pipeline seams.
- **Benzene.Abstractions.Validation** - `IValidationSchemaBuilder`/`IValidationSchema`/
  `ValidationConstants`, consumed by `OpenApiValidationSchemaBuilder`.
- **Benzene.Http** - HTTP endpoint definitions/routing and the BenzeneMessage endpoint info.
- **Benzene.Results** - `BenzeneResult`, `ProblemDetails`.
- NuGet: **Microsoft.OpenApi**(+**.Readers**), **ByteBard.AsyncAPI.NET** (AsyncAPI 3.0 model+serializer;
  the maintained continuation of `LEGO.AsyncAPI.NET`, which was 2.0-only), **Swashbuckle.AspNetCore.SwaggerGen**
  (schema generation only - no Swagger UI is bundled), **Newtonsoft.Json**.
- Note: this package does **not** depend on `Benzene.JsonSchema`; schema generation is done via
  Swashbuckle's `SchemaGenerator`, not that package.

## Important conventions
- The `spec` topic is served like any other message handler; the default format is `benzene`, output
  is JSON unless `Format` is `yaml`.
- **Reserved utility topics** (`ReservedTopics`, i.e. `BenzeneTopic.IsReserved`): never listed in the
  `benzene` document (contract-document.md §1) - their shapes are wire-contracts §7's. The OpenAPI and
  AsyncAPI exports still include them.
- Schemas are generated from handler request/response types and keyed by type name; validation
  constraints are merged in only when an `IValidationSchemaBuilder` is registered.
- OpenAPI output is version 3.0; AsyncAPI output is 3.0.

## Tests
- Example payload generation: `test/Benzene.Core.Test/Autogen/Schema/OpenApi/Examples/ExamplePayloadBuilderTest.cs`
  (with golden files).
