# Round 17 review — Infrastructure Product Owner surface (validation, JSON Schema, Avro, Newtonsoft.Json)

Scope: `src/Benzene.FluentValidation`, `src/Benzene.DataAnnotations`, `src/Benzene.JsonSchema`,
`src/Benzene.Avro`, `src/Benzene.NewtonsoftJson`. Reviewed against `daniellepelley/benzene-dotnet`
`main` at `4389bfb`. Round 16's infrastructure findings (`RedisCacheService`/#262, the Microsoft DI
adapter disposal bug/#266) and `Benzene.Resilience.Polly` (#267) were explicitly out of scope and not
re-litigated.

Read-only review. No source files were modified. Every test described below was written, built, run
(`dotnet test test/Benzene.Core.Test/Benzene.Test.csproj --filter ...`), and then deleted (not
committed) — they are reproduced here in full so a fix owner can re-add them as permanent
regressions.

## Headline findings — both in `Benzene.Avro`

### Finding 1 — `AvroDatumConverter` has no `Schema.Type.Map` case at all: any Avro `map` field is broken

**Severity: high.** `map` is a first-class Avro type (not a documented/deliberate limitation the way
"no schema evolution" is — see `src/Benzene.Avro/CLAUDE.md`), and it is fully reachable today via
`AvroOptions.RegisterSchema<T>(...)`, the "schema-registry model common in finance/Kafka deployments"
the package's own docs advertise as a first-class use case. `AvroDatumConverter.ToDatum`/`FromDatum`
switch on `schema.Tag` with explicit cases for `Union`, `Record`, `Array`, and the primitives — there
is **no case for `Schema.Type.Map`**, so it silently falls through to the primitive `default` branch:

```csharp
// ToDatum
default:
    return value;               // a raw Dictionary<TKey,TValue> is handed straight to GenericDatumWriter unconverted

// FromDatum
default:
    return datum == null ? DefaultValue(targetType) : ConvertPrimitive(datum, targetType);
```

Unlike `Record`/`Array`, a map's *values* never get recursively converted to the datum shape
`GenericDatumWriter`/`GenericDatumReader` expect (`GenericRecord` for record values, `object[]` for
array values), and on deserialize the raw `IDictionary` datum from the Avro reader is handed to
`Convert.ChangeType`, which cannot target a `Dictionary<,>`.

Two concrete, independently-verified failure modes — the *simplest possible* map (primitive-to-string)
already crashes, and the "record-within-array-within-map" shape the assignment called out crashes even
harder:

```csharp
public class InnerRecord { public int Id { get; set; } public string Label { get; set; } = ""; }
public class OuterRecord { public Dictionary<string, List<InnerRecord>> Buckets { get; set; } = new(); }

const string OuterSchema = """
{
  "type": "record", "name": "OuterRecord",
  "fields": [ { "name": "Buckets", "type": { "type": "map", "values":
    { "type": "array", "items": { "type": "record", "name": "InnerRecord",
      "fields": [ {"name":"Id","type":"int"}, {"name":"Label","type":"string"} ] } } } } ]
}
""";

var serializer = new AvroSerializer(new AvroOptions().RegisterSchema<OuterRecord>(OuterSchema));
serializer.Serialize(new OuterRecord { Buckets = { ["a"] = new() { new() { Id = 1, Label = "one" } } } });
```

throws on **serialize**:

```
Avro.AvroException : Array required to write against array schema but found
System.Collections.Generic.List`1[...InnerRecord]
   at Avro.Generic.GenericDatumWriter`1.GenericArrayAccess.EnsureArrayObject(Object value)
   at Avro.Generic.PreresolvingDatumWriter`1.WriteArray(...)
   at Avro.Generic.PreresolvingDatumWriter`1.DictionaryMapAccess.WriteMapValues(...)
   ...
   at Benzene.Avro.AvroSerializer.SerializeToAvroBytes(Type type, Object payload)
```

And the control case — a map of plain strings, no records/arrays involved —round-trips through
`Serialize` but throws on **deserialize**:

```csharp
public class PrimitiveMapHolder { public Dictionary<string, string> Tags { get; set; } = new(); }
const string PrimitiveMapSchema = """
{ "type":"record","name":"PrimitiveMapHolder",
  "fields":[{"name":"Tags","type":{"type":"map","values":"string"}}] }
""";
var serializer = new AvroSerializer(new AvroOptions().RegisterSchema<PrimitiveMapHolder>(PrimitiveMapSchema));
var payload = serializer.Serialize(new PrimitiveMapHolder { Tags = { ["env"] = "prod" } }); // succeeds
serializer.Deserialize<PrimitiveMapHolder>(payload);                                        // throws
```

