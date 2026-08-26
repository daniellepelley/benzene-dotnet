# Benzene.Azure.Function.SourceGenerators

## What this package does
A Roslyn incremental source generator that emits the Azure Functions `[Function]`/`[…Trigger]`
boilerplate class per transport from a user-authored **assembly-attribute declaration**. It does
**not** ship as its own NuGet package (`IsPackable=false`): it's a separate project only because
analyzers must target `netstandard2.0`, and its built DLL is **packed into
`Benzene.Azure.Function.Core`** (`analyzers/dotnet/cs`). Core is the universal Azure Functions
dependency, so referencing any `Benzene.Azure.Function.*` package brings the generator (and the
`FunctionsEnableWorkerIndexing=false` prop it needs) automatically — no separate reference. The developer declares *what* triggers they want and their
bindings (route, queue, hub, container, schedule, …); the generator writes the ceremony that forwards
each trigger invocation into the built `IAzureFunctionApp`. Mirrors `Benzene.CodeGen.SourceGenerators`'
packaging. Design: `work/archive/azure-functions-trigger-codegen-design-2026-08.md`.

## Key types
- `AzureFunctionTriggerGenerator` (`[Generator]`, `IIncrementalGenerator`) - registers one
  `ForAttributeWithMetadataName` **per transport**, and one `RegisterSourceOutput` **per transport**
  (restored in WP-C, #38 - see `work/archive/bug-fix-designs-round7-10-2026-08.md`; a prior round had merged
  every transport into one shared output, which both re-emitted every trigger class on any single
  trigger edit and was a build-crash vector). The `BENZ0001` name-collision check stays a single
  *global* view (`Combine`d into every transport's output) so a name shared *across* transports is
  still caught even though emission itself is per-transport, using an explicit content-aware
  `IEqualityComparer` on each `Collect()` result (`ImmutableArray<T>`'s own equality compares by
  reference, not content - relying on the default would silently defeat per-transport registration).
  Emits one class per declared trigger into namespace `Benzene.Azure.Function.Generated`, fully
  qualified (`global::`) so the generated file needs no `using`s.
- `TriggerInfo` - the value-equatable emit model (class name, function-name literal, full parameter
  list incl. the binding attribute, return type, dispatch expression, an optional *blocking*
  `PendingDiagnosticInfo` - the instance is diagnostic-only, nothing is emitted - and optional
  *advisory* `PendingDiagnosticInfo`s reported alongside a normal emission). Value equality (which
  excludes `Location`) drives the incremental cache; see `Location`'s doc comment for why this is safe
  (an *external* Location, carrying no `SyntaxTree` reference - see `AttributeReading` below - has
  nothing to go stale across an incremental boundary, unlike the tree-bound one WP-C, #38 crashed on).
- `Transports/Http.cs` and `Transports/MessagingTransports.cs` - one reader per transport (HTTP,
  Service Bus, Event Hubs, Kafka, Queue Storage, Blob Storage, Event Grid, Cosmos DB, Timer). Each
  turns its `Benzene*TriggerAttribute` into a `TriggerInfo`, validating `Name` first
  (`AttributeReading.ValidateName` - `BENZ0008`) then its own required field(s) (`BENZ0002`-`BENZ0007`)
  before building a binding.
- `AttributeReading` - helpers for safe, fully-qualified emission (escaped literals, named
  string/bool/enum/type/array args, identifier sanitization, the shared `Name` validation above).
  `AttributeLocation` deliberately builds an *external* `Location` (file path + span, no `SyntaxTree`)
  rather than returning `SyntaxNode.GetLocation()` directly - see WP-C, #38.
- `DiagnosticDescriptors` - `BENZ0001` (cross-transport Function-name collision) through `BENZ0009`
  (see that file's doc comments for the full table).

## The two hard Azure constraints (both load-bearing)
1. **Worker indexing must be off.** The Functions SDK's worker-indexing source generators (metadata +
   executor) can't see another generator's output, so the generated `[Function]` would land in the
   host's `functions.metadata` (post-compile reflection, which *does* see it) but not the worker's
   generated executor. `Benzene.Azure.Function.Core` ships `FunctionsEnableWorkerIndexing=false` via
   `buildTransitive` so the reflection metadata + executor (which see generated code) are used end to
   end. Repo examples (ProjectReference) set it in their csproj.
2. **Extension package stays a direct reference.** The transport's
   `Microsoft.Azure.Functions.Worker.Extensions.*` package must be referenced directly by the app - a
   Functions tooling requirement the generator can't remove (it only removes the trigger *class*).

## The declaration attributes
One `Benzene*TriggerAttribute` lives in each transport's package (assembly-scoped, `AllowMultiple`),
e.g. `BenzeneHttpTriggerAttribute` in `Benzene.Azure.Function.AspNet`. Plain attributes with no
Functions-type coupling except HTTP's `AuthorizationLevel`. The generator matches them by metadata
name; a named-property default declared on the attribute is re-applied in the reader (it doesn't
surface in `AttributeData` unless explicitly set).

## When to use this package
- Referenced (as an analyzer) by an Azure Functions app that wants declared triggers instead of
  hand-written `[Function]` classes. Inert if no `Benzene*Trigger` attribute is declared.

## Testing
`test/Benzene.Core.Test/Autogen/AzureFunctions/`:
- `AzureFunctionTriggerGeneratorTest.cs` drives the generator with `CSharpGeneratorDriver` over stub
  attributes and asserts the emit per transport, plus every `DiagnosticDescriptors` case.
- `CrashReproTest.cs` reproduces WP-C/#38's build crash directly (two independently-constructed
  `CSharpCompilation`s sharing one driver; a genuine single-tree incremental edit via
  `SyntaxTree.WithChangedText`/`Compilation.ReplaceSyntaxTree`) - keep these green if the diagnostic
  reporting path is ever touched again.
- `IncrementalityTest.cs` proves editing one transport's declaration doesn't re-invoke another
  transport's `RegisterSourceOutput` (reference-equality of the untouched transport's generated
  `SyntaxTree` across an incremental edit).

End-to-end proof is `functions.metadata` from building `examples/Azure/Benzene.Example.Azure` (which
declares its HTTP/Queue/Service Bus triggers).

## Important conventions
- netstandard2.0, `IsRoslynComponent`, `IncludeBuildOutput=false` - a generator, not a runtime library.
- Keep readers' binding/dispatch strings verified against `docs/azure-functions.md` and the target
  `Benzene.Azure.Function.*` package's `HandleX` signature.
