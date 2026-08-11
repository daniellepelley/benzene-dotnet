# Slice 3 — Discovery as a separate deployable

**Status:** ready to build.
**Depends on:** slice 1 (the `registryDocuments` key joins the schema slice 1 establishes).
**Branch:** `claude/mesh-enterprise-slice-3`

## Why, and the product position you are implementing

An enterprise customer may want the mesh to discover services automatically — and may equally not,
because software that can enumerate a cloud account is a finding in a security review. The position
this slice implements, taken by the mesh product owner:

> **The default is an explicit, hand-written service list, and the vanilla host is *physically
> incapable* of enumerating a cloud account — not "off by default"; absent.**

The distinction is the whole point. In a security review, "the flag is off" invites the question
"who can turn it on?". "The image contains no code path that calls `ListFunctions`, and its role
needs no list permissions" ends the conversation.

So discovery becomes a **separate deployable** running under its own least-privilege role, which
emits an inspectable registry document. The vanilla host reads that document like any other config.
Discovery proposes; config disposes — and the proposal can be reviewed, diffed, or gated through a
pull request before the mesh consumes it.

This is not a new architecture. `work/mesh-self-discovery-design.md` already decided that discovery
creates config and the aggregator consumes it. This slice packages that seam.

## What exists today

- `IMeshDiscoveryProvider` (`src/Benzene.Mesh.Contracts/`) with three implementations:
  `AwsLambdaDiscoveryProvider` (paginated `ListFunctions` + per-function `ListTags`),
  `AzureAppServiceDiscoveryProvider`, `KubernetesServiceDiscoveryProvider`.
- `MeshDiscoveryRunner` — unions providers with an optional static seed; **the seed and earlier
  providers win on name clash**.
- `MeshDiscoveryFilter` — default tag key `"benzene"`, optional `Regions`, `Namespace`.
- `MeshRegistryJson.Serialize` / `.Deserialize`.

Three facts that shape this slice:

1. The AWS/Azure/K8s example hosts run discovery **in-process**, replacing an empty registry at
   runtime. That is the pattern being replaced, not extended.
2. `MeshDiscoveryFilter` is **never configurable** — every caller does `new MeshDiscoveryFilter()`.
3. `MeshRegistryJson.Deserialize` has **no production caller**. Discovery writes `registry.json` and
   nothing ever reads it back. Closing that loop is task 3.1.

## Before you start

Read [`README.md`](README.md) here, `work/mesh-self-discovery-design.md`, and your merged slice 1
branch. Confirm green:

```bash
dotnet build Benzene.sln
dotnet build deploy/Mesh/Benzene.Mesh.Host.sln
dotnet test  deploy/Mesh/Benzene.Mesh.Host.sln
```

## Tasks

### 3.1 — `registryDocuments`: the host reads a discovered list

**Files:** `deploy/Mesh/Benzene.Mesh.Host/MeshHostConfig.cs`, `Startup.cs` (modify).

Add `"registryDocuments": [ ... ]` — a list of locations, each resolved through the **configured
artifact store** from slice 1, so a document written to S3 by the discovery job is read from S3 by
the host with no new credential path. Reuse `IMeshArtifactStore.TryReadAsync`.

Union them with `services` from config. **`services` wins on name clash** — that is the "config
disposes" half of the position, and it means an operator can always override a discovered entry by
naming it explicitly. It also matches `MeshDiscoveryRunner`'s existing seed-wins precedence, so the
two behave the same way.

Deserialize with the existing `MeshRegistryJson.Deserialize`. Do not write a second parser.

**Failure behaviour:** a missing or unparseable document must **not** crash the host, and must not
silently vanish either. Log it loudly and continue with what could be read — the estate keeps
working on the last-known list. But if `registryDocuments` is non-empty and **none** could be read,
that is worth failing on: an operator who configured discovery and got an empty dashboard needs to
be told why.

**Verify:** union works; `services` wins a clash; one bad document among several degrades and logs;
all bad fails.

### 3.2 — The discovery deployable

**Files:** new `deploy/Discovery/Benzene.Mesh.Discovery.Host/` — mirror
`deploy/Mesh/Benzene.Mesh.Host`'s layout exactly: `.csproj`, `Program.cs`, its own config class,
`Dockerfile`, `CLAUDE.md`, and its own `.sln` under `deploy/Discovery/`.

