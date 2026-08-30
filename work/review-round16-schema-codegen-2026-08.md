# Round 16 adversarial review — schema, validation, code generation

Scope: `src/Benzene.*Schema*`, `src/Benzene.CodeGen.*` (Markdown, ApiGateway, Terraform, typed
clients, OpenApi, LambdaTestTool, SourceGenerators, Cli/Cli.Core), `src/Benzene.*Validation*`,
`Benzene.JsonSchema`, `Benzene.DataAnnotations`. Reviewed against commit `28473b0` on `main`.

Method: read the code, then for each candidate wrote a small xUnit test directly in
`test/Benzene.Core.Test` (matching the existing suite's structure), ran it with
`dotnet test --filter <TestName>`, confirmed red, then deleted the test file (no source or test
files were left modified — `git status --porcelain` is clean of my changes; a few untracked files
from other concurrently-running review agents in the same shared checkout remain, not mine).

Three findings, all genuine correctness bugs matching the bug classes called out in the brief.

---

## Finding 1 — `OpenApiSchemaCSharpTypeBuilder` interpolates discriminator strings into generated C# unescaped: the fourth "unescaped interpolation into structured output" generator

**File:** `src/Benzene.CodeGen.Client/OpenApiSchemaCSharpTypeBuilder.cs`, lines 68–74.

```csharp
lineWriter.WriteLine(
    $"[JsonPolymorphic(TypeDiscriminatorPropertyName = \"{schema.Discriminator!.PropertyName}\")]", 1);
foreach (var mapping in schema.Discriminator.Mapping)
{
    lineWriter.WriteLine(
        $"[JsonDerivedType(typeof({_nameFormatter.Format(RefName(mapping.Value))}), \"{mapping.Key}\")]", 1);
}
```

`schema.Discriminator.PropertyName` and each `mapping.Key` are interpolated directly into a C#
string-literal position with no escaping. Both come straight from the OpenAPI schema — for a
discriminator built by hand, via `SuppliedSchemaCatalog`, or via any non-Swashbuckle schema source,
`mapping.Key` is an arbitrary caller-supplied string (the discriminator *value*, not an identifier)
with no guarantee it excludes `"` or `\`. This is exactly the bug class already found and fixed
three times in other generators this round-family: unescaped YAML string interpolation in
`ApiGatewayBuilderV1` (#212, now routed through `YamlValueEscaping.EscapeForDoubleQuoted`),
unescaped Markdown interpolation (#86), and unescaped HCL interpolation in the Terraform builders
(#244, now routed through `NameFormatter.EscapeHclString` on every user-supplied value — see e.g.
`TerraformLambdaBuilder.cs` lines 43–49). `OpenApiSchemaCSharpTypeBuilder` has no equivalent
C#-string-literal escaper at all (a `EscapeForCSharp`/`SymbolDisplay`-style helper exists elsewhere
in the codebase, e.g. `Benzene.CodeGen.SourceGenerators/MessageHandlerSourceGenerator.cs`, but
isn't used here), and the codebase's own dedicated Roslyn-compile test for this generator
(`CodegenOutputCompilesTest.cs`, covering #66/#67/#240) never exercises a discriminator schema, so
nothing catches it.

**Concrete failure:** a discriminator mapping value containing a double quote — realistic for a
size/dimension-flavoured value like `12" wheel`, or any value a schema author didn't realize would
land inside a C# string literal — produces:

```csharp
[JsonDerivedType(typeof(CardPayment), "12" wheel")]
```

which is not just semantically wrong but **doesn't compile**: the embedded `"` terminates the string
literal early and the rest of the line becomes garbage tokens, cascading into unrelated syntax
errors for the whole file (confirmed below — 7 distinct Roslyn diagnostics from one bad character).
A hand-authored discriminator mapping key with a quote, backslash, or newline anywhere in the
document (this build path is reachable from `SuppliedSchemaCatalog`/hand-built `EventServiceDocument`s,
not only reflection-derived schemas) silently breaks the whole generated client SDK's build with no
warning at generation time — the CLI's `build` command reports success and writes uncompilable
`.cs` files.

**Proof (red test, run and then deleted per instructions):** built a `PaymentMethod`/`CardPayment`
pair with `Discriminator.Mapping["12\" wheel"] = "#/components/schemas/CardPayment"`, ran
`OpenApiSchemaCSharpTypeBuilder.BuildCodeFiles`, then compiled every emitted file with
`CSharpCompilation` (same pattern as `CodegenOutputCompilesTest.cs`). Result:

```
PaymentMethod.cs(9,48): error CS1003: Syntax error, ',' expected
PaymentMethod.cs(9,53): error CS1003: Syntax error, ',' expected
PaymentMethod.cs(9,53): error CS1010: Newline in constant
PaymentMethod.cs(9,56): error CS1026: ) expected
PaymentMethod.cs(9,56): error CS1003: Syntax error, ']' expected
PaymentMethod.cs(9,48): error CS0103: The name 'wheel' does not exist in the current context
PaymentMethod.cs(9,6): error CS1729: 'JsonDerivedTypeAttribute' does not contain a constructor that takes 4 arguments
```
generated line: `[JsonDerivedType(typeof(CardPayment), "12" wheel")]`

**Fix shape:** escape `schema.Discriminator.PropertyName` and each `mapping.Key` with a proper
C#-string-literal escaper before interpolating (e.g. `SymbolDisplay.FormatLiteral(value, true)` from
`Microsoft.CodeAnalysis.CSharp`, already a transitive dependency via the Roslyn-based test project —
or a small local escaper mirroring `NameFormatter.EscapeHclString`/`YamlValueEscaping`), and add a
`CodegenOutputCompilesTest`-style theory case with an adversarial discriminator value.

