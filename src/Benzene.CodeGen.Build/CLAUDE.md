# Benzene.CodeGen.Build

The MSBuild one-liner for client generation (implementation plan Phase 6/6b,
`work/archive/spec-mesh-tooling-implementation-plan-2026-08.md`): a consumer team commits the producer's
`{service}.spec.json` (Phase 1's `Benzene.Descriptor` output, or `benzene spec` piped to a file) into
their own repo, adds one `<BenzeneServiceContract>` item, and gets a typed client compiled into their
project on every build — no manual `benzene build` invocation, no checked-in generated `.cs` files.

## Shape

- **Targets-only NuGet** (`IncludeBuildOutput=false`, no assembly): `build/Benzene.CodeGen.Build.targets`
  is the whole package. No `.props` — the `BenzeneServiceContract` item type needs no separate
  declaration; MSBuild items don't require pre-registration.
- **Item**: `<BenzeneServiceContract Include="contracts/orders.spec.json" Mode="topic-client"
  ServiceName="Orders" Namespace="Contracts.Orders" Topics="order:create,order:cancel" />`.
  `Mode` defaults to `topic-client`; `ServiceName` defaults to the file's stem; `Namespace`/`Topics`
  are optional and map 1:1 onto the `benzene build` CLI's `-namespace`/`-topics` flags. `Mode`'s valid
  values (`client`, `topic-client`, `message-handlers`, `readme`) must stay in sync with
  `Benzene.CodeGen.Cli.Core`'s `CodeBuilderFactory.ValidOutputs` — this package doesn't validate them
  itself; an unrecognized value is the CLI's own error, surfaced through `Exec` and failing the build.
- **Two targets, split on purpose**:
  - `BenzeneGenerateClients` (`BeforeTargets="CoreCompile"`) shells out to `$(BenzeneCliCommand) build`
    per contract, with `Inputs`/`Outputs` keyed off a per-contract stamp file
    (`$(IntermediateOutputPath)benzene/{ServiceName}/.generated.stamp`) so an unchanged contract's
    regeneration is skipped on the next build — MSBuild's ordinary incremental-build machinery, not
    anything bespoke.
  - `BenzeneAddGeneratedClientSources` (also `BeforeTargets="CoreCompile"`, `DependsOnTargets` the
    first) globs `$(IntermediateOutputPath)benzene/**/*.cs` into `Compile`, unconditionally, on
    **every** build. It has no `Inputs`/`Outputs` of its own on purpose: if it lived inside
    `BenzeneGenerateClients`, a build where generation gets skipped as up-to-date would run none of
    that target's child elements either, and a previous build's generated sources would silently stop
    being compiled.
- **`$(BenzeneCliCommand)`**, default `benzene`: override to run the tool another way (`dotnet tool
  run benzene`, or — this repo's own examples' pattern, see `examples/Directory.Build.props` — build
  and run it from source when the packaged tool can't be assumed to be on `PATH` yet, the same
  cross-job-CI problem `Benzene.Descriptor.targets` solved first in Phase 6a).

## Two MSBuild traps this package works around

- **Property-evaluation order**: nothing that depends on `$(IntermediateOutputPath)` (or any other
  SDK-computed path property) lives in a top-level `PropertyGroup` — only inside the `Target`'s own
  body/`Inputs`/`Outputs`, which evaluate after every import (including the SDK's own) has run. A
  top-level capture would silently see an empty value. Same trap `Benzene.Descriptor.targets` hit
  first for `$(TargetDir)`.
- **Per-item optional arguments must be item metadata, not `$(Properties)`**: `_NamespaceArg`/
  `_TopicsArg` are computed via an `<ItemGroup>` `Update` inside the target, not a conditionally-set
  `$(Property)`. A `$(Property)` set from `%(metadata)` is not batch-scoped — MSBuild batches each
  metadata-referencing element (a `PropertyGroup`, an `Exec`, …) *independently*, so a `PropertyGroup`
  finishes its own full pass over every contract before a later `Exec`'s pass even starts, and the
  property is left holding whichever contract's value was set *last* — leaking one contract's
  `-namespace` onto every other contract's command line. Per-item metadata doesn't have this problem:
  it travels with the item itself, correctly scoped no matter which task reads it later.

## Fixed alongside this (a genuine, previously-unverified bug)

Building this package's own example was the first time any generated client's code actually got
compiled end-to-end in this repo (`Benzene.CodeGen.Client`'s tests only ever asserted generated
*strings*, never compiled them) — and it didn't compile: `MessageClientSdkBuilder`'s health-check
method referenced a `NullPayload` type that doesn't exist anywhere in the codebase, and neither
`BuildClass` nor `BuildInterface` emitted a `using Benzene.Abstractions.Results;` that `IBenzeneResult<>`
needs. Fixed in `src/Benzene.CodeGen.Client/MessageClientSdkBuilder.cs` (now `Benzene.Abstractions.Results.Void`,
fully qualified — a bare `Void` is ambiguous with `System.Void` whenever the topic's own schema
doesn't already contribute a same-namespace `Void` DTO); the golden-file tests under
`test/Benzene.Core.Test/Autogen/CodeGen/Client/Examples/*.txt` were updated to match. This affects
every consumer of `MessageClientSdkBuilder`/`AtomicClientSdkBuilder`, not just this package.

## Not in scope

- No validation of `Mode` values here — that's the CLI's job (`CodeBuilderFactory`), so the two lists
  can't drift by one of them forgetting to update.
- No content diffing on the stamp file — its content is meaningless, only its existence/mtime, which
  is all MSBuild's `Inputs`/`Outputs` comparison uses. A contract edited to have byte-identical
  content but a new mtime still regenerates; that's normal MSBuild incrementality, same as any other
  build.
- `Inputs` is the contract *file* only, exactly as designed (see the plan). Changing an item's
  `Mode`/`ServiceName`/`Namespace`/`Topics` attribute in the csproj without also touching the
  contract file does **not** trigger regeneration on its own — the stamp is still fresh, so the
  target is (correctly, by its own Inputs/Outputs contract) skipped. A `dotnet clean` or any edit to
  the contract file itself forces it. This mirrors ordinary MSBuild incrementality elsewhere (e.g.
  changing a `<Compile>` item's own attributes without touching the source file doesn't reliably
  invalidate `CoreCompile` either) rather than being unique to this package.
