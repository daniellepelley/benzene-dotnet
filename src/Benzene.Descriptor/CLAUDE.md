# Benzene.Descriptor

A `dotnet` tool (`benzene-descriptor`) that emits a service's contract artifacts —
`{name}.spec.json` (the `EventServiceDocument`) and `{name}.service.json` (the mesh §2
`ServiceDescriptor` wire shape) — from a **built, non-running, non-deployed** Benzene service, by
constructing it in-process and reading the descriptors it already computes. No deploy, no socket.

## Shape

- `Program.cs` — a thin argument-parsing/exit-code shell. Never grows real logic; that lives in
  `DescriptorEmitter`.
- `EmitOptions` — parsed CLI options, `public` (not `internal`) so tests can drive
  `DescriptorEmitter` in-process without spawning this executable.
- `DescriptorEmitter.Emit(EmitOptions)` — the core: loads the assembly, selects a host adapter, runs
  the service's own registration, and returns `DescriptorEmitResult` (either/both JSON strings).
  `ResolveOutputPaths` is the pure path-derivation logic (`--output`, `--emit`, and the "next to the
  assembly" default), kept separate so it's independently testable.
- `HostAdapters` (+`NeutralHostAdapter`/`AwsLambdaHostAdapter`) — runs `ConfigureServices` (+
  host-specific `Configure` for AWS) against the built container, never the run/listen step. A new
  cloud is a new adapter of the same shape.
- `ServiceLoadContext` — the plugin `AssemblyLoadContext`: defers Benzene/Microsoft/System contract
  assemblies to the tool's own copies (keeps type identity across the boundary — the service's
  `StartUp` stays assignable to the tool's `BenzeneStartUp`), loads the service's unique deps from
  its own output folder.
- `OutboundRouteInspector` — best-effort reflection recovering each outbound topic's transport kind.
  Currently **unused**: it backed the older distilled deployment projection (deferred, see the
  implementation plan's Amendment A), kept in-tree for when that lands as `--emit deploy`.

## Notes

- `--emit spec|descriptor|both` (default `both`). `descriptor` is the mesh §2 wire shape exactly as
  `benzene:mesh:register` sends it — not a bespoke projection. See `README.md` for real output.
- A `Benzene.Core` version mismatch between the tool and the service (detected by comparing the
  service assembly's own `AssemblyRef` metadata to the tool's loaded copy) fails loudly rather than
  silently running against an API surface the service wasn't built for — the ALC's `Load` always
  prefers the tool's own copy, so a skew would otherwise be invisible until something deep inside
  `Configure`/`ConfigureServices` throws a confusing `MissingMethodException`.
- `build/Benzene.Descriptor.targets` is packed into the NuGet package (`Pack="true"`,
  `PackagePath="build\"`) but NuGet does not auto-import a *tool* package's targets — a consumer
  copies the file in or `<Import>`s it explicitly. Document both patterns honestly; don't imply
  automatic wiring that doesn't happen.
- Tests: `test/Benzene.Core.Test/Autogen/Descriptor/DescriptorEmitterTest.cs`, driven directly against
  `DescriptorEmitter` (no process spawn) using the real, already-built
  `examples/Aws/Benzene.Examples.Aws.Minimal` assembly (a `ProjectReference` from
  `Benzene.Test.csproj` guarantees it's built alongside the test project).
