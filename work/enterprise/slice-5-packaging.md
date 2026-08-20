# Slice 5 — Packaging polish

**Status:** **SHIPPED** (verified against source 2026-08-20) — `deploy/Mesh/helm/benzene-mesh/`, `deploy/Mesh/CONFIG.md`, and the redacted
startup summary in `MeshConfigSummary.cs` (covered by `MeshConfigSummaryTest`).
**Depends on:** slice 1 (there is nothing to document until the config catalog exists) and slice 2
(the Helm chart and the config reference both have to cover `auth`).
**Branch:** `claude/mesh-enterprise-slice-5`

## Why

Slices 0–3 make the host capable. This one makes it *takeable*: a platform engineer should be able
to find it, run it, and read what every key in their config file does without opening a `.cs` file.

Two things already exist and must not be rebuilt:

- `deploy/Mesh/Benzene.Mesh.Host/Dockerfile` — a working multi-stage build (context = repo root).
- `.github/workflows/deploy-mesh-host.yml` — a manual `workflow_dispatch` that publishes
  `ghcr.io/<owner>/benzene-mesh:{sha,latest}` to GHCR.

So "publish the container image" is **done**. What is missing is everything around it.

## Before you start

Read, in this order:

1. [`README.md`](README.md) in this folder — the house rules apply to every task below.
2. `deploy/Mesh/README.md` — the current operator-facing documentation.
3. `deploy/Mesh/Benzene.Mesh.Host/CLAUDE.md` — the package guide; it must end this slice accurate.
4. Your merged slice 1 and slice 2 branches — this slice documents what they built.

Confirm green before you touch anything:

```bash
dotnet build deploy/Mesh/Benzene.Mesh.Host.sln
dotnet test  deploy/Mesh/Benzene.Mesh.Host.sln
```

## Tasks

### 5.1 — Effective-config printing

**Files:** `deploy/Mesh/Benzene.Mesh.Host/Program.cs` (modify), plus a new small type if it helps.

**Do:** on startup, log the configuration the host actually resolved — every source it registered,
with its options — at `Information`, once, before the first poll. This is the single highest-value
support tool in the whole slice: "it isn't picking up my Tempo URL" is otherwise unanswerable
without a debugger.

**Redaction is mandatory and is the whole difficulty.** Per the house rules, secrets do not live in
`mesh.json` — but options *values* may still carry a token someone pasted in against advice, and
connection strings sometimes embed credentials. Redact by key: any key whose name contains
`password`, `secret`, `token`, `key`, `credential`, or `connectionstring` (case-insensitive) prints
as `***`. Print the key, never drop it — an operator needs to see that a value was supplied.

**Verify:** a test asserting that a config containing an option named `apiKey` prints the key name
and does not print its value. `dotnet test deploy/Mesh/Benzene.Mesh.Host.sln`

**Done when:** starting the host with the compose sample logs a readable summary, and the redaction
test passes.

### 5.2 — The config reference document

**File:** `deploy/Mesh/CONFIG.md` (new). Link it from `deploy/Mesh/README.md`.

**Do:** one table per config section (`artifactStore`, `services`, `registryDocuments`, `usage`,
`fleet`, `topology`, `dispatch`, `auth`), each row being: key, type, default, required-when, and
one sentence on what it does. Then a worked example per deployment shape — at minimum: filesystem +
HTTP services (the compose case), and S3 + Lambda-invoked services + X-Ray + CloudWatch (the AWS
case).

Include the **per-source least-privilege permission matrix** — which IAM/RBAC permissions each
source name requires. Slice 1 puts a first version of this in the host README; this document is
where it belongs permanently. Moving it is part of this task, not duplicating it.

State the two invariants explicitly, because a config reference is where an operator looks for them:
credentials never go in `mesh.json`; unknown source names fail at startup rather than defaulting.

**Verify:** every key in the document exists in the config classes, and every public config property
appears in the document. Check by reading `MeshHostConfig.cs` against your table — there is no
automated check, so do it deliberately.

**Done when:** someone could write a working `mesh.json` for either worked example without reading
any C#.

### 5.3 — A `dotnet tool` distribution

**Files:** `deploy/Mesh/Benzene.Mesh.Host/Benzene.Mesh.Host.csproj` (modify).