```
System.InvalidCastException : Object must implement IConvertible.
   at System.Convert.ChangeType(Object value, Type conversionType, IFormatProvider provider)
   at Benzene.Avro.AvroDatumConverter.ConvertPrimitive(Object datum, Type targetType)
   at Benzene.Avro.AvroDatumConverter.FromDatum(Schema schema, Object datum, Type targetType)
   at Benzene.Avro.AvroDatumConverter.FromRecord(RecordSchema schema, Object datum, Type targetType)
```

So: any type with a `map`-typed field, registered via an explicit schema, is unusable end-to-end —
crashes on deserialize at best (primitive values), crashes on serialize at worst (complex values). This
is a straightforward correctness gap (missing switch arm), not a documented limitation, and it sits
right next to `Union`/`Record`/`Array`, which *are* handled — it reads as an oversight rather than a
deliberate scope cut.

**Verification:** `test/Benzene.Core.Test/Plugins/Avro/AvroMapNestingTest.cs` (temporary, deleted after
verification) — `RoundTrips_RecordWithinArrayWithinMap` and `RoundTrips_PrimitiveValuedMap`, both red.

### Finding 2 — `AvroDatumConverter.NonNullBranch` silently miscodes any union with 3+ branches

**Severity: high (silent data corruption, not just a crash).** `AvroDatumConverter` resolves *every*
union field — on both serialize and deserialize — via:

```csharp
private static Schema NonNullBranch(UnionSchema union)
{
    return union.Schemas.FirstOrDefault(s => s.Tag != Schema.Type.Null) ?? union.Schemas[0];
}
```

This is correct only for the common 2-branch `["null", X]` "optional field" shape generated by
`AvroSchemaGenerator`'s own reflection path. It is **not** correct for a union with two or more
non-null branches — e.g. a hand-authored, explicit-schema "polymorphic value" field
`["null","string","long","boolean"]`, again reachable via `AvroOptions.RegisterSchema<T>`. For such a
union, `ToUnionDatum`/`FromUnion` always pick the *first declared* non-null branch, regardless of the
value's actual runtime type (serialize) or the branch the wire data actually used (deserialize).

Verified round-trip corruption — a `long` and a `bool` sent through a `["null","string","long","boolean"]`
union both come back as the *string* `"long"`/`"boolean"`-branch coercion of the first non-null branch
("string"), not as their original type or value:

```csharp
public class MultiUnionRecord { public object? Value { get; set; } }
const string MultiUnionSchema = """
{ "type":"record","name":"MultiUnionRecord",
  "fields":[{"name":"Value","type":["null","string","long","boolean"]}] }
""";
var serializer = new AvroSerializer(new AvroOptions().RegisterSchema<MultiUnionRecord>(MultiUnionSchema));

serializer.Deserialize<MultiUnionRecord>(serializer.Serialize(new MultiUnionRecord { Value = true }));
// -> Value is the STRING "True", not the bool `true`

serializer.Deserialize<MultiUnionRecord>(serializer.Serialize(new MultiUnionRecord { Value = 42L }));
// -> Value is the STRING "42", not the long `42`
```

Both assertions (`Assert.IsType<bool>(result.Value)` / `Assert.IsType<long>(result.Value)`) fail with
`Actual: System.String`. Note this isn't merely "wrong formatting" — the *type* itself changes, so any
downstream code that pattern-matches or casts on the expected branch type (`(bool)result.Value`,
`result.Value is long`) breaks, and for value types that don't happen to have a friendly
`Convert.ToString` (e.g. a boolean serialized through a `["null","boolean","long"]` union, where
`Convert.ToBoolean(42L)` silently succeeds and returns `true`) the *original value itself is lost*, not
just its CLR type. Because the same fixed "first non-null branch" is used on both write and read, a
number of value/first-branch-type combinations do "round-trip" back to the same (wrong) value by
coincidence — which makes this the more dangerous of the two Avro findings: it doesn't reliably crash,
it silently drifts.

**Verification:** `test/Benzene.Core.Test/Plugins/Avro/AvroMultiBranchUnionTest.cs` (temporary, deleted
after verification) — `RoundTrips_BooleanValue_ThroughAThreePlusBranchUnion` and
`RoundTrips_LongValue_ThroughAThreePlusBranchUnion`, both red exactly as predicted.

**Recommendation for both Avro findings:** the fix is at the same seam in both cases —
`AvroDatumConverter` needs to resolve the union branch by the *value's actual runtime shape*
(serialize) and by the *raw datum's actual runtime type* (deserialize, which `GenericDatumReader`
already resolved correctly against the wire's branch index — `NonNullBranch` just needs to stop
discarding that information), and add a proper `Schema.Type.Map` arm mirroring `ToArray`/`FromArray`
(convert each value recursively, target a `Dictionary<TKey,TValue>` on read). Both are scoped, additive
changes to one file; recommend fixing together since they're the same root cause (the two-branch-union
assumption baked into the file predates general union/map support).

