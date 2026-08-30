# Round 17 adversarial review — CLI commands, schema registry, descriptor tool, cloud-service probe

Scope: `Benzene.CodeGen.Cli`/`Cli.Core` (`spec`/`build`/`diff`/`healthcheck`/`cloud-service-profile`/
`lambda-test-tool` commands), `Benzene.SchemaRegistry.Core` (`InMemorySchemaRegistryClient`,
`SchemaRegistrySerializer`, `SchemaRegistrar`), `Benzene.Descriptor` (the `benzene-descriptor` dotnet
tool), `Benzene.CloudService.Probe`, and `Benzene.CodeGen.LambdaTestTool`. Reviewed against commit
`4389bfb` on `main`. Round 8 (#65–70) and round 9 (#93–95) previously fixed issues in this same
territory but nothing since (7+ rounds); round 16 (`work/review-round16-schema-codegen-2026-08.md`)
covered the schema/codegen escaping bug family and is not re-litigated here.

Method: read the code, then for each candidate wrote a small xUnit test in
`test/Benzene.Core.Test` (matching the existing suite's structure and using its existing test seams
— e.g. `HealthCheckCommand`'s `HealthCheckClient?` constructor overload, exactly as
`HealthCheckCommandFailOnTest` already does), ran it with `dotnet test test/Benzene.Core.Test/Benzene.Test.csproj
--filter <TestName>`, confirmed red, then deleted the test file (`git status --porcelain` is clean —
no source or test files left modified).

One finding meets this codebase's bar.

---

## Finding — `benzene healthcheck` crashes with a raw, unhandled `JsonReaderException` on a
non-JSON (or empty) health-check response body, even though the command explicitly special-cases
"a response shape this tool doesn't recognize"

**Files:**
`src/Benzene.CodeGen.Cli.Core/Commands/HealthCheck/HealthCheckCommand.cs` (lines 43–56),
`src/Benzene.CodeGen.Cli.Core/Commands/HealthCheck/Extensions.cs` (lines 8–12).

```csharp
public override async Task ExecuteAsync(HealthCheckPayload payload)
{
    var failOn = ResolveFailOn(payload);

    var client = _healthCheckClient ?? CreateClient(payload);
    var json = await client.GetHealthCheckAsync();
    Console.Out.WriteJson(json);              // <-- crashes here, before IsHealthy() ever runs

    if (Trips(json, failOn))
    {
        throw new HealthCheckFailedException(json, ...);
    }
}
```

```csharp
public static class Extensions
{
    public static void WriteJson(this TextWriter source, string json)
    {
        var output = JValue.Parse(json).ToString(Formatting.Indented);   // no try/catch
        source.WriteLine(output);
    }
}
```

`HealthCheckCommand`'s own `IsHealthy()` helper (used by `Trips`, called *after* `WriteJson`) is
explicit about its intent:

```csharp
// Absent/unparseable `isHealthy`: don't fail-loud on a response shape this tool doesn't
// recognize - only trip on an explicit `false`.
return isHealthyToken == null || isHealthyToken.Value<bool>();
```

But that tolerance is unreachable: `Console.Out.WriteJson(json)` on line 49 runs *before*
`Trips`/`IsHealthy` on line 51, and `WriteJson` calls `JValue.Parse(json)` with no exception
handling at all. Any response body that isn't valid JSON — the exact "response shape this tool
doesn't recognize" case the `IsHealthy` comment says is tolerated — throws a raw
`Newtonsoft.Json.JsonReaderException` out of `ExecuteAsync`, propagating up through
`ConsoleApplication`/`Program.cs`'s top-level catch as an unhandled crash with a Newtonsoft stack
trace, not a diagnosable CLI error.

This is a realistic, not contrived, failure mode for exactly the audiences `benzene healthcheck`
targets:
- an **empty body** — e.g. a target Lambda that answered the health-check topic but the handler
  itself (or an intermediate proxy/adapter) returned an empty string instead of a JSON object;
- a **plain-text body** — e.g. `--lambda-name` pointed at a Lambda that isn't running
  `UseHealthCheck()`'s standard shape at all (the class's own doc comment anticipates exactly this
  misconfiguration: *"is UseHealthCheck() registered and the function name/profile correct?"*), or
  a Lambda that returned an ordinary string/HTML/plain-text error body rather than the health
  contract's `{isHealthy, healthChecks}` JSON.