**Do:** add `PackAsTool`, a `ToolCommandName` of `benzene-mesh`, and `PackageOutputPath`, so the
host installs with `dotnet tool install -g Benzene.Mesh.Host`. Keep the existing independent
`VersionPrefix`/`VersionSuffix` line — this artifact versions separately from the library, and that
is deliberate.

**Check the precedent first:** `templates/Benzene.Templates.csproj` is the repo's existing example of
an independently-versioned, independently-packed artifact. Match how it declares packaging metadata
rather than inventing a second style.

**Verify:**

```bash
dotnet pack deploy/Mesh/Benzene.Mesh.Host/Benzene.Mesh.Host.csproj -c Release
```

then install the produced `.nupkg` from a local feed and run `benzene-mesh --validate-config`
against the compose sample (`examples/K8sMesh/compose/mesh.json`).

**Done when:** the tool installs and `--validate-config` (built in slice 1) works through it.

### 5.4 — Publish the tool from CI

**File:** `.github/workflows/deploy-mesh-host.yml` (modify).

**Do:** add a job that packs and pushes the tool package alongside the existing image push. Match
the trigger and auth pattern the workflow already uses — manual `workflow_dispatch`, built-in
`GITHUB_TOKEN`. Do not add an automatic publish on push; every other deploy workflow in this repo is
manual and this one should stay consistent with them.

**Verify:** the workflow file parses and the job is gated the same way the image job is. Do not run
a real publish to validate.

### 5.5 — A Helm chart

**Files:** `deploy/Mesh/helm/benzene-mesh/` (new) — `Chart.yaml`, `values.yaml`, and templates for
`Deployment`, `Service`, `ConfigMap`, and optionally `Ingress`.

**Do:** the `ConfigMap` carries `mesh.json`; the `Deployment` mounts it at `/config/mesh.json` and
sets `MESH_CONFIG_PATH`. Model the volume and env wiring on
`examples/K8sMesh/compose/docker-compose.yml`'s `mesh` service, which already does exactly this.

**Secrets are the part to get right.** Auth client secrets and any source credentials come from a
Kubernetes `Secret` referenced by `envFrom`/`secretKeyRef` — **never** templated into the ConfigMap.
`values.yaml` should carry the *name* of an existing secret, not the secret. Say so in a comment.

**Verify:**

```bash
helm lint deploy/Mesh/helm/benzene-mesh
helm template deploy/Mesh/helm/benzene-mesh
```

and read the rendered output to confirm no secret value appears in the ConfigMap.

**Done when:** `helm template` renders a Deployment that would start the published image with a
mounted config, and the chart is documented in `deploy/Mesh/README.md`.

### 5.6 — Make the package guides true again

**Files:** `deploy/Mesh/README.md`, `deploy/Mesh/Benzene.Mesh.Host/CLAUDE.md` (both modify).

**Do:** these were written when the host did aggregation + UI and nothing else. After slices 1–3
they describe a different product. Bring both up to date: the source catalog, auth, discovery
documents, the tool, the chart.

`CLAUDE.md` carries a "Deviations from the original design sketch" section listing **"No Tempo
wiring"** as a deliberate omission. Slice 1 closes that. Remove the entry rather than leaving a
resolved deviation on the page — a stale caveat is worse than none, because the next reader plans
around it.

**Done when:** neither document describes a limitation that no longer exists.

## Definition of done

- [x] `dotnet build` and `dotnet test` green on `deploy/Mesh/Benzene.Mesh.Host.sln`.
- [x] Startup logs the effective configuration, with secret-shaped keys redacted and a test proving it.
- [x] `deploy/Mesh/CONFIG.md` documents every config key and both worked examples.
- [x] `dotnet pack` produces an installable tool whose `--validate-config` works.
- [x] `helm lint` and `helm template` pass, with no secret in the rendered ConfigMap.
- [x] `README.md` and `CLAUDE.md` describe the host as it now is; the "No Tempo wiring" deviation is gone.

## Do NOT touch

- `Dockerfile` and the image-publish job — they work. You are adding beside them.
- Anything under `src/`. This slice is packaging and documentation only; if you find yourself
  editing a library, you have misread a task.
- The library's `version.txt` / main versioning. This artifact versions independently and that is a
  decision already made.

## Report back with

The commands you ran and their output for `helm template` and `dotnet pack`, plus a note of anything
in `README.md`/`CLAUDE.md` you found stale but out of scope to fix.
