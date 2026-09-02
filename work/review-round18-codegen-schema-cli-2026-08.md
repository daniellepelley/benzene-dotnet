# Round 18 adversarial review — CodeGen, CLI, Schema, Descriptor, CloudService, Templates

Scope: `Benzene.CodeGen.Core/.Build/.Client/.SourceGenerators`, `Benzene.CodeGen.ApiGateway/
.LambdaTestTool/.Markdown/.Terraform`, `Benzene.CodeGen.Cli/.Cli.Core`, `Benzene.SchemaRegistry.Core`,
`Benzene.Schema.Compatibility`, `Benzene.Schema.OpenApi`, `Benzene.Descriptor`, `Benzene.CloudService`,
`Benzene.CloudService.Probe`, `templates/`. Reviewed against commit `7f642b2` on `main`.

Read `work/outstanding-bugs.md`, `work/review-round17-cli-registry-2026-08.md` and
`work/review-round16-schema-codegen-2026-08.md` first. Round 16's three escaping/crash findings in
this territory (#263, #264, #265) and round 17's `healthcheck` `WriteJson` crash (#268-ish, see
`outstanding-bugs.md` ~line 4830) are all verified fixed in current source and are not
re-litigated. This round found four new, genuine issues — one of them (Finding 1) materially more
severe than anything previously found in this territory: it's a filesystem write outside the
requested output directory, not a codegen-time crash or a cosmetic escaping glitch.

