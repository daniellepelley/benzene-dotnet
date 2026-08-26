# JSON Schema Validation
JSON Schema is the standard, language-neutral way to describe and validate the shape of JSON documents. Validating with JSON Schema means the contract *is* the validator: the same schema document that describes a payload in your spec can reject non-conforming payloads at the door — with no C# validator classes to write, and no way for the two to drift apart.

### Integration with Benzene
JSON Schema validation runs as pipeline middleware over the **raw request body**, before deserialization — the natural place for a document validator (it checks the wire JSON itself, so it also catches malformed or missing bodies).

For each message it obtains a schema for the current topic and evaluates the body against it. A failing body short-circuits with a **ValidationError** result whose payload is an array of property-scoped messages — the same failure contract as `Benzene.FluentValidation` and `Benzene.DataAnnotations`:

```json
["/name: Value is longer than 5 characters", "/lines/0/sku: Required properties [\"sku\"] are not present"]
```

A `null` schema for a topic means "no validation" and the message passes through.

```csharp
.UseJsonSchema()
.UseMessageHandlers()
```

### Where schemas come from
- **Generated (default):** `DefaultJsonSchemaProvider` derives a schema from the registered handler's request type (JsonSchema.Net.Generation, draft 2020-12, camelCase).
- **Bring your own:** register hand-authored schema documents per request type — the same documents you can serve from the spec via `Benzene.Schema.OpenApi`'s `SuppliedSchemaCatalog`, so published contract and runtime validation stay aligned:

```csharp
var schemas = new SuppliedJsonSchemaCatalog()
    .AddJson(typeof(CreateOrderMessage), File.ReadAllText("schemas/create-order.json"));

services.UsingBenzene(x => x.AddSuppliedJsonSchemas(schemas));
```

- **Fully custom:** implement `IJsonSchemaProvider<TContext>` to source schemas from anywhere (a registry service, embedded resources, per-tenant stores).

### Gap: `DefaultJsonSchemaProvider` does not understand `System.ComponentModel.DataAnnotations`

**The generated (default) schema is a type-shape check only — it silently ignores
`System.ComponentModel.DataAnnotations` attributes.** `DefaultJsonSchemaProvider` generates the schema
via `Json.Schema.Generation`'s `JsonSchemaBuilder().FromType(...)`, which recognizes only its **own**
attribute set (`Json.Schema.Generation.Generation.*` — e.g. `[Required]`/`[Minimum]`/`[MinLength]` from
that namespace). It does **not** read `System.ComponentModel.DataAnnotations`'s attributes of the same
names (`[Required]`, `[Range]`, `[MinLength]`, `[StringLength]`, `[RegularExpression]`, ...) — the ones
`Benzene.DataAnnotations` itself validates against. If your DTO is annotated with
`DataAnnotations` attributes (the common case — `Benzene.FluentValidation` rules and
`System.ComponentModel.DataAnnotations` attributes are the two most idiomatic ways to constrain a C#
DTO), those constraints are **not** enforced by `Benzene.JsonSchema`'s default provider: a payload that
fails `Benzene.DataAnnotations`/`Benzene.FluentValidation` validation (missing required field, value
out of range, string too long, ...) can still pass `Benzene.JsonSchema`'s generated-schema check,
because the generated schema only describes the request type's *shape* (property names/types/
nullability), not those attributes' constraints — **with no warning at generation or validation time.**

If you switch (or add) `Benzene.JsonSchema` expecting it to enforce the same constraints your
`DataAnnotations`/`FluentValidation` rules already do, it will not, silently, unless you close the gap
yourself:

- **Supply a hand-authored schema** via `SuppliedJsonSchemaCatalog` (see above) that encodes the real
  constraints directly (`"required"`, `"minLength"`, `"minimum"`, ...) — the most direct fix, and the
  one that keeps the schema as the single source of truth for the wire contract.
- **Annotate the DTO with `Json.Schema.Generation`'s own attributes** (from the `Json.Schema.Generation`
  namespace this package already depends on) alongside — or instead of — the `DataAnnotations` ones, so
  `DefaultJsonSchemaProvider`'s generator picks the constraints up. Note this means two attribute sets
  on the same DTO if you also validate it with `Benzene.DataAnnotations` elsewhere in the pipeline.
- **Don't rely on `Benzene.JsonSchema` alone** for a DTO that's meaningfully constrained beyond its
  shape — pair it with `Benzene.DataAnnotations`/`Benzene.FluentValidation` in the same pipeline (all
  three adapters share one short-circuit failure contract, so stacking them is safe) if you need the
  stronger checks and don't want to hand-author or dual-annotate the schema.
