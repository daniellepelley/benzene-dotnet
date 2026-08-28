# Benzene.Xml

## What this package does
Adds XML (`application/xml`) as a negotiable request/response media format for Benzene, backed by
the BCL `System.Xml.Serialization.XmlSerializer`. It plugs an `IMediaFormat<TContext>` into the
content-negotiation pipeline, so a request with `content-type: application/xml` is read as XML and a
response is written as XML when `application/xml` appears in the `accept` header - alongside JSON and
any other registered format, not replacing them.

## Key types
- `XmlSerializer : ISerializer` - wraps `System.Xml.Serialization.XmlSerializer`, caching one
  serializer instance per CLR type in a `ConcurrentDictionary` to avoid the per-call lookup/lock
  overhead. String-based `ISerializer` (produces/consumes XML text); it is **not** an
  `IPayloadSerializer` (no byte-oriented path - contrast `Benzene.Avro`/`Benzene.MessagePack`).
  Constructed either parameterless (default `XmlOptions`) or from an explicit `XmlOptions`.
- `XmlOptions` - configures the deserialize path. `MaxDepth` (default 32,
  `XmlOptions.DefaultMaxDepth`) bounds the XML element-nesting depth `Deserialize` will follow before
  throwing `Benzene.Core.Exceptions.BenzeneException` (#260) - mirrors `Benzene.Avro`'s
  `AvroOptions.MaxDepth`. Serialization is not guarded (not attacker-controlled; out of scope).
- `DepthGuardedXmlReader` (internal) - the `XmlReader` decorator that enforces `XmlOptions.MaxDepth`.
  A self-referencing/very-deeply-nested request DTO (a comment tree, category tree, org chart) would
  otherwise drive `System.Xml.Serialization.XmlSerializer`'s generated deserializer into unbounded CLR
  recursion and an uncatchable `StackOverflowException` - the same bug class Avro's #56
  `BoundedBinaryDecoder` closed. Every member forwards unchanged to the wrapped reader except `Read()`,
  which additionally checks the wrapped reader's own (BCL-correct) `Depth` against the configured
  maximum whenever the current node is an element start, throwing once exceeded - the exception
  unwinds the deserializer's own recursive `Read()` calls well before they could blow the stack.
  `Deserialize` wraps the raw `XmlReader.Create(...)` result in this before handing it to
  `System.Xml.Serialization.XmlSerializer.Deserialize(XmlReader)`.
- `XmlMediaFormat<TContext> : AcceptHeaderMediaFormatBase<TContext>` - `ContentType` =
  `Constants.XmlContentType` (`application/xml`); selected by `content-type` on read and `accept` on
  write. `GetSerializer(...)` returns the shared `XmlSerializer`.
- `Constants` - `XmlContentType` (`application/xml`), `ContentTypeHeader` (`content-type`).
- `DependencyInjectionExtensions` - `AddXml(Action<XmlOptions>? configure = null)` (open-generic
  `IMediaFormat<>` for every context), `AddXml<TContext>(configure)` (one context), and
  `UseXml<TContext>(configure)` (pipeline-builder convenience) - same shape as `Benzene.Avro`'s
  `AddAvro`/`UseAvro`. All register the shared, options-built `XmlSerializer` via `TryAddSingleton`.

## When to use this package
- When integrating with XML-based or SOAP-like APIs, or legacy systems that speak XML.
- When you need XML available for content negotiation alongside JSON rather than as the sole format.

## Dependencies on other Benzene packages
Direct project references: **Benzene.Abstractions.MessageHandlers** (`ISerializer`,
`IMediaFormat<TContext>`, DI seams), **Benzene.Core.MessageHandlers**
(`AcceptHeaderMediaFormatBase<TContext>`, the content-negotiation base), and
**Benzene.Core.Messages**. `IMiddlewarePipelineBuilder<TContext>` (used by `UseXml`) comes in
transitively via `Benzene.Abstractions.Middleware`; `Benzene.Core.Exceptions.BenzeneException` (thrown
by `DepthGuardedXmlReader`) comes in transitively via `Benzene.Core.Messages`/`Benzene.Core.MessageHandlers`
→ `Benzene.Core.Middleware` → `Benzene.Core`. `System.Xml` is part of the BCL - no NuGet package
reference.

## Important conventions
- Registered as an `IMediaFormat<TContext>`, so XML is negotiated per message via `content-type`/
  `accept`; it does not replace the process default serializer.
- Request/response types are serialized by `System.Xml.Serialization.XmlSerializer`'s rules - decorate
  them with `System.Xml.Serialization` attributes as needed (public parameterless ctor required, etc.).
- Serializers are cached per type and shared as a singleton; the format is stateless.
- Untrusted-input hardening on `Deserialize` (a negotiated request body): DTDs are prohibited
  (entity-expansion DoS, `DtdProcessing.Prohibit` + `XmlResolver = null`), a leading UTF-8 BOM is
  stripped (matching every other transport), and element-nesting depth is bounded by
  `XmlOptions.MaxDepth` (#260, default 32) via `DepthGuardedXmlReader`. Serialization (writing a
  response) is not depth-guarded - it never reads attacker-controlled input.
