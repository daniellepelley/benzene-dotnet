# Benzene Cloud Service Profile Check (GitHub Action)

Runs [`benzene profile-check`](https://www.nuget.org/packages/Benzene.CodeGen.Cli) — the
external, black-box live-probe checker for the
[Cloud Service Profile](https://github.com/daniellepelley/Benzene/blob/main/docs/specification/cloud-service-profile.md)
(`Benzene.CloudService.Probe`) — against a deployed service, as a CI/CD conformance gate.

Because the probe audits over plain HTTP rather than by inspecting source, this works against a
service written in **any** conforming language port (.NET, Go, TypeScript, Python) — the profile
is language-neutral, and so is the checker.

## ⚠️ Prerequisite

This action installs `Benzene.CodeGen.Cli` from NuGet and runs `profile-check --format json
--fail-on ...`. Those two flags were added to the CLI in
[`daniellepelley/benzene-dotnet#c6a53df`](https://github.com/daniellepelley/benzene-dotnet/commit/c6a53df)
and **are not yet in a published NuGet release** as of this action's initial commit (the latest
published version at that time was `0.0.2.18-alpha`, built before the flags existed). Until a new
version is published (`workflow_dispatch` on `benzene-dotnet`'s release workflow — a manual,
maintainer-triggered step, deliberately not run as a side effect of this commit), you must:

- pin `cli-version` to a version you've confirmed supports these flags, or
- publish a new prerelease first and leave `cli-version` unset to pick up "latest prerelease".

Running this action against an old CLI version will fail at the "Install benzene CLI" or
"Run profile-check" step with an unrecognized-argument error — a loud, early failure, not a
silent false pass.

## Usage

```yaml
- name: Check Cloud Service Profile conformance
  uses: daniellepelley/benzene-dotnet/tools/profile-check-action@main
  with:
    url: https://orders.example.com
    cli-version: '0.1.0-alpha.1'   # pin once a version with --fail-on/--format is published
```

### Inputs

| Input | Required | Default | Description |
|---|---|---|---|
| `url` | yes | — | Base URL of the Benzene Cloud Service to probe |
| `fail-on` | no | `not-satisfied` | `not-satisfied`, `inconclusive`, or `none` — see below |
| `cli-version` | no | latest prerelease | Pin to a specific `Benzene.CodeGen.Cli` version |
| `invoke-path` | no | `/benzene/invoke` | Override the envelope endpoint path (R4, and R5's `benzene:spec` topic) |
| `spec-path` | no | `/benzene/spec` | Override the R5 derived-spec HTTP path |
| `health-path` | no | `/benzene/health` | Override the R3 health endpoint path |
| `no-traceparent-probe` | no | `false` | Skip the M2 bonus `traceparent` header on the envelope calls |

Passing any of `invoke-path`/`spec-path`/`health-path` means the probe can no longer confirm the
service's *own* defaults, so R7 degrades to `Inconclusive` in that run — this is the checker's own
documented behavior, not a bug in this action.

### `fail-on`: read this before setting it to `inconclusive`

The Cloud Service Profile is R1–R5 and R7, and every one of them is observable over HTTP: a
conformant service probed at its default paths comes back fully `Satisfied`, so it passes even
`fail-on: inconclusive`. What can still be `Inconclusive` is R7 when you override a path input (see
above) and R2 when the contract document lists no requests.

The **meshed-service claim** (mesh.md §7.1) — M1, registration/heartbeat/trace delivery to a
collector, and M2, trace-context propagation (formerly the profile's R6 and R8) — is **structurally
unobservable** by a single-service HTTP probe. The report carries it apart (`MeshRequirements`,
always `Inconclusive`, with the reason), and it never counts toward `fail-on` or the outputs below.

### Outputs

| Output | Description |
|---|---|
| `verdict` | `pass` or `fail`, per the resolved `fail-on` threshold |
| `not-satisfied` | JSON array of profile requirement ids observed as unmet (e.g. `["R3","R5"]`) |
| `inconclusive` | JSON array of profile requirement ids the probe could not determine |
| `report-json` | The full report — every R1–R5/R7 requirement, plus the M1/M2 meshed-service claim, with verdict and reason — as JSON |

A markdown table (one row per profile requirement, a summary line, and a second table for the meshed-service claim) is written to the job's step
summary (`$GITHUB_STEP_SUMMARY`) on every run, pass or fail.

## Example: post-deploy gate

```yaml
jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - name: Deploy
        run: ./deploy.sh   # however your service ships

      - name: Verify Cloud Service Profile conformance
        uses: daniellepelley/benzene-dotnet/tools/profile-check-action@main
        with:
          url: https://orders.example.com
          cli-version: '0.1.0-alpha.1'
```

## What this action does *not* do

- It does not implement any probe logic itself — that lives entirely in
  `Benzene.CloudService.Probe` / the `benzene` CLI, which this action only installs and invokes.
  Fixing or extending the probe's behavior is a `benzene-dotnet` change, not an action change.
- It does not call `/benzene/invoke` in any capacity beyond the checker's own read-only R4/R5
  probes (no dispatch, no side effects on the target service).
- It cannot make the meshed-service claim (M1/M2) observable — no CI wrapper can; that is a property of a single-service
  black-box HTTP probe, documented in the
  [Cloud Service Profile spec §5](https://github.com/daniellepelley/Benzene/blob/main/docs/specification/cloud-service-profile.md#5-conformance-testing).

## See also

- [`docs/specification/cloud-service-profile.md`](https://github.com/daniellepelley/Benzene/blob/main/docs/specification/cloud-service-profile.md) — the spec this checker audits against
- [`work/third-party-tool-integrations-plan.md`](https://github.com/daniellepelley/Benzene/blob/main/work/third-party-tool-integrations-plan.md) (WP3) — the plan this action implements