---

## Finding 2 — `JsonOpenApiSchemaBuilder.Create` throws on a JSON float or null value

**File:** `src/Benzene.Schema.OpenApi/JsonOpenApiSchemaBuilder.cs`, lines 18–31.

```csharp
private OpenApiSchema Create(string key, JToken jToken)
{
    return jToken.Type switch
    {
        JTokenType.String => CreateStringSchema(),
        JTokenType.Date => CreateDateTimeSchema(),
        JTokenType.Integer => CreateIntegerSchema(),
        JTokenType.Boolean => CreateBooleanSchema(),
        JTokenType.Guid => CreateGuidSchema(),
        JTokenType.Array => CreateArraySchema(key, jToken),
        JTokenType.Object => CreateObjectSchema(key, jToken),
        _ => throw new Exception($"No map for {jToken.Type}")
    };
}
```

This is the same crash-on-legitimate-input shape as #241/#242/#243 (`MapOperationType`,
`CreateArraySchema`'s prior unconditional `jToken.First()`, `EventServiceDocumentDeserializer`): a
schema-building method that throws instead of handling an ordinary, spec-legal input it simply
doesn't have a branch for. Here the switch has no case for `JTokenType.Float` (an ordinary JSON
number with a decimal point, e.g. `3.14`) or `JTokenType.Null` (an ordinary JSON `null` value) — both
completely unremarkable in example JSON. This method is reachable from the documented public API
`EventServiceDocumentBuilder.AddJsonEvent(topic, typeName, json)`
(`src/Benzene.Schema.OpenApi/EventService/EventServiceDocumentBuilderExtensions.cs`), the
spec-driven route for hand-supplied event schemas not backed by a C# type. Any example payload with
a price, percentage, rating, or other decimal field, or any nullable field represented as JSON
`null` (extremely common for an optional field in a captured real-world example), throws and aborts
spec/schema generation entirely for the whole document — not degraded output, a hard crash.

**Proof (red test, run and then deleted):**

```csharp
new JsonOpenApiSchemaBuilder().CreateSchema("Order", "{\"price\":3.14}");
```
→ `System.Exception : No map for Float`

```csharp
new JsonOpenApiSchemaBuilder().CreateSchema("Order", "{\"middleName\":null}");
```
→ `System.Exception : No map for Null`

Both thrown from `JsonOpenApiSchemaBuilder.Create`, `JsonOpenApiSchemaBuilder.cs:29`, via
`CreateObjectSchema`'s property-dictionary projection (`JsonOpenApiSchemaBuilder.cs:113`).

**Fix shape:** add `JTokenType.Float => CreateNumberSchema()` (mirroring `CreateIntegerSchema` but
`Type = "number"`) and a `JTokenType.Null` branch — the honest answer for a null-valued example
field is an untyped/nullable placeholder schema, the same convention `CreateArraySchema` already
established for "nothing in the example to infer from" after #242 (an untyped `OpenApiSchema`, or
`Nullable = true` with no further type inference possible from a single null sample).

---

## Finding 3 — `MarkdownTypeBuilder` silently drops the property name for a map (`additionalProperties`) or empty-object property, rendering an unlabelled bare `{}`

**File:** `src/Benzene.CodeGen.Markdown/MarkdownTypeBuilder.cs`, `MapProperty`, lines 71–88 (and the
matching array branch, lines 113–117).

```csharp
private void MapProperty(string name, string? reference, OpenApiSchema openApiSchema, ILineWriter lineWriter)
{
    if (openApiSchema.Type == "object")
    {
        if (openApiSchema.Properties.Any())
        {
            lineWriter.WriteLine($"{CodeGenHelpers.Camelcase(name)}: {{");
            ...
        }
        else
        {
            lineWriter.WriteLine("{}");   // <-- `name` is never written here
        }
    }
```

