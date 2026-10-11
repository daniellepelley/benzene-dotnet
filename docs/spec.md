# Spec

The reserved [`benzene:spec`](https://github.com/daniellepelley/Benzene/blob/main/docs/specification/wire-contracts.md)
topic lets a Benzene service serve its own contract document — and, as .NET extensions, OpenAPI and
AsyncAPI. Add it to a message pipeline with the `UseSpec` middleware extension (`Benzene.Schema.OpenApi`):

```csharp
app.UseBenzeneMessage(benzeneMessageApp => benzeneMessageApp
    .UseSpec()
    .UseMessageHandlers());
```

Over HTTP the default route is `GET /benzene/spec` (`UseBenzeneCloudService` maps it; otherwise map it
yourself), with `type`/`format` as query parameters.

## Making a Spec Request

```json
{
  "topic": "benzene:spec",
  "body": "{\"type\":\"asyncapi\",\"format\":\"json\"}"
}
```

| Field | Options |
| ----- | ------- |
| `type`  | `benzene` (the contract document, the default), `openapi`, `asyncapi` |
| `format` | `json` (the default), `yaml` |

An absent body, an empty body, `null` and `{}` all ask for `benzene`/`json`. Values are compared
case-insensitively (`YAML` is `yaml`). A `type` or `format` the service does not support is answered
`not-implemented` — never with a different document — so a tool can tell "not offered" from "here it is".
`SpecBuilder.IsSupported(request)` answers the same question in code.

## The contract document

The `benzene` format is the language-neutral Contract Document
([contract-document.md](https://github.com/daniellepelley/Benzene/blob/main/docs/specification/contract-document.md)),
derived from the service's registrations:

```json
{
  "schemaDialect": "openapi-3.0",
  "service": "orders",
  "serviceVersion": "1.4.2",
  "versionScheme": "semver",
  "description": "Order intake",
  "messageEndpoint": "/benzene/invoke",
  "transports": ["http", "sqs"],
  "contractHash": "sha256:…",
  "registrationHash": "sha256:…",
  "requests": [
    { "topic": "order:create",
      "request": { "$ref": "#/components/schemas/CreateOrder" },
      "response": { "$ref": "#/components/schemas/OrderDto" } }
  ],
  "events": [
    { "topic": "order:placed", "message": { "$ref": "#/components/schemas/OrderPlaced" } }
  ],
  "sends": [
    { "topic": "payments:capture", "request": { "$ref": "#/components/schemas/CapturePayment" } }
  ],
  "components": { "schemas": { } }
}
```

- **Identity** comes from `IApplicationInfo` (`SetApplicationInfo(name, version, description[, versionScheme])`;
  `UseBenzeneCloudService` sets the name and `WithServiceVersion`'s version): `service`, `serviceVersion`,
  `versionScheme`, `description`.
- **`requests`** are the handlers. A handler that returns no payload (`IMessageHandler<TRequest>`, or a
  `Void` response) has no `response` at all; `{}` would mean "a payload of any shape".
- **`events`** are the topics this service publishes and *defines* - declared with
  `AddResponseEventDeclarations` (`Benzene.ResponseEvents`).
- **`sends`** are messages to topics *another party* defines - declared with a `MeshOutboundRegistry`
  through `AddSendDeclarations` (`Benzene.Mesh.Wire`) or the cloud service's `WithSends`.
  `Register<TRequest>` is one-way (no `response`); `Register<TRequest, TResponse>` expects a reply. A
  generated client's `Add{Service}ServiceClient()` declares the topics it calls as sends too; every
  registered declaration reaches the document, and a (topic, version) declared twice is listed once. A
  (topic, version) declared both as an event and as a send fails the build of the document.
- **Deployment-side fields** - `runtime` (`dotnet`), `placement` (`cloud`, and `region` only where
  known), `profile` (the Cloud Service Profile self-check) - come from a registered
  `ContractDocumentDeployment` (`SetContractDocumentDeployment(...)`; `UseBenzeneCloudService` registers
  one). `degraded` names a member the producer could not determine (`requests` when no handler registry
  is wired). None of them is in either hash.
- **The mesh register body is this document.** A meshed service registers exactly the document
  `benzene:spec` serves with its collector (`benzene:mesh:register`), and its heartbeats carry the
  document's `registrationHash` and the `serviceVersion` (mesh.md §2, §5). There is no separate mesh
  descriptor or `benzene:mesh` topic.
- **Reserved `benzene:` topics** (`benzene:spec`, `benzene:healthcheck`, …) are never listed.
- **`contractHash`** (requests + events: what a client depends on) and **`registrationHash`**
  (+ sends: what a mesh collector's graph depends on) are always present - see
  [Client SDKs](client-sdks.md#contract-hash) for how a generated client and `SchemaHealthCheck` use them.
- **`example`** appears only when an author supplied one; none is synthesized from the schema any
  more. The [Spec UI](spec-ui.md) and `benzene:test-payloads` (`ExamplePayloadBuilder`) still build
  ready-to-send payloads from the schemas.

### Schemas

Payload schemas are OpenAPI 3.0 Schema Objects derived from the CLR types by Swashbuckle, with the
dialect rules of contract-document.md §4.2: a property the serializer always writes is `required`
(Benzene's System.Text.Json serializer writes nulls, so every property is, unless it carries
`[JsonIgnore(Condition = WhenWritingNull)]`); a nullable reference to a named type is
`{"type":"object","nullable":true,"allOf":[{"$ref":…}]}`; records carry no `additionalProperties`;
enums keep their values; `format` (`int64`, `uuid`, …) is written where known.

### Component names

A named type's component name is its simple name (`Page<Order>` → `OrderPage`). Two different types
that would share a name fail derivation, naming both types, the topics that use them, and the fix -
declare a distinct name with `[SchemaName("…")]` (`Benzene.Abstractions.Schema`):

```csharp
[SchemaName("PaymentItem")]
public record Item(string PaymentId);
```

Derivation happens on the first `benzene:spec` request. To fail at start-up instead - before any
message is handled - opt in with `services.AddContractDocumentStartUpCheck()`; `benzene-descriptor
--emit spec` fails the build the same way.

`UseBenzeneCloudService` turns the start-up check **on by default**. Deriving every schema costs a
cold start, so a short-lived host such as an AWS Lambda function can opt out - and rely on the
build-time check instead:

```csharp
app.UseBenzeneCloudService("orders", cloud => cloud
    .WithoutContractDocumentStartUpCheck());
```

## Message endpoint advertisement

When the service exposes a BenzeneMessage-over-HTTP endpoint (`UseBenzeneMessage` — see
[Payload Testing](payload-testing.md)), the `benzene` format advertises it as an optional
top-level `messageEndpoint` field:

```json
{ "schemaDialect": "openapi-3.0", "service": "orders", "messageEndpoint": "/benzene/invoke", "requests": [ ], "…": "…" }
```

Consumers feature-detect send capability on this field — no field means the service accepts
messages only through its normal transports.

## Transport advertisement

The `benzene` format also advertises every transport the service is wired to receive messages
over as an optional top-level `transports` field — sourced from every registered
`Benzene.Abstractions.MessageHandlers.Info.ITransportInfo` at spec-build time (e.g. `"sqs"`,
`"kafka"`, `"http"`), written only when at least one is registered:

```json
{ "schemaDialect": "openapi-3.0", "service": "orders", "transports": ["http", "sqs"], "requests": [ ], "…": "…" }
```

This is document-level, not per-topic: any wired non-HTTP transport can reach any registered
topic (Benzene's topic routing has no per-topic transport filtering), so a per-topic list would
just repeat this same array on every request/event. HTTP is the one exception — a topic's actual
HTTP reachability is still its own per-topic `httpMappings`, which requires an explicit
`[HttpEndpoint]` attribute per handler and is unaffected by this field. Both [Spec UI](spec-ui.md)
and [Mesh UI](mesh-ui.md) render this as a chip row.

