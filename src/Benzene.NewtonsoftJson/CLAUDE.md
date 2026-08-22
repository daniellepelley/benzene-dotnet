# Benzene.NewtonsoftJson

## What this package does
Provides an `ISerializer` backed by Newtonsoft.Json (Json.NET), for apps that need Json.NET's
behavior instead of the default `System.Text.Json`-based serializer, plus - matching the shape of
`Benzene.Xml`/`Benzene.Avro`/`Benzene.MessagePack` - a content-negotiated `IMediaFormat<TContext>`
and DI registration extensions (`AddNewtonsoftJson`/`AddNewtonsoftJson<TContext>`/
`UseNewtonsoftJson<TContext>`) so `application/json` traffic can be negotiated onto Json.NET
without touching the process default. `JsonSerializer` alone can still be used directly wherever a
bare `ISerializer` is required.

## Key types
- `JsonSerializer : ISerializer` (namespace `Benzene.NewtonsoftJson`) - all four members are
  `virtual`, so a subclass can override the settings.
  - Serialization uses a `DefaultContractResolver` with a `CamelCaseNamingStrategy`
    (`ProcessDictionaryKeys = false`) — camelCase **property** names only, matching Benzene's default
    `System.Text.Json` serializer. It deliberately does **not** use
    `CamelCasePropertyNamesContractResolver`, whose naming strategy camel-cases **dictionary keys**
    too (`ProcessDictionaryKeys = true`) and silently corrupts free-form keys on round-trip
    (deserialize doesn't undo it). `Serialize<T>` and `Serialize(Type, object)` produce the same
    output - the non-generic overload delegates to the generic one, so the `Type` argument does not
    change the result.
  - Deserialization (`Deserialize<T>` / `Deserialize(Type, string)`) uses default
    `JsonSerializerSettings` (Newtonsoft's property matching is case-insensitive).
  - Settings are constructed inline per call; there is no injectable `JsonSerializerSettings` /
    custom-converter configuration hook other than subclassing and overriding.
- `NewtonsoftJsonMediaFormat<TContext> : AcceptHeaderMediaFormatBase<TContext>` - `ContentType` =
  `Constants.JsonContentType` (`application/json`); selected by `content-type` on read and `accept`
  on write, exactly like `Benzene.Xml`'s `XmlMediaFormat`. Sharing `application/json` with the
  process-default `Benzene.Core.MessageHandlers.MediaFormats.JsonMediaFormat<TContext>` is
  deliberate and non-conflicting: that default is only ever injected as the negotiator's fallback
  (never a negotiated `IMediaFormat<TContext>` candidate - see its own doc comment), so registering
  this format is what makes `application/json` actually negotiate, onto Json.NET instead of
  `System.Text.Json`.
- `Constants` - `JsonContentType` (`application/json`), `ContentTypeHeader` (`content-type`).
- `DependencyInjectionExtensions` - `AddNewtonsoftJson()` (open-generic `IMediaFormat<>` for every
  context), `AddNewtonsoftJson<TContext>()` (one context), and `UseNewtonsoftJson<TContext>()`
  (pipeline-builder convenience). All register the shared `JsonSerializer` via `TryAddSingleton`.

## When to use this package
- When you require Json.NET semantics (its converters, attributes, or nuanced type handling) rather
  than `System.Text.Json`, either as a bare `ISerializer` or negotiated over `application/json` via
  `AddNewtonsoftJson`.
- For migrating existing Json.NET-based request/response models onto Benzene.

## Dependencies on other Benzene packages
- **Benzene.Abstractions** - `ISerializer`, `IBenzeneServiceContainer` (`Benzene.Abstractions.DI`).
- **Benzene.Abstractions.Pipelines** - referenced by the project.
- **Benzene.Abstractions.MessageHandlers** - `IMediaFormat<TContext>`, DI seams.
- **Benzene.Core.MessageHandlers** - `AcceptHeaderMediaFormatBase<TContext>`, the content-negotiation
  base (same base `Benzene.Xml` uses). `IMiddlewarePipelineBuilder<TContext>` (used by
  `UseNewtonsoftJson`) comes in transitively via `Benzene.Abstractions.MessageHandlers`.
- **Benzene.Core.Messages** - referenced by the project.
- **Newtonsoft.Json** (NuGet, 13.0.3) - the Json.NET engine.

## Important conventions
- `JsonSerializer` can still be registered/used directly as a drop-in `ISerializer` without the DI
  extensions, exactly as before.
- Serialized output is camelCase by default; override the `virtual` members to change contract
  resolvers, converters, or other settings.
- Registered as an `IMediaFormat<TContext>` via `AddNewtonsoftJson`, so JSON-via-Json.NET is
  negotiated per message via `content-type`/`accept`, same as `Benzene.Xml`'s `AddXml`.