It is a **job, not a server**: run discovery once, write the registry document to the configured
artifact store, exit. Non-zero exit on failure so a scheduler notices. No web host, no poll loop —
a long-running process holding cloud-enumeration permissions is exactly what this design avoids.

Config: which providers to run (by name — `awsLambda`, `azureAppService`, `kubernetes`), the
filter (tag key, regions, namespace — task 3.3), the artifact store, and the output path. Reuse
slice 1's mirror-POCO-then-construct approach and its fail-fast-on-unknown-name rule; do not invent
a second configuration style.

Add `deploy/Discovery/Benzene.Mesh.Discovery.Host.sln` plus `build-discovery-host.yml` and
`deploy-discovery-host.yml`, copying the shape of the existing `build-mesh-host.yml` /
`deploy-mesh-host.yml` — including manual `workflow_dispatch` for the publish. Do not add an
automatic publish; nothing else in this repo has one.

The README must state the least-privilege role: for AWS, `lambda:ListFunctions` and
`lambda:ListTags` — and **not** `lambda:InvokeFunction`, which the discovery job does not need. The
separation of roles is the security argument; write it down.

### 3.3 — Make the discovery filter configurable

**Files:** the new host's config class.

`MeshDiscoveryFilter` supports a tag key, regions and a namespace, and no caller has ever set them.
Surface all three. The default tag key stays `"benzene"`.

This matters more than it looks: an estate where the mesh should see only part of the account is the
normal enterprise case, and the tag filter is the mechanism. Untunable, it is close to useless there.

### 3.4 — The invariant test

**Files:** `deploy/Mesh/Benzene.Mesh.Host.Test/NoDiscoveryInVanillaHostTest.cs` (new).

Assert that `Benzene.Mesh.Host` has **no** transitive dependency on any
`Benzene.Mesh.Discovery.*` assembly. Walk the loaded/referenced assemblies of the host and fail if
any name starts with `Benzene.Mesh.Discovery`.

This is the cheapest task in the slice and the most valuable. It converts a product position into
something CI enforces: the day someone adds a discovery reference "just to make one thing easier",
the build fails and they have to make the argument out loud. Without it, the invariant survives
exactly as long as everyone remembers it.

Give the test a comment saying *why* it exists — the security posture, not the mechanics. A future
reader who does not know the argument will otherwise delete it as pedantry.

### 3.5 — Documentation

**Files:** `deploy/Discovery/README.md` (new), `deploy/Mesh/README.md` (modify).

Document the two-deployable model and, more importantly, the *reason* for it: the vanilla host
cannot enumerate a cloud account, discovery runs separately under a narrower role, and its output is
a reviewable artifact. Include the review/PR-gating workflow as a recommended pattern — that is the
enterprise selling point, not an afterthought.

Cross-reference from the mesh host's README so an operator finds it.

## Definition of done

- [ ] All build and test commands green; the compose smoke test still passes.
- [ ] `registryDocuments` unions with `services`, `services` wins clashes, proven by tests.
- [ ] Partial read failure degrades and logs; total failure fails loudly.
- [ ] The discovery job runs once, writes the document, exits non-zero on failure.
- [ ] Tag key, regions and namespace are all configurable.
- [ ] **The invariant test fails if a `Benzene.Mesh.Discovery.*` reference is added to the host.**
      Verify by temporarily adding one and watching it fail — then remove it.
- [ ] Both READMEs explain the model and the least-privilege roles.

## Do NOT

- **Do not add a discovery reference to `Benzene.Mesh.Host`.** That is the entire point of the slice.
  If a task appears to need it, you have misread it — stop and report.
- Do not add discovery to the existing mesh image "as an option". Two deployables is the design.
- Do not make the discovery job long-running or give it an HTTP surface.
- Do not have the host write registry documents. It reads them; the job writes them.
- Do not put cloud credentials in config.

## Report back with

The invariant test and proof that it fails when a discovery reference is added; the IAM permissions
you documented for each provider; and confirmation that `MeshRegistryJson.Deserialize` now has a
production caller.