Method: read the code (no `dotnet` SDK available in this environment — every claim below is a
manual trace against the actual source, cross-checked against the existing test suite to confirm
the gap isn't already covered elsewhere). Each finding below describes the regression test that
would prove it, for a future round with CI access to write and run it.

---

## Finding 1 (headline, high severity) — a schema/topic name from an externally-sourced spec is used, completely unsanitized, as the generated **file name**, and `CodeFileWriter` writes wherever that name points — directory traversal / arbitrary file write

**Files:**
`src/Benzene.CodeGen.Client/OpenApiSchemaCSharpTypeBuilder.cs:107,149`,
`src/Benzene.CodeGen.LambdaTestTool/LambdaTestFilesBuilder.cs:27,36`,
`src/Benzene.CodeGen.Core/CodeFileWriter.cs:5-27`.

`benzene build`/`benzene lambda-test-tool` are documented, supported ways to generate code from a
spec fetched from **outside the local checkout**: `SpecSourceResolver.Resolve`
(`src/Benzene.CodeGen.Cli.Core/Commands/Spec/SpecSourceResolver.cs`) accepts `--url` (arbitrary
HTTP), `--mesh` (a mesh manifest naming any registered service), or `--lambda-name` (an arbitrary
AWS Lambda), in addition to a local `--file`. Nothing downstream distinguishes "spec I trust because
I typed the path myself" from "spec I just pulled from a URL/mesh registry/Lambda I don't control" —
and `EventServiceDocumentDeserializer`/`SchemaDeserializer` places **no restriction whatsoever** on
the strings that become schema names or topic ids:

```csharp
// SchemaDeserializer.cs:115-124 — GetRequest
var request = JsonConvert.DeserializeObject<RequestResponse>(jToken.ToString(Formatting.Indented));
request!.Request = GetSchema(jToken, "request");
request.Response = GetSchema(jToken, "response");
```

`request.Topic` and every key of `components.schemas` are taken verbatim from attacker-controlled
JSON — no character set restriction, no length limit.

That unsanitized value then becomes a generated **file name**, in two places:

```csharp
// OpenApiSchemaCSharpTypeBuilder.cs:32-107 (BuildSimpleType) and :120-149 (BuildEnumType)
private ICodeFile BuildSimpleType(string name, OpenApiSchema schema, ...)
{
    ...
    return new CodeFile($"{name}.cs", lineWriter.GetLines());   // `name` = raw schema-catalogue key
}
```

```csharp
// LambdaTestFilesBuilder.cs:21-41
var filePrefix = requestResponse.Topic.Replace(":", "-");   // only ':' is touched
...
codeFiles.Add(new CodeFile($"{filePrefix}-{FormatTransport(exampleBuilder.Transport)}.json", ...));
```

Both differ from every other name-emission path in the same packages: `_nameFormatter.Format(name)`
**is** used for the emitted C# class/enum name a few lines above (line 83/138), which strips
everything but letters/digits/underscore — but the raw, unstripped `name`/`filePrefix` is what
becomes the **file name**. `TopicMethodName`/`TopicReversedMethodName`/`CSharpNameFormatter` (used
for every other file name in this territory — `MessageHandlerBuilder.cs:76`,
`MessageClientSdkBuilder.cs:67-69`, `CSharpSdkTypeBuilder.cs:108`) all route through
`RemoveNonIdentifierCharacters()`, which would have neutralized this. These two call sites don't.

`CodeFileWriter.CreateAsync` then writes wherever that name points, with no containment check:

```csharp
// CodeFileWriter.cs:5-27
var path = Path.Combine(directoryPath, codeFile.Name);
var fileDirectory = Path.GetDirectoryName(path);
if (!string.IsNullOrEmpty(fileDirectory) && !Directory.Exists(fileDirectory))
{
    Directory.CreateDirectory(fileDirectory);        // creates it, wherever it resolves to
}
return File.WriteAllLinesAsync(path, codeFile.Lines);
```

`Path.Combine`/`Path.GetDirectoryName` do not normalize or reject `..` segments — `Path.Combine("/out",
"../../../../tmp/evil.cs")` resolves to a path outside `/out`, and `CodeFileWriter` happily creates
the intermediate directories and writes there. This isn't a latent capability nobody meant to use —
`test/Benzene.Core.Test/Autogen/CodeGen/Core/CodeFileWriterTest.cs`'s
`CreateAsync_NestedFileNames_CreatesSubdirectoriesAndWritesEachFile` test explicitly documents and
pins that a `codeFile.Name` carrying subdirectory segments (`"UserGet/UserGetServiceClient.cs"`) is
honored and `AtomicClientSdkBuilder.cs:161` relies on exactly that (`$"{clientName}/{file.Name}"`,
itself carrying the same unsanitized inner schema name from `OpenApiSchemaCSharpTypeBuilder`) —
the feature is real and intentional, it's just never contained to `directoryPath`.

**Concrete failure scenario:** run `benzene build --output client --url
https://partner.example.com/spec.json --directory ./generated` against a partner/third-party
service's spec (a documented, ordinary use of `--url`/`--mesh` — generating a typed client for
*someone else's* service). If that spec's `components.schemas` includes a key like
`"../../../../home/user/.bashrc"`, `OpenApiSchemaCSharpTypeBuilder.BuildCodeFiles` emits a
`CodeFile` named `"../../../../home/user/.bashrc.cs"`; `CodeFileWriter.CreateAsync` combines it with
`./generated` and writes the generated C# text straight into the operator's home directory,
overwriting whatever is there — no error, `benzene build` reports success ("N code files created
… Completed"). The same shape reaches `benzene lambda-test-tool` via a topic id like
`"../../../../etc/cron.d/pwn"` (topics need no colon; `Replace(":", "-")` is a no-op on it),
landing attacker-supplied JSON content at a path the operator never named on the command line.

**Proof (manual trace, not run — no SDK here):**
```csharp
var schemas = new Dictionary<string, OpenApiSchema> { ["../../../../tmp/evil"] = new OpenApiSchema { Type = "object" } };
var files = new OpenApiSchemaCSharpTypeBuilder("Ns").BuildCodeFiles(schemas);
// files[0].Name == "../../../../tmp/evil.cs"  — verified by reading BuildSimpleType line by line above;
// class name inside the file is EvilTmp-sanitized via _nameFormatter.Format, the FILE NAME is not.
```
A regression test for a fix round with CI access: extend `CodeFileWriterTest` with a case asserting
`CreateAsync` throws (or otherwise refuses) when a `codeFile.Name` resolves, via
`Path.GetFullPath`, outside `directoryPath` — and add a
`BuildCodeFiles_SchemaNameContainsPathTraversal_DoesNotEscapeIntoTheFilename`-style test to both
`OpenApiSchemaCSharpTypeBuilderTest` and `LambdaTestToolBuilderTest` asserting the emitted
`ICodeFile.Name` never contains `..` or a path separator regardless of the input schema/topic name.

**Fix shape:** the containment check belongs in `CodeFileWriter.CreateAsync` (defense at the one
place that actually touches the filesystem, so every current and future `ICodeBuilder` is covered,
not just these two) — resolve `Path.GetFullPath(path)` and reject/throw if it doesn't start with
`Path.GetFullPath(directoryPath)`. Independently, `OpenApiSchemaCSharpTypeBuilder`/
`LambdaTestFilesBuilder` should sanitize the *file-name* component the same way they already
sanitize the *identifier* component (route both through `RemoveNonIdentifierCharacters()`-style
stripping, or at minimum reject `/`, `\`, and `..`), so a hand-authored or `SuppliedSchemaCatalog`
schema with an unusual name degrades to a safe file name instead of an unsanitized one — belt and
braces, since a future third code path could reintroduce the same gap if only the writer is fixed.

---

## Finding 2 — `ApiGatewayBuilderV1.BuildVerb` interpolates the raw HTTP path into the API Gateway VTL request-mapping template's embedded JSON body, unescaped — the one place in this file the #212/#263 fix didn't reach

**File:** `src/Benzene.CodeGen.ApiGateway/ApiGatewayBuilderV1.cs:203-210`.

This package's own `CLAUDE.md` documents that a path/topic is user-authored and must be
quote-escaped via `YamlLiteral`/`YamlValueEscaping` before being embedded — and three call sites in
this exact file do so correctly: the path mapping key (`YamlLiteral.Format("/" + path)`, line 96),
the tag (`YamlLiteral.Format(tag)`, lines 123/189), and the topic (`YamlLiteral.Format(topic)`, line
187). But `BuildVerb`'s VTL request-mapping template — the block embedded under `application/json: |`
that AWS API Gateway itself evaluates on every real HTTP request — interpolates the same `path`
value raw, straight into a JSON string literal, with no escaping at all:

```csharp
// ApiGatewayBuilderV1.cs:203-210
stringBuilder.AppendLine(@$"        uri: ""#{_options.Url}#""");
...
stringBuilder.AppendLine($@"              ""httpMethod"": ""{verb.ToUpperInvariant()}"",");
stringBuilder.AppendLine($@"              ""resource"": ""{resource}"",");
stringBuilder.AppendLine($@"              ""path"": ""/{path}"",");
```

`resource` (line 180) is derived from the same raw `path` via `TemplateParser`/string-join with no
escaping either. Because this text sits inside a YAML **literal block scalar** (`application/json:
|`), a `"` in `path` does *not* break the surrounding YAML — `openApi.yaml` still parses as valid
YAML, `benzene build --output api-gateway` still reports success, and the existing golden-file/
adversarial tests (which only assert YAML-level validity — see
`LambdaOpenApiBuilderTest.BuildPath_AdversarialPath_ColonAndQuote_KeyAndTagSafelySingleQuoted`, whose
`adversarialPath` (`"user/weird:segment"`) contains no `"` at all) never catch it. What breaks
instead is the **JSON payload of the VTL template AWS actually evaluates when a request comes in** —
a `"` in the path corrupts the JSON object structure that's built from `$input`/`$context` velocity
directives, and a crafted path (`foo"},"role":"admin` etc.) can inject arbitrary additional keys
into that JSON before it's escaped again by API Gateway's templating — a request-mapping-template
injection that's invisible at generation time (no build failure) and only manifests once the
extended OpenAPI doc is deployed to API Gateway.

**Concrete failure scenario:** a handler's `[HttpEndpoint]` route, or (per the same externally-sourced-
spec reasoning as Finding 1) a `benzene build --output api-gateway --url ...`-fetched spec's
`HttpMappings.Path`, contains a `"`. The generated `openApi.yaml` deploys cleanly (still valid YAML),
but the `x-amazon-apigateway-integration` request-mapping template for that route now contains
malformed/attacker-extendable JSON at request-mapping time — a deploy-time-invisible defect in a
security-relevant artifact (the same template that also sets `Access-Control-Allow-*` and other
security headers a few lines away).

**Proof (manual trace):** `BuildVerb("GET", "user/{id}\"x", "user:get")` — `path =
"user/{id}\"x"` is never passed through `YamlLiteral`/`YamlValueEscaping` before landing at line 210,
so the emitted line is literally:
```
              "path": "/user/{id}"x",
```
— unbalanced/invalid JSON inside the VTL body, embedded in otherwise-valid YAML.

**Fix shape:** escape `path` (and the derived `resource`) for embedding in a JSON string the same
way `YamlValueEscaping.EscapeForDoubleQuoted` already does for `_options.AllowedHeaders` two lines
below (`\`/`"` escaped) before interpolating into `"path"`/`"resource"`/anywhere else raw `path`
reaches a JSON string literal in this method. Add a
`BuildVerb_PathContainingAQuote_ProducesValidEmbeddedJson`-style test that parses the extracted
`application/json: |` block's content as JSON (not just asserts the outer YAML parses) to close the
exact coverage gap that let this one call site go unnoticed while its siblings were fixed.

---

## Finding 3 — `OpenApiSchemaCSharpTypeBuilder.BuildEnumType` can emit two identically-named enum members, failing to compile

**File:** `src/Benzene.CodeGen.Client/OpenApiSchemaCSharpTypeBuilder.cs:120-162`.

```csharp
private ICodeFile BuildEnumType(string name, OpenApiSchema schema)
{
    ...
    foreach (var entry in schema.Enum)
    {
        lineWriter.WriteLine($"{FormatEnumMember(entry, isStringEnum)},", 2);
    }
    ...
}

private string FormatEnumMember(IOpenApiAny entry, bool isStringEnum)
{
    if (isStringEnum)
    {
        var value = OpenApiAnyConverter.ToPlainValue(entry) as string ?? entry.ToString() ?? string.Empty;
        return _nameFormatter.Format(value);   // CSharpNameFormatter: strip, then Pascalcase — NOT injective
    }
    ...
}
```

`schema.Enum` is walked with a plain `foreach` and no de-duplication; `CSharpNameFormatter.Format`
(`.RemoveNonIdentifierCharacters().Pascalcase()`) only **uppercases the first character** of the
sanitized value (`CodeGenHelpers.Pascalcase`, `CodeGenHelpers.cs:30-35`) — it does not otherwise
change case, and C# enum member names are case-sensitive. Two distinct, entirely legitimate enum
values that differ only in the case of their first letter format to the **same** identifier and are
emitted as two enum members with the identical name, which doesn't compile (`CS0102: The type
'X' already contains a definition for 'Foo'`).

This is reachable with no adversarial/hand-authored input at all — it's a property of `Pascalcase`
being non-injective, hit by an entirely ordinary, valid C# enum: `SchemaBuilder`/Swashbuckle derives
`schema.Enum` from the CLR enum's own member names (as the wire strings, when
`[JsonConverter(typeof(JsonStringEnumConverter))]` is applied — the exact case this generator's own
comment at line 41 says it exists to support), and C# permits two members differing only by case:

```csharp
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Status { foo, Foo }   // perfectly legal, distinct C# enum members
```

`schema.Enum` then carries `["foo", "Foo"]` (as `OpenApiString`s). Tracing `FormatEnumMember` for
each:
- `"foo"` → `EnsureStartsWithLetterOrUnderScore()` no-op → `RemoveNonIdentifierCharacters()` no-op →
  `Pascalcase()` → `"Foo"`.
- `"Foo"` → same pipeline, already starts uppercase → `Pascalcase()` → `"Foo"`.

Both produce `"Foo"`. `BuildEnumType` emits:
```csharp
public enum Status
{
    Foo,
    Foo,
}
```
which fails to compile (`CS0102`). The same collision is reachable, more easily, for a hand-authored/
`SuppliedSchemaCatalog`/`JsonOpenApiSchemaBuilder`-sourced enum with values like `["Draft", "draft"]`
or `["N/A", "NA"]` — nothing upstream validates enum-value uniqueness-after-formatting. An integer
enum has the analogous exposure: two members sharing an underlying numeric value (`enum Status {
Active = 1, Enabled = 1 }`, also perfectly legal C#) produce two `Value1 = 1` members via the other
branch of `FormatEnumMember`, the same `CS0102`.

The failure mode matches the pattern round 16 (Finding 1) already fixed once in this exact method
for discriminator strings: codegen reports success, and the generated client SDK's `.cs` files
simply don't compile — surfaced only when the consumer tries to build against them, with an error
that gives no hint the root cause is an enum-value-casing collision three layers upstream.
`CodegenOutputCompilesTest.cs` (the dedicated Roslyn-compile test for this generator, which caught
round 16's discriminator finding) has no enum-value case exercising this.

**Proof (manual trace):** as above — `FormatEnumMember("foo", true)` and `FormatEnumMember("Foo",
true)` both evaluate to `"Foo"` by direct inspection of `CSharpNameFormatter.Format` and
`CodeGenHelpers.Pascalcase`; nothing in `BuildEnumType`'s `foreach` de-duplicates before emission.

**Fix shape:** track emitted member names in `BuildEnumType` (a `HashSet<string>`) and disambiguate a
collision (e.g. append the entry's original ordinal or raw value) instead of emitting a silent
duplicate — mirroring how a real C#-identifier sanitizer conventionally handles collisions elsewhere
in the codebase. Add a `CodegenOutputCompilesTest`-style theory case with two enum values that
Pascalcase-collide (case-only difference is the cleanest, most "this needs no adversarial input at
all" repro) and assert the emitted file still compiles.

---

## Finding 4 — `JsonSchemaComparer` (the mesh aggregator's live compatibility screen) never inspects `additionalProperties`, unlike its documented behaviorally-identical sibling `SchemaCompatibilityComparer` — a breaking `Dictionary` value-type change is silently reported "Compatible"

**Files:** `src/Benzene.Schema.Compatibility/JsonSchemaComparer.cs` (the whole `Walk` method, lines
58-145 — no `additionalProperties` handling anywhere in the file), consumed by
`src/Benzene.Mesh.Aggregator/MeshAggregator.cs:575-630` (`CompareVersions`, the mesh UI's
cross-version `MeshTopicCompatibility` verdict — `Breaking`/`Warning`/`Compatible`).

`JsonSchemaComparer`'s own doc comment states the design invariant this finding breaks: *"The two
walkers are deliberately kept behaviourally identical — same traversal order, same descriptions,
same kinds — and a test asserts that over a shared corpus."* Its sibling,
`SchemaCompatibilityComparer` (`src/Benzene.Schema.OpenApi/Compatibility/SchemaCompatibilityComparer.cs`,
the CI-gate comparer `benzene diff`/`SchemaCompatibility.EnsureBackwardCompatible` use), was fixed
in an earlier round (#168, cited in `SchemaCompatibilityComparer.cs:187-199`) to recurse into
`AdditionalProperties` — a `Dictionary<string, T>`-shaped property's value schema — because without
it a breaking change to a map's value type (e.g. `Dictionary<string,string>` →
`Dictionary<string,int>`) was invisible to the comparer entirely. `JsonSchemaComparer.Walk` was
never given the equivalent fix: it reads `type`/`format`/`properties`/`required`/`items`/`oneOf`/
`anyOf`/`allOf` (confirmed by a full read of the file) and nowhere reads `additionalProperties`.

This isn't a cosmetic difference — the two walkers feed genuinely different products, and
`JsonSchemaComparer`'s consumer (`MeshAggregator.CompareVersions`) is exactly the kind of live,
non-CI-gated, user-facing "is this a breaking change?" signal the whole package exists for: it drives
the Mesh UI's per-topic cross-version `Breaking`/`Warning`/`Compatible` badge shown to a human
deciding whether it's safe for a consumer to stay on an older version of a topic.

**Confirmed reachable with a real `additionalProperties` schema, not a hypothetical:**
`Benzene.Mesh.Aggregator`'s own `InlineSchema` (`MeshAggregator.cs:1348-1401`) explicitly recurses
into and preserves `additionalProperties` when building the `JsonObject` it hands to
`JsonSchemaComparer.Compare`:
```csharp
// MeshAggregator.cs:1399-1401
case "additionalProperties" when property.Value.ValueKind == JsonValueKind.Object:
    result["additionalProperties"] = InlineSchema(property.Value, components, visiting, depth + 1);
    break;
```
So the exact shape `JsonSchemaComparer` is asked to compare routinely carries an
`additionalProperties` key — the walker just never looks at it.

**Concrete failure scenario, traced against `JsonSchemaComparer.Walk` line by line:**
baseline = `{"type":"object","additionalProperties":{"type":"string"}}` (a
`Dictionary<string,string>`-shaped topic field, v1), current = `{"type":"object",
"additionalProperties":{"type":"integer"}}` (the same field changed to `Dictionary<string,int>`, v2
— a genuinely breaking wire change: an old consumer parsing the new payload's map values as strings
fails). `Walk`:
1. `Str(baseline,"type") == Str(current,"type")` — both `"object"`, equal; `format` both null,
   equal → no `TypeChanged`, continues.
2. `baselineProps`/`currentProps` (`Obj(schema,"properties")`) — neither schema has a `"properties"`
   key at all (it's a map schema) → both empty dictionaries → the three `foreach` loops over
   properties (add/remove/became-required/became-optional, lines 81-124) iterate zero times.
3. `baseline["items"]`/`current["items"]` — neither is a `JsonObject` (not an array schema) → the
   `items` branch (lines 126-140) is skipped entirely.
4. `CompareUnionMembers`/`CompareAllOfMembers` for `oneOf`/`anyOf`/`allOf` — none present on either
   side → each returns immediately (lines 163-166/216-219).

`Walk` returns with **zero changes recorded** for a field whose map value type changed from `string`
to `integer` — `MeshAggregator.CompareVersions` (`Worst(changes)`, line 632-642) then reports
`MeshCompatibilityVerdict.Compatible` for a topic version pair that is not compatible. The CI-gate
`SchemaCompatibilityComparer` for the identical schema shape would report this as `TypeChanged`
(via its own `AdditionalProperties` recursion, `SchemaCompatibilityComparer.cs:192-199`) —
confirming this is a genuine divergence between the two walkers, not a shared, deliberate limitation.
(The class's doc comment does disclaim ignoring `minimum`/`pattern`/etc. — but does not list
`additionalProperties` among them, and the sibling walker demonstrably does handle it.)

**Why nothing caught this:** `JsonSchemaComparerTest.cs`'s "shared equivalence corpus" cross-checks
(`EquivalenceCorpus`, and the individual tests that construct a `SchemaCompatibilityComparer` report
alongside a `JsonSchemaComparer` one for comparison — confirmed by grep, dozens of call sites) never
construct an `OpenApiSchema`/JSON pair with `AdditionalProperties`/`additionalProperties` set — a
`grep -i "dictionary\|additionalProp"` over the whole test file turns up only unrelated
`Dictionary<string, OpenApiSchema>` catalogue-construction noise, no actual map-schema test case.

**Fix shape:** add an `additionalProperties` branch to `JsonSchemaComparer.Walk`, mirroring
`SchemaCompatibilityComparer.cs:187-199`'s treatment: when both sides have an
`additionalProperties` object, recurse into it (`Walk(baseline["additionalProperties"] as
JsonObject, current["additionalProperties"] as JsonObject, ...)`); when only one side has it, record
a `TypeChanged` (the map appeared/disappeared, same treatment the `items` branch already gives an
array's element schema at lines 134-139 — a nearly identical existing pattern to copy). Add a shared-
corpus test case (a `Dictionary<string,T>`-shaped property, value type changed) so the parity
assertion the file's own doc comment promises actually holds for this keyword, closing the same gap
`items`/`oneOf`/`anyOf`/`allOf` already don't have.

---

## Areas checked and found solid (no findings)

- **`Benzene.CodeGen.SourceGenerators/MessageHandlerSourceGenerator.cs`**: every user-authored value
  (`topic`, `version`) reaching generated C# source is escaped via
  `SymbolDisplay.FormatLiteral(value, quote: true)` (lines 363-364) before interpolation — confirmed
  correct and the origin of the pattern round 16 found missing elsewhere. `BENZ001`-`BENZ004`
  diagnostics traced through `Initialize`/`Execute`/`ReportValidationDiagnostics`; incremental-cache
  value-equality (`MessageHandlerInfo`/`UnroutedHttpEndpointInfo`) is consistent with the fields
  actually compared. No bug found.
- **`CodeGenHelpers.ToCSharpStringLiteral`**: re-verified against round 16 Finding 1's fix — handles
  `\`, `"`, and every control character (including the ones a naive escaper misses: `\0`/`\a`/`\b`/
  `\f`/`\v`, plus a generic `\uXXXX` fallback for any other control character) correctly. Used
  consistently at every C#-string-literal interpolation site now (`OpenApiSchemaCSharpTypeBuilder`
  discriminator property name/mapping keys, `MessageClientSdkBuilder`'s topic literal and
  `RequiredTopics` array, `MessageHandlerBuilder`'s `[Message(...)]` attribute argument) — this is a
  genuinely closed gap now, not just partially patched.
- **`Benzene.CodeGen.Terraform`/`HclLiteral`, `Benzene.CodeGen.ApiGateway`/`YamlLiteral`**: re-read
  both against their own adversarial tests (`HclLiteralTest.cs`, `YamlLiteralTest.cs`) — the `${`/
  `%{` HCL live-interpolation neutralization and the YAML single-quote-doubling are both correct for
  every call site *except* the one gap in Finding 2 above (which isn't a `YamlLiteral`/
  `YamlValueEscaping` correctness bug — it's a call site that never routes through either helper).
- **`Benzene.SchemaRegistry.Core`**: re-confirmed round 17's characterization — `ConfluentWireFormat`
  round-trips correctly (magic byte, big-endian 4-byte id, `HeaderLength = 5`, too-short/wrong-magic
  both throw cleanly), `InMemorySchemaRegistryClient` id/version assignment and
  `TextualSchemaCompatibilityChecker`'s documented "textual identity only" limitation are exactly as
  described in the package's own `CLAUDE.md` and not silently oversold anywhere. No bug found.
- **`Benzene.CloudService`/`Benzene.CloudService.Probe`**: re-confirmed round 17's characterization of
  `CloudServiceProbe`'s tri-state honesty rule and the reachable-vs-non-conformant distinction (both
  hold on re-read); additionally traced `CloudServiceProfileCheckCommand`'s `WriteJson`/`WriteText`
  paths (not covered by name in round 17) — both serialize a well-typed `CloudServiceProbeReport` via
  `JsonConvert.SerializeObject`, never raw-string interpolation, so there's no analogue of round 17's
  `healthcheck` `WriteJson` crash here. No bug found.
- **`Benzene.Descriptor`**: `ResolveOutputPaths`' `--output`/`--emit both` derivation
  (`.service.json` → `.spec.json` swap) traced for both the suffix-present and suffix-absent branches
  — correct in both. `--output` is always an operator-supplied local CLI flag here, not a value
  derived from a fetched spec, so this package doesn't share Finding 1's threat model.
- **`templates/`**: read every `.template.config/template.json` (12 templates) and their `.csproj`
  files — `shortName`s are unique, `sourceName`/`preferNameDirectory`/the `IncludeTests` symbol and
  its `BenzeneStarter.Tests/**` exclude modifier are consistent and match the actual on-disk
  `BenzeneStarter.Tests/` folder in every template that has one; package versions/`TargetFramework`
  are consistent per-transport-family. No inconsistency found that would break a fresh `dotnet new`
  scaffold (could not actually run `dotnet new`/`dotnet build` in this environment — no SDK
  available — so this is a static-consistency check only, not a build verification).
- **`Benzene.CodeGen.Build`**, **`Benzene.CodeGen.Markdown`** (re-read after round 16's Finding 3 fix
  — the `additionalProperties`/empty-object property-name-labelling fix holds up): no new issues
  found beyond what's already fixed and documented in their own `CLAUDE.md`s.

No other bugs meeting this codebase's bar (genuine correctness bug / crash / silent data corruption /
security-relevant defect) turned up in the remaining surface of this territory.