Both are cases the command's design already intends to treat as "an unrecognized shape, don't fail
loud" — but the crash happens one line earlier than the tolerant code path, so the intent is never
realized.

**Proof (red test, run and then deleted):** using the existing `HealthCheckCommandFailOnTest`
pattern (a fake `IAwsLambdaClient` wired through `HealthCheckCommand`'s `HealthCheckClient?` test
seam):

```csharp
var command = new HealthCheckCommand(FakeClient(""));                 // empty body
await command.ExecuteAsync(new HealthCheckPayload { LambdaName = "orders-fn" });
```
→ `Newtonsoft.Json.JsonReaderException : Error reading JToken from JsonReader. Path '', line 0, position 0.`

```csharp
var command = new HealthCheckCommand(FakeClient("Internal Server Error"));   // plain-text body
await command.ExecuteAsync(new HealthCheckPayload { LambdaName = "orders-fn" });
```
→ `Newtonsoft.Json.JsonReaderException : Error parsing Infinity value. Path '', line 1, position 2.`

Both thrown from `Extensions.WriteJson` (`Extensions.cs:10`), called from
`HealthCheckCommand.ExecuteAsync` (`HealthCheckCommand.cs:49`) — one line before the `IsHealthy`
check that was specifically designed to tolerate this input shape.

**Fix shape:** either (a) reorder `ExecuteAsync` to evaluate `Trips`/`IsHealthy` before printing, and
have `WriteJson` fall back to printing the raw string verbatim (with a `try { JValue.Parse... } catch
(JsonException) { source.WriteLine(json); }`) when it isn't valid JSON, so an operator still sees
*something* useful on stdout instead of a stack trace; or (b) make `WriteJson` itself defensive the
same way. Either way, add a regression test alongside `HealthCheckCommandFailOnTest` covering an
empty and a non-JSON body, matching the existing `ExecuteAsync_ResponseMissingIsHealthy_DoesNotThrow`
test's intent one level further (a shape this tool doesn't recognize at the JSON-object level vs. one
it doesn't recognize at the JSON-syntax level — the same tolerance policy, not currently enforced at
both levels).

---

## Areas checked and found solid (no findings)

- **`benzene diff` self-comparison** (`DiffCommand`/`SchemaCompatibilityComparer`): traced the full
  comparison — `CompareRequests`/`CompareEvents` index both sides by topic key (not by array
  position), `CompareSchemas` recurses by property/key match (not position), and union/`allOf`
  matching (`IndexVariants`/`VariantKey`) is keyed by `$ref` id / discriminator mapping / stable
  position, never by raw list order. A spec diffed against itself produces zero changes with no
  ordering-dependent noise; confirmed an existing regression test already covers this exact case
  (`SchemaCompatibilityComparerTest.IdenticalDocuments_AreCompatible_WithNoChanges`). `Example`
  fields (which can be non-deterministic in principle) are never compared at all, so they can't
  introduce spurious diff noise either.
- **`benzene spec` on a completely empty service** (zero registered handlers): traced
  `SpecBuilder.CreateBuilder` → `EventServiceDocumentBuilder`/`OpenApiDocumentBuilder`/
  `AsyncApiDocumentBuilder` and `SchemaBuilder.Build()` with an empty handler/schema catalogue —
  every builder's per-item logic is a `foreach`/`GroupBy` over the (empty) definitions arrays, and
  `EventServiceDocument.SerializeAsV3` uses `IOpenApiWriter.WriteRequiredCollection`/
  `WriteOptionalCollection`, which are designed for and handle empty collections cleanly. No crash
  path found for zero handlers, zero schemas.
- **`benzene profile-check`'s connection-refused vs. unhealthy-response distinction**: confirmed
  `CloudServiceProbe.RunAsync`'s own doc comment ("never throws for an unreachable or non-conformant
  service") holds — `GetAsync`/`PostAsync` catch every exception and set `Reached = false` with the
  raw `ex.Message` as the failure reason, which reads distinctly from a reached-but-shape-mismatched
  verdict (e.g. `"GET /benzene/health did not reach the service: Connection refused"` vs. `"200
  response ... did not have a boolean 'isHealthy' field"`). `benzene spec`'s `HttpSpecSource` has the
  equivalent distinction (unreachable vs. non-2xx status), also handled cleanly.