This is the same bug class as the discriminator/oneOf-blindness family (#25/#53/#239 in the schema
comparer, #86 in this same Markdown generator for a different shape) but for `additionalProperties`
(#168's family, previously fixed only in `SchemaCompatibilityComparer`/`benzene diff`, and in
`CSharpTypeName.GetName` for the C# client generator — see its comment at
`src/Benzene.CodeGen.Client/OpenApiSchemaCSharpTypeBuilder.cs:176-183` explicitly calling out that
every `additionalProperties` value type, not just `string`, must be handled). `MarkdownTypeBuilder`
was never given the same treatment: a property whose schema is `type: object` with a value schema
under `additionalProperties` but no own declared `properties` (the normal shape for a
`Dictionary<string, T>`-typed property reflected off a real C# type) has `Properties.Any()` false,
so it falls into the `else` branch — which hard-codes `"{}"`  with **no property name at all**. The
property doesn't render as `scores: object` (which would at least be present, if uninformative) — it
doesn't render as anything identifiable; the generated markdown gets a bare, anonymous `{}` line
that a reader cannot even associate with the field that produced it. The identical pattern recurs at
line 116 for an array of such objects (`"{}[]" `, also missing `name`).

**Proof (red test, run and then deleted):** a `Root` schema with one property `scores: { type:
object, additionalProperties: { type: integer } }` (i.e. `Dictionary<string, int> Scores` in the
reflected C# shape) renders as:

```
{
    {}
}
```

— the property is entirely unnamed in the output; a real generated service doc would show this
line with no way to tell which field it came from, immediately preceding or following whatever
property is next.

**Fix shape:** in the `else` branch of both the object and array-of-object cases, always emit
`{CodeGenHelpers.Camelcase(name)}: ` before the placeholder (`{name}: {}` / `{name}: {}[]`), and —
to actually carry the information rather than just fixing the label — special-case
`AdditionalProperties != null` the way `CSharpTypeName.GetName` already does, rendering something
like `scores: {[string]: integer}` instead of a bare `{}`.

---

## Areas checked and found solid (no findings)

- **`SchemaCompatibilityComparer`** (`src/Benzene.Schema.OpenApi/Compatibility/SchemaCompatibilityComparer.cs`):
  already recurses into `oneOf`, `anyOf`, `allOf` (matching by `$ref` name, then unclaimed
  discriminator-mapping key, then position — a deliberately careful scheme, see the docstrings on
  `IndexVariants`/`VariantKey`) and `additionalProperties` (the #168 fix). This is the most complete
  of the schema-diffing code and I did not find a gap in its union/map handling.
- **`benzene diff`** (`DiffCommand.cs`): both JSON (`JsonConvert.SerializeObject`) and text output
  paths are safe; no unescaped interpolation into the JSON report.
- **`ExampleBuilder`/`LambdaTestFilesBuilder`** (the fourth candidate I checked most closely before
  finding #1): all use `JsonConvert.SerializeObject`, not manual interpolation, so the
  Lambda-Test-Tool JSON output is safe from the escaping bug class.
- **`Benzene.DataAnnotations`**: `Validator.TryValidateObject(..., validateAllProperties: true)`
  does not recurse into nested complex properties — standard BCL `Validator` behaviour, and
  explicitly documented as a known, deliberate limitation in the package's own `CLAUDE.md` ("a
  deliberately minimal alternative to `Benzene.FluentValidation`"). Not a bug.
- **`OpenApiDocumentBuilder`/`JsonOpenApiSchemaBuilder.CreateArraySchema`/
  `EventServiceDocumentDeserializer`**: re-checked the #241/#242/#243 fixes; all three hold up under
  the edge cases the original fixes targeted. `CreateArraySchema`'s empty-array handling in
  particular is solid — the gap I found (Finding 2) is a sibling switch arm in the same file that
  the #242 fix didn't touch.

No other bugs meeting this codebase's bar (genuine correctness bug / crash / silent data
corruption / spec-contract violation) turned up in the remaining CodeGen targets
(`Benzene.CodeGen.ApiGateway`, `Benzene.CodeGen.Terraform`, `Benzene.CodeGen.SourceGenerators`,
`Benzene.CodeGen.Cli`/`Cli.Core`'s `build`/`healthcheck` commands, `Benzene.JsonSchema`,
`Benzene.FluentValidation`) after a systematic grep for string-interpolation-into-structured-output
across every `Benzene.CodeGen.*` project and a manual read of the escaping call sites that already
exist (`YamlValueEscaping`, `NameFormatter.EscapeHclString`) to see whether any sibling emission
path had been missed.
