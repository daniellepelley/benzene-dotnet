# Contract Artifacts

`benzene-descriptor` is a `dotnet` tool that emits a service's contract as two build artifacts —
`{name}.spec.json` and `{name}.service.json` — from a **built, non-running, non-deployed** service.
It constructs the app in-process (runs `ConfigureServices`/`Configure`, never the run/listen step)
and reads the descriptors it already computes. No deploy, no socket, no network.

That means a service's contract can be produced, stored, diffed, and fed into code generation
**at build time**, without needing the service to be running anywhere — the "static floor" the rest
of the tooling in this section builds on.

## The two artifacts

| Artifact | Shape | Use |
| --- | --- | --- |
| `{name}.spec.json` | `EventServiceDocument` — the same JSON the [`spec` topic](spec.md) serves with `"type":"benzene"` | Codegen input ([client SDKs](client-sdks.md)), and the compatibility gate baseline ([contract testing](cookbooks/contract-testing.md)) |
| `{name}.service.json` | The mesh **§2 `ServiceDescriptor`** wire shape, exactly as `benzene:mesh:register` sends it | Drop-in for the mesh artifact store — indistinguishable from a live-fetched snapshot, so a build can seed or refresh the mesh without deploying first |

Both are read directly off the service's real, code-derived registration — handlers, HTTP routes,
FluentValidation-enriched schemas, wired transports — not re-derived from source. The mesh
descriptor's shape is pinned by the language-neutral spec
(`docs/specification/mesh.md` §2 in the [Benzene repo](https://github.com/daniellepelley/Benzene))
and covered by its conformance fixtures, so this build artifact and a live `benzene:mesh:register`
call are the same contract by construction.

> An older, more opinionated projection (`consumes`/`produces`/`transportKind` per topic) existed as
> a spike output; it is deferred pending the outbound-routing read-model that would make its
> `transportKind` field reliable rather than best-effort reflection. It is not part of the shipped
> tool.

## Install

```bash
dotnet tool install -g benzene-descriptor
```

Or as a local tool (pinned per-repo, restored via `dotnet tool restore`):

```bash
dotnet new tool-manifest   # once, if the repo has no .config/dotnet-tools.json yet
dotnet tool install benzene-descriptor
```

## Run it directly

```bash
benzene-descriptor --assembly path/to/YourService.dll --service your-service --service-version 1.0.0
```

With no `--output`, both files are written next to the assembly, named after it
(`YourService.spec.json` / `YourService.service.json`). Options:

| Flag | Meaning |
| --- | --- |
| `--assembly <dll>` | **Required.** The built service assembly. |
| `--emit spec\|descriptor\|both` | Which artifact(s) to produce. Default `both`. |
| `--output <path>` | Override the output location. For a single `--emit`, the exact file path. For `--emit both`, the *descriptor* path — the spec path is derived from it (`.service.json` → `.spec.json`, else `.spec.json` appended). Omit for both files next to the assembly. |
| `--service <name>` | Service name (defaults to the assembly's simple name). |
| `--service-version <v>` | Service version, carried into the mesh descriptor. |
| `--cloud <aws>` / `--region <r>` | Placement metadata for the mesh descriptor. |
| `--host <neutral\|aws-lambda>` | Force a host adapter (auto-selected by default — see below). |
| `--startup <fullTypeName>` | Pick the `BenzeneStartUp` type explicitly, needed only when the assembly defines more than one candidate. |

Exit codes: `0` on success, `2` on a bad/unparseable argument, `1` on any failure during
construction (assembly not found, no `BenzeneStartUp` found, a Benzene version mismatch between the
tool and the service, or any exception the service's own registration throws) — always with a
one-line reason on stderr.

### Host adapters

Everything except the *inbound* transport list is cloud-agnostic (comes from host-neutral
`ConfigureServices`). An AWS Lambda host adapter additionally runs the host-specific `Configure` to
populate the inbound transport list and validation-enriched schemas; other hosts fall back to the
neutral core. See `src/Benzene.Descriptor/README.md` for the full mechanism.

## MSBuild opt-in

Import `build/Benzene.Descriptor.targets` (packed into the tool's package — copy it into the repo,
or explicitly `<Import Project="...">` it from wherever the package restores to) and opt in per
project:

```xml
<PropertyGroup>
  <BenzeneEmitDescriptor>true</BenzeneEmitDescriptor>
</PropertyGroup>
```

This runs `benzene-descriptor --emit both` after every `Build`, writing
`$(TargetDir)$(AssemblyName).spec.json` / `.service.json`. A failing emit **fails the build** — a
service that cannot produce its own contract should not report green. Useful overrides:
`BenzeneDescriptorCommand` (default `benzene-descriptor`; e.g. `dotnet tool run benzene-descriptor`
for a local-tool install), `BenzeneDescriptorEmit`, `BenzeneDescriptorOutput`,
`BenzeneDescriptorServiceName`, `BenzeneDescriptorStartup`.

A NuGet tool package does not auto-import its `.targets` (that only happens for `PackageReference`
libraries) — either copy the file into the repo, or `<Import>` it explicitly from the restored
package path.

**See it in action:** `examples/AwsMesh/Payments` in this repo opts in exactly this way — its csproj
sets `BenzeneEmitDescriptor` and imports the targets file from source, and
`examples/Directory.Build.props` supplies the `BenzeneDescriptorCommand` override every example
under `examples/` inherits (a `dotnet run --project` form, since this repo's CI builds
`Benzene.Descriptor` and the examples in separate jobs with no shared `PATH`). Copy that pattern for
a new service.

## CI: upload the artifacts

```yaml
- name: Emit contract artifacts
  run: benzene-descriptor --assembly bin/Release/net10.0/YourService.dll --service your-service --service-version ${{ github.sha }}
- uses: actions/upload-artifact@v4
  with:
    name: your-service-contract
    path: |
      bin/Release/net10.0/YourService.spec.json
      bin/Release/net10.0/YourService.service.json
```

Feed the uploaded `spec.json` into the `benzene` CLI's `diff` command as a compatibility gate (see
[contract testing](cookbooks/contract-testing.md)), or the `service.json` into a mesh artifact store
to seed/refresh it without a live deploy.

## What the compatibility comparer detects

`SchemaCompatibilityComparer` (OpenAPI-backed) and `JsonSchemaComparer` (`JsonNode`-backed, used where
a component must classify a change without an OpenAPI toolchain dependency — e.g. the mesh aggregator)
walk a `spec.json` pair field by field and classify every difference as `SchemaChangeKind`:

| `SchemaChangeKind` | Meaning |
| --- | --- |
| `TopicAdded` / `TopicRemoved` | A topic exists on only one side. |
| `PropertyAdded` / `RequiredPropertyAdded` | A property exists on only the current side (required or not). |
| `PropertyRemoved` | A property exists on only the baseline side. |
| `PropertyBecameRequired` / `PropertyBecameOptional` | A property's `required` membership changed. |
| `TypeChanged` | A schema's `type`/`format` changed — also used for an `items` null-asymmetry (below). |
| `UnionVariantAdded` | A `oneOf`/`anyOf`/`allOf` member exists on only the current side. |
| `UnionVariantRemoved` | A `oneOf`/`anyOf`/`allOf` member exists on only the baseline side. |
| `UnionVariantChanged` | A member matched between baseline and current, but the matched pair's schemas differ. The specific differences are reported immediately after this entry (their own kinds, from a recursive comparison of the matched pair). |

**Matching a `oneOf`/`anyOf`/`allOf` member across baseline and current** (so a reordered or
renumbered union doesn't manufacture spurious adds/removes) uses this priority, per member:

1. The discriminator mapping value, when the owning schema declares a `discriminator` — matches by the
   discriminator's semantic name (e.g. `"dog"`), independent of position.
2. Else the member's `$ref` target name (e.g. `"Dog"` from `#/components/schemas/Dog`).
3. Else its position in the array.

`allOf` matches its `$ref` members by target name and its inline members by position among the inline
members (the two pools are matched independently).

An `items` schema present on only one side (while both sides agree on `type: array`) is treated as a
`TypeChanged`, not silently skipped — the array's element contract appeared or disappeared entirely.

### Breaking-direction table

Classification is direction-aware — see [contract testing](cookbooks/contract-testing.md) for the
full default table including the older kinds. The rows added for union-aware comparison:

| Change | Request schema (what the service accepts) | Response schema (what consumers receive) |
|---|---|---|
| Union variant **removed** | **Breaking** (callers still sending it are rejected) | Non-breaking |
| Union variant **added** | Non-breaking | **Breaking** (consumers meet an unknown variant) |
| `Items` appears/disappears on one side | Breaking (as type change) | Breaking (as type change) |

This table is reproduced verbatim from the design ruling
(`work/bug-fix-designs-2026-08.md` §"WP-9 — Schema compatibility: union-aware walkers") and applies
identically to `oneOf`/`anyOf` variants and to `allOf` members (both use the `UnionVariantAdded`/
`UnionVariantRemoved` kinds — there is no separate `allOf`-specific kind). `UnionVariantChanged` itself
carries no direction-specific rule; its compatibility falls to the rules table's default (`Warning`) —
the concrete impact of a changed variant is carried by the nested changes reported alongside it, which
are independently classified by their own kind.

## The version-pinning caveat

`benzene-descriptor` loads the service assembly into a plugin `AssemblyLoadContext` that **prefers
its own already-loaded `Benzene.*` assemblies** over the service's copies — that is what keeps type
identity intact across the boundary (e.g. the service's `StartUp` stays assignable to the tool's
`BenzeneStartUp`). The consequence: pin the tool version to the service's Benzene version. The tool
detects a `Benzene.Core` version mismatch between what the service was compiled against and what the
tool carries, and fails loudly (printing both versions) rather than silently running the service's
registration against an API surface it wasn't built for.

## Related

- [`docs/cookbooks/contract-testing.md`](cookbooks/contract-testing.md) — using `spec.json` as a
  compatibility gate baseline
- [`docs/client-sdks.md`](client-sdks.md) — generating typed clients from a spec artifact
- `src/Benzene.Descriptor/README.md` — implementation detail: the ALC plugin mechanism, host
  adapters, and why this needs a *built* assembly rather than source analysis
  (`work/deployment-descriptor-design.md` has the full rationale)

A `benzene` CLI reference page (install, every command including a `diff` verb over these
artifacts) is planned but not yet part of this repo's docs tree.
