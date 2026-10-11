# Benzene.Descriptor

A `dotnet` tool (`benzene-descriptor`) that emits a service's **contract document** —
`{name}.spec.json` — at **build time** from a **built but non-running, non-deployed** Benzene
service, by constructing it in-process and deriving the document it already computes. No deploy, no
socket, no AWS. See [`work/deployment-descriptor-design.md`](../../work/deployment-descriptor-design.md)
for the design and rationale, and [`docs/contract-artifacts.md`](../../docs/contract-artifacts.md) for
the consumer-facing guide.

> **Status:** sln-approved (implementation plan Phase 1,
> [`work/archive/spec-mesh-tooling-implementation-plan-2026-08.md`](../../work/archive/spec-mesh-tooling-implementation-plan-2026-08.md)),
> packed and published like any other `src/` package — no longer a spike. The introspection is
> **cloud-agnostic** (see below); an **AWS Lambda** host adapter additionally supplies the inbound
> transport-name list.

## One artifact, wire-exact

**`{name}.spec.json`** is the Contract Document (`SpecBuilder`'s `"benzene"` type): the same JSON
the [`benzene:spec` topic](../../docs/spec.md) serves, the input of codegen and the compatibility
gate, and — with the deployment-side fields — the body a meshed service registers with a collector
(`benzene:mesh:register`, `docs/specification/mesh.md` §2 in the
[Benzene repo](https://github.com/daniellepelley/Benzene)). One builder for all three.

> Until spec-0.4.0 the tool also emitted `{name}.service.json`, the mesh `ServiceDescriptor`. The
> descriptor is retired — the register body is the contract document — so `--emit descriptor` and
> `--emit both` now fail with a message saying so; `--emit spec` (the default) still works.

> An earlier, more opinionated distilled shape (`descriptorVersion`, `consumes[]`/`produces[]` with
> a per-topic `transportKind`) was this tool's original spike output. It is **deferred**, not
> shipped: its most IaC-relevant field rests on `OutboundRouteInspector`'s best-effort
> private-field reflection into the built outbound routing table (kept in-tree, unused, for when a
> proper outbound read-model lands — see the design note).

## Cloud-agnostic by design

Everything in `spec.json` except the *inbound* transport-name list comes from
host-neutral `ConfigureServices` — so the same service (which in Benzene can target multiple clouds
from one codebase) yields the same logical contract regardless of host:

- **Cloud-agnostic core** (`NeutralHostAdapter`, works for any host): service identity, consumed
  topics + HTTP routes + schemas (`requests[]`), published events (`events[]`) and send declarations
  (`sends[]`) with their payload schemas. No cloud coupling.
- **Host adapter** (AWS Lambda today): runs the host-specific `Configure` so the **inbound
  transport-name list** (`spec.json`'s top-level `transports[]`) and validation-enriched schemas are
  populated. A new cloud is just a new adapter of the same shape — nothing else changes.

Force one with `--host neutral` / `--host aws-lambda`; auto-selected otherwise (AWS Lambda if the
assembly references `Benzene.Aws.Lambda.Core`).

## What it produces

Against the real `examples/AwsMesh/Payments` service (a compiled `.dll`, never deployed), with
`--service-version 1.0.0 --version-scheme semver --cloud aws --region eu-west-1`:

```jsonc
{
  "schemaDialect": "openapi-3.0",
  "service": "payments-api",
  "serviceVersion": "1.0.0",
  "versionScheme": "semver",
  "runtime": "dotnet",
  "placement": { "cloud": "aws", "region": "eu-west-1" },
  "transports": [ "api-gateway", "benzene", "sqs", "sns", "eventbridge" ],
  "contractHash": "sha256:…",
  "registrationHash": "sha256:…",
  "requests": [
    { "topic": "payments:capture", "httpMappings": [ { "method": "POST", "path": "/payments" } ],
      "request": { "$ref": "#/components/schemas/CapturePayment" },
      "response": { "$ref": "#/components/schemas/PaymentDto" } },
    { "topic": "payments:get-all", "httpMappings": [ { "method": "GET", "path": "/payments" } ], ... }
  ],
  "events": [
    { "topic": "payment:captured", "message": { "$ref": "#/components/schemas/OutboundPaymentCaptured" } }
  ],
  "sends": [
    { "topic": "shipping:book", "request": { "$ref": "#/components/schemas/OutboundShipmentBook" } }
  ],
  "components": { "schemas": { "...": "..." } }
}
```

`payment:captured` is an event payments-api defines; `shipping:book` is a one-way send to a topic
shipping-api defines (contract-document.md §3). A component-name collision or a topic declared both
ways fails the emit, and so the build that runs it.

`service` is the service's own name (its `IApplicationInfo`) unless `--service` overrides it, the
assembly name when it sets none. `serviceVersion` and `versionScheme` are the flags' (#171: the scheme
used to be validated and then dropped). `runtime` is always `dotnet`; `placement` is written only when
`--cloud` is given (`region` only with `--region`) — a build does not know where it will run unless
told. None of these is in either hash.

## How it works

1. Loads the built service assembly in a plugin `AssemblyLoadContext` (`ServiceLoadContext`) that
   defers Benzene/Microsoft/System contract assemblies to the tool's own copies (keeping type identity)
   and loads the service's unique transports/deps from its output folder.
2. Compares the `Benzene.Core` version the service was compiled against to the tool's own — a
   mismatch fails loudly rather than silently running the service's registration against an API
   surface it wasn't built for (see the version-pinning caveat below).
3. Selects a host adapter (`HostAdapters`): the AWS Lambda adapter if the service references
   `Benzene.Aws.Lambda.Core`, else the cloud-agnostic `NeutralHostAdapter`. The adapter runs the
   service's registration (`ConfigureServices`, plus host-specific `Configure` for AWS) **without** the
   run/listen step. Network-free.
4. Runs `SpecBuilder` directly against the built container and stamps the identity and placement
   flags on the document.

`DescriptorEmitter.Emit` is the whole core, callable in-process (no process spawn) — `Program.cs` is
a thin shell around it plus argument parsing, output-path resolution, and exit codes, so tests drive
it directly (`test/Benzene.Core.Test/Autogen/Descriptor/DescriptorEmitterTest.cs`).

## Run it directly

```bash
dotnet run --project src/Benzene.Descriptor -- \
  --assembly examples/AwsMesh/Payments/bin/Debug/net10.0/Benzene.Examples.AwsMesh.Payments.dll \
  --service payments --service-version 1.0.0 --version-scheme semver
```

With no `--output`, `Benzene.Examples.AwsMesh.Payments.spec.json` is written next to the assembly.

Options: `--assembly <dll>` (required), `--emit spec` (the default and only value), `--output <path>`
(the exact path; omit for the file next to the assembly), `--service <name>` (overrides the
document's `service`), `--service-version <v>` with `--version-scheme <integer|semver|lexicographic>`
(see below), `--cloud <aws|azure|gcp|self-hosted|…>` and `--region <r>` (the document's `placement`;
omit for none),
`--host <neutral|aws-lambda>` (force an adapter; auto-selected otherwise), `--startup <fullTypeName>`
(pick the `BenzeneStartUp` type explicitly — needed only when the assembly defines more than one
candidate).

### The version and its ordering scheme

`--service-version` is the immutable release identity for this build ([service-versions.md §1][sv]) — a build
number, a tag, a run id. It comes from the pipeline and is never derived from the contract, because
two builds can declare byte-identical contracts and still be different releases.

`--version-scheme` says how those values are **compared** ([service-versions.md §2][sv]), and is **required
whenever a version is declared**:

| Scheme | Value form | Use it for |
|---|---|---|
| `integer` | ASCII digits | a bare build counter |
| `semver` | Semantic Versioning 2.0.0 | a NuGet-style version |
| `lexicographic` | any non-empty string | a sortable string, e.g. a timestamp |

The scheme is declared rather than inferred, and there is no default. `"10"` and `"9"` order one way
as integers and the opposite way as strings, so a tool that guessed would report a rollback as an
upgrade — silently, in the surface somebody uses to decide a deployment.

**A version that does not parse under its declared scheme fails the build.** That is the point: the
build that declares a version is the cheapest place in the system to catch a mismatch, and after this
point the value travels into a catalogue, a comparison, and a screen.

Declaring no version at all remains legitimate — service-versions.md §1 case 3 gives such a service exactly one
service version, and that is not an error.

[sv]: https://github.com/daniellepelley/Benzene/blob/main/docs/specification/service-versions.md

Exit codes: `0` success; `2` bad/unparseable arguments, **including a version that does not parse
under its declared scheme** (reason printed to stderr); `1` any failure
during construction — assembly not found, no `BenzeneStartUp` found, ambiguous `BenzeneStartUp`
without `--startup`, `Benzene.Core` version mismatch, or any exception the service's own
registration throws — always with a one-line reason on stderr.

## As a build step

Install the tool, then either call it from a CI step, or import
`build/Benzene.Descriptor.targets` (packed into the tool's NuGet package) and opt in:

```xml
<PropertyGroup>
  <BenzeneEmitDescriptor>true</BenzeneEmitDescriptor>
</PropertyGroup>
```

The version forwarded is MSBuild's `$(Version)`, ordered as `semver` — which is what `$(Version)`
is, being the NuGet package version. Override `BenzeneDescriptorVersionScheme` when your pipeline
stamps it with something else (`integer` for a build counter, `lexicographic` for a sortable string),
or set it empty to declare no version at all.

That runs `benzene-descriptor` after `Build`, writing `<AssemblyName>.spec.json` next to the output — and **fails the
build** if the emit fails (no `ContinueOnError`). A NuGet tool package does not auto-import its
`.targets` (that only happens for `PackageReference` libraries), so either copy the file into the
repo or `<Import Project="...">` it explicitly from the restored package path; both patterns are
legitimate and documented in [`docs/contract-artifacts.md`](../../docs/contract-artifacts.md).

## Caveats

- **Inbound transports need a host adapter** — AWS Lambda is implemented; other hosts (self-host
  worker, ASP.NET, Azure Functions) fall back to the neutral core (full logical contract, but
  `spec.json`'s `transports: []`) until their adapter is added.
- The plugin ALC assumes the tool and the service resolve the shared `Benzene.*` assemblies to the
  **same version** — pin the tool to the service's Benzene version. The tool detects a `Benzene.Core`
  mismatch between what the service was compiled against and what it carries, and fails loudly
  (printing both versions) rather than silently coercing to its own.
- A `StartUp` that does real I/O in `ConfigureServices`/`Configure` (reads a secret, pings a DB) would
  have build-time side effects. Benzene's convention is registration-only.
- `OutboundRouteInspector` (best-effort reflection recovering each outbound topic's transport kind)
  is kept in the tree but currently unused — it backed the deferred distilled projection above and
  is the starting point when that lands as `--emit deploy`.