## Areas investigated with no bug found

### `Benzene.FluentValidation` — cascade/rule-set propagation; validator throws instead of returning a failure

- **`CascadeMode`**: set on the validator itself (globally via `ValidatorOptions.Global`, or per-rule
  via `.Cascade(...)`), and `ValidationMiddleware.HandleAsync` just calls
  `await validator.ValidateAsync(context.Request)` — the validator's own configured cascade behavior
  applies unmodified. Nothing in Benzene's wrapper touches or needs to touch it.
- **`RuleSet`**: `ValidationMiddleware` calls `ValidateAsync(request)` with no `ValidationContext`/
  `IncludeRuleSets` option, so (correctly, matching plain FluentValidation semantics) only rules
  outside any named `RuleSet` run — a validator author relying on named rule sets to segment validation
  gets no help selecting one from Benzene today, but that's an absent *feature*, not a contract
  violation: `Benzene.FluentValidation` never claims rule-set selection support (grepped
  `docs/fluent-validation.md`, the package's `CLAUDE.md`/README — no mention). `FluentValidationSchemaBuilder`
  (used for schema/documentation generation) does read every rule via `CreateDescriptor().GetMembersWithValidators()`
  regardless of rule-set membership, which means a rule confined to a named ruleset shows up in the
  generated schema even though the middleware would never enforce it by default — a real
  schema/runtime-behavior mismatch, but only for an already-unsupported combination (named rule sets),
  so it doesn't clear the bar as a fresh, actionable finding this round.
- **Validator throws instead of returning a failed `ValidationResult`**: `ValidationMiddleware.HandleAsync`
  has no try/catch around `validator.ValidateAsync(...)`; a thrown exception propagates unchanged through
  `MiddlewareApplication<...>.HandleAsync` (`src/Benzene.Core.Middleware/MiddlewareApplication.cs`, no
  exception handling of its own) to whatever `ExceptionHandlerMiddleware<TContext>` (or transport-level
  handling) the app has wired in — the same path any other middleware's exception takes. It is never
  reinterpreted as a `ValidationError`-status `BenzeneResult`; `DefaultValidationStatusMapper.GetStatus`
  is only ever called on the "validation genuinely failed" branch (`!validationResult.IsValid`), which a
  thrown exception never reaches. So a throwing validator and a failing validator are cleanly
  distinguishable (exception vs. typed result) — confirmed by reading the full call chain, not just the
  middleware in isolation.

### `Benzene.DataAnnotations` — `[Required]`/`[Range]`/nullable value types; `IValidatableObject` invocation

- `ValidationMiddleware` delegates directly to `System.ComponentModel.DataAnnotations.Validator.TryValidateObject(request, ctx, results, validateAllProperties: true)` with no additional logic layered on
  top, so nullable-value-type interaction with `[Required]`/`[Range]` (e.g. `[Range]` treating an unset
  `int?` as valid, `[Required]` on a non-nullable value type being a permanent no-op) is exactly the BCL's
  own well-documented behavior — nothing Benzene-specific to find.
- **`IValidatableObject.Validate()` invocation**: confirmed empirically (small standalone repro, not
  just documentation) that `Validator.TryValidateObject(..., validateAllProperties: true)` *does* call
  `IValidatableObject.Validate()` — but only when the object has **no** attribute-level validation errors
  already. When both an attribute failure (e.g. `[Required]`) and an `IValidatableObject` failure exist
  on the same request, only the attribute error surfaces; `Validate()` is never invoked (`ValidateCalled`
  stays `false` in the repro). This looked like a promising lead — `docs/data-annotations.md` claims
  `IValidatableObject` is "honored the same way it would be by ASP.NET Core model validation" — so I
  built a minimal ASP.NET Core (`net10.0`, `Microsoft.NET.Sdk.Web`) controller with the identical
  `IValidatableObject` type and confirmed **ASP.NET Core's own model validation exhibits the identical
  short-circuit** (`ValidateCalled` also `false`, response body only contains the `[Required]` error).
  So the doc's comparison is accurate, not misleading, and this is faithful, matching behavior — not a
  Benzene defect.

### `Benzene.JsonSchema` — `$ref` cycles; unversioned schema-catalogue gaps

- **`$ref` cycles**: reflection-generated schemas for a self-referencing CLR type (`class TreeNode { List<TreeNode> Children; }`) produce a valid recursive `$ref: "#"` schema via `JsonSchemaBuilder().FromType(...)`, and evaluate correctly against nested instance data. A hand-authored two-schema `$ref` cycle (`$defs/A` ↔ `$defs/B`) evaluates correctly and quickly against genuinely-nested (finite) documents of nontrivial depth — `Json.Schema.Net`'s evaluator walks the *document*, not the schema, so a schema-level cycle can't drive unbounded evaluation against a necessarily-finite JSON document; and `System.Text.Json.JsonDocument.Parse` (used by `JsonSchemaMiddleware`, no custom `JsonDocumentOptions`) already enforces its own default 64-level parse depth limit, cleanly rejecting a maliciously deep body as a `JsonException` → `MalformedBody` validation error before it ever reaches the schema evaluator. No crash, no hang, no bug found.
- **Version not in the schema catalogue at all**: confirmed `IVersionSelector`'s "exact match, else highest available version" fallback (`VersionSelector.cs`) is the documented, spec-mandated default (`docs/specification/versioning.md` §3: "`IVersionSelector` (default: exact match, else highest available version)"), not an oversight — and confirmed (initially suspected the opposite, then disproved with a red-then-green test) that `JsonSchemaMiddleware`'s failure-reporting path and `IJsonSchemaProvider`'s schema-selection path resolve to the *same* handler/version for `BenzeneMessageContext`, because task #98's fix (`work/archive/bug-fix-designs-round10-2026-08.md` WP-V) already moved the version-join into `IMessageTopicGetter<BenzeneMessageContext>.GetTopic()` itself (`BenzeneMessageGetter.GetTopic`, registered as the concrete `IMessageTopicGetter<BenzeneMessageContext>`) — so every caller of the plain `GetTopic()`, not just the ones that remember to call the `GetVersionedTopic` helper, already gets the version-augmented topic for this transport. A `SuppliedJsonSchemaCatalog` entry missing for the resolved type also degrades gracefully (`SuppliedJsonSchemaProvider` falls back to the generated-from-type schema). No bug found.

### `Benzene.NewtonsoftJson` — custom `JsonConverter` interaction with the framework's own settings; `TypeNameHandling`

- Initially suspected an inconsistency: `Serialize`/`Serialize<T>`/`Deserialize<T>` all pass an explicit
  `JsonSerializerSettings` instance (camelCase resolver, or an empty one) while `Deserialize(Type, string)`
  passes none — which looked like it would mean ambient `JsonConvert.DefaultSettings` (the standard place
  to register a process-wide custom `JsonConverter`) is honored by one method and silently ignored by
  the other three. **Verified this empirically and it does not hold**: `JsonSerializer.CreateDefault(settings)`
  (what `JsonConvert.DeserializeObject`/`SerializeObject` use under the hood) always applies
  `JsonConvert.DefaultSettings` *first*, then *merges* any explicitly-passed settings on top of it —
  collections like `Converters` are additive, not replacing. A minimal repro (register a custom
  `JsonConverter` via `JsonConvert.DefaultSettings`, then call both `JsonConvert.DeserializeObject(json, type)`
  and `JsonConvert.DeserializeObject<T>(json, new JsonSerializerSettings())`) shows the converter applies
  in **both** cases identically. So a custom `JsonConverter` registered the idiomatic Json.NET way is
  honored consistently across all four of `Benzene.NewtonsoftJson.JsonSerializer`'s members — no bug.
  (`Serialize`'s hardcoded camel-case `ContractResolver` *would* override a `DefaultSettings`-registered
  custom resolver, since `ContractResolver` isn't a merged collection — but that's the class doing its
  one documented job, forcing a consistent camelCase wire contract, and every member is `virtual`
  precisely so a subclass can override it; not a contract violation.)
- **`TypeNameHandling`**: grepped the entire repository — `TypeNameHandling` does not appear anywhere.
  No `$type`/polymorphic-deserialization code path is exercised by this package or the framework at all,
  so there is nothing to find here; this is simply unused surface, not a hidden landmine.

## Bottom line

Two genuine, concrete bugs found, both in `Benzene.Avro`'s `AvroDatumConverter`, both reachable through
the package's own advertised "explicit/registered schema" use case (not exotic misuse): missing `map`
support (crashes) and the 2-branch-only union assumption (silent type/value corruption for 3+ branch
unions). `Benzene.FluentValidation`, `Benzene.DataAnnotations`, `Benzene.JsonSchema`, and
`Benzene.NewtonsoftJson` were all pushed on the specific angles assigned and came back clean — including
two cases (`IValidatableObject` short-circuiting, `JsonSchema` version-catalogue-gap handling) that
looked like bugs on first read and were disproved with an actual empirical/regression test rather than
taken on faith.