- **`CloudServiceProbe`'s R8 timeout handling**: R8 is not a live, separately-timed probe at all — by
  design (see the method's own comment) it is always `Inconclusive` and derives its "bonus signal"
  purely from the already-computed R4/R6 (`invoke`/`mesh`) verdicts. A timeout on the underlying R4
  POST simply makes `invoke.Requirement.Verdict != Satisfied`, which R8 already handles as "the weak
  non-breakage signal is absent" — there is no separate "R8 failed" vs. "R8 timed out" state to
  conflate, because R8 never runs its own request. No bug relative to the documented design.
- **`InMemorySchemaRegistryClient`/`SchemaRegistrySerializer` and a deleted/deprecated schema
  version**: `ISchemaRegistryClient` has no delete/deprecate operation at all in this codebase (only
  `RegisterAsync`/`GetByIdAsync`/`GetLatestAsync`/`IsCompatibleAsync`), so the "a message was produced
  under a version later deleted from the registry" scenario the brief describes cannot arise against
  this in-memory reference implementation — there's nothing to delete. `GetByIdAsync` returns a clean
  `null` for an unknown id rather than throwing. `SchemaRegistrySerializer` holds no cache at all (its
  `_schemaIds` map is a fixed, immutable snapshot resolved once at startup by design — see its own XML
  doc `<remarks>`, and confirmed already thoroughly documented, not silently stale, by #95), so there
  is no "staleness under mid-flight evolution" behavior to find beyond what #95 already covers.
- **`Benzene.Descriptor`'s handling of a service assembly with a missing dependency**: built a real
  fixture reproducing this exact scenario — a `UsesMissingLib.dll` class library whose one type
  derives from a base class in `MissingLib.dll`, with `MissingLib.dll` then deleted from the output
  folder (deps.json still references it) — and pointed `DescriptorEmitter.Emit` at it directly.
  `Assembly.GetTypes()` in `FindStartUpType` does throw `System.Reflection.ReflectionTypeLoadException`
  as expected, but on this runtime (.NET's modern `ReflectionTypeLoadException.Message` override,
  unlike classic .NET Framework's generic text) the message already concatenates the underlying
  loader failure: `"Unable to load one or more of the requested types.\nCould not load file or
  assembly 'MissingLib, Version=1.0.0.0, ...'. The system cannot find the file specified."` —
  naming the actual missing assembly. `Program.cs`'s top-level `catch (Exception ex) =>
  Console.Error.WriteLine($"benzene-descriptor: {ex.Message}")` therefore already surfaces an
  actionable message, not a raw dump or a generic "consult LoaderExceptions" message. No bug found
  here (this contradicts what was expected on first reading the code — verified empirically before
  writing it up as a finding, and it doesn't hold up).
- **`Benzene.CodeGen.LambdaTestTool` generated test-event JSON** (the fifth-instance check for the
  unescaped-interpolation bug class found in YAML/Markdown/HCL/C# generators): both
  `ExampleBuilder.BuildExample` and `HttpExampleBuilder.BuildExample`
  (`src/Benzene.CodeGen.Core/ExampleBuilder.cs`, `HttpExampleBuilder.cs` — the actual JSON-emission
  code `LambdaTestFilesBuilder`/`DefaultExampleBuilders` delegate to) serialize their payloads via
  `JsonConvert.SerializeObject(message, new JsonSerializerSettings { Formatting = Formatting.Indented })`,
  never manual string interpolation. This confirms round 16's conclusion on the same class
  (`work/review-round16-schema-codegen-2026-08.md`'s "areas checked and found solid" list) — there is
  no fifth instance of the bug class here; the Lambda Test Tool was never vulnerable to it in the
  first place because it never built JSON by hand.

No other bugs meeting this codebase's bar (genuine correctness bug / crash / silent data corruption /
spec-contract violation) turned up in this scope's remaining surface (`benzene build`'s CLI wrapper,
`ConsoleApplication`/`CommandRouter`/`Program.cs`'s top-level exception handling, `SchemaRegistrar`,
`AwsLambdaSpecSource`/`MeshSpecSource`).
