# Scope: bringing a real, externally-deployed service into the AwsMesh demo (2026-08-31)

## The ask
Dogfood the mesh on a real service with real traffic — specifically an admin function ("wet sticks
admin", a separate Benzene example the requester runs out of a different repo) — instead of the
mesh only ever showing its own three-to-six demo Lambdas. Two shapes were floated:
1. Open discovery up so it pulls in *everything* except the mesh's own Lambda.
2. An admin-managed allowlist of extra services, if we'd rather not go fully open.

This doc scopes both, explains why they're not actually mutually exclusive with the codebase as it
stands, and recommends one to implement first. **No code changed as part of this pass** — this is
the brief for whoever implements it next.

## How discovery works today
- `AwsLambdaDiscoveryProvider` (`src/Benzene.Mesh.Discovery.Aws/AwsLambdaDiscoveryProvider.cs`)
  paginates `lambda:ListFunctions` across the **whole account**, reads each function's tags
  (`lambda:ListTags`), and keeps only the ones matching a `MeshDiscoveryFilter`
  (`src/Benzene.Mesh.Contracts/MeshDiscoveryFilter.cs`) — by default "carries a `benzene` tag key,
  any value" (`MeshDiscoveryFilter.DefaultTagKey`).
- `MeshAggregateHandler.HandleAsync` (`examples/AwsMesh/Mesh/MeshAggregateHandler.cs:36`) is the one
  call site: `await _discovery.DiscoverAsync(new MeshDiscoveryFilter())` — default filter, **no
  static seed**. This is where either option below actually plugs in.
- `MeshDiscoveryRunner.DiscoverAsync` (`src/Benzene.Mesh.Contracts/MeshDiscoveryRunner.cs`) already
  accepts an optional `staticSeed` registry that is unioned with whatever the provider(s) discover,
  **winning on a name clash** — this plumbing exists today and is unused by the example.
- Terraform (`examples/AwsMesh/deploy/main.tf`):
  - Each demo service Lambda is tagged `{ benzene = "true" }` (`discovery_tag_key` var, default
    `"benzene"`); the mesh Lambda itself is deliberately left untagged so it never discovers itself.
  - The mesh's IAM role has `lambda:ListFunctions`/`lambda:ListTags` on `resources = ["*"]` already —
    **discovery itself is already account-wide**, not tag-scoped at the AWS API level; the tag
    filter is applied client-side, after the scan.
  - But `lambda:InvokeFunction` (needed to actually interrogate/dispatch a discovered service) is
    scoped to a **hardcoded list** of the six demo services' ARNs (`main.tf:238`). So even a same-
    account, correctly-tagged 7th Lambda is discoverable today but not interrogable until its ARN is
    added to this policy by hand — the "known list" the requester is running into is really here,
    not in the discovery filter.
- What a discovered target must expose either way: `UseBenzeneCloudService(...)` (spec/health/invoke
  over the Cloud Service Profile). Two transports already exist to reach it —
  `LambdaMeshServiceSource` (same-account `Invoke`) or `HttpMeshServiceSource`
  (`src/Benzene.Mesh.Aggregator/HttpMeshServiceSource.cs`, plain `GET specUrl`/`GET healthUrl` — no
  AWS coupling at all).

## The one fact that decides the shape: same AWS account, or not?
"Running it out of another repo" doesn't tell us whether it's in the *same* AWS account as this
Terraform stack. That matters a lot:
- `lambda:ListFunctions`/`ListTags` **cannot cross accounts** without the mesh assuming a role in the
  target account (`sts:AssumeRole`) — `work/archive/mesh-self-discovery-design-2026-07.md` already
  flagged multi-account as out of scope for v1 and left an assume-role hook as a future extension
  point, not something built.
- If the admin function is HTTP-reachable (a Function URL, API Gateway, or any public/authenticated
  endpoint answering `/benzene/spec` + `/benzene/health`), **account doesn't matter at all** — it's
  just a URL to `HttpMeshServiceSource`.
Confirm this before implementation starts; it doesn't block writing the code (Option B below works
either way), but it does decide whether Option A is even on the table.

## Option A — open discovery to "everything except the mesh itself"
Drop (or make configurable) the tag requirement in `MeshDiscoveryFilter`, and change the mesh's IAM
`lambda:InvokeFunction` statement from the hardcoded ARN list to a wildcard (or a
`tag:GetResources`-conditioned wildcard) so anything discovered is also interrogable.
- **Only works same-account** — see above.
- **Real security/blast-radius tradeoff**: the mesh Lambda's role would gain `InvokeFunction` on
  every Lambda in the account, Benzene or not — including whatever the admin function's own downstream
  calls happen to be, and anything unrelated running in that account. A misbehaving or compromised
  mesh dispatch (`Benzene.Mesh.Dispatch`, already gated but real) suddenly has a much bigger reach.
  This is the point that most needs a maintainer sign-off before an implementing agent goes near IAM.
- **Noise**: "everything in the account" will also surface non-Benzene Lambdas (CI runners, other
  infra) that don't answer the Cloud Service Profile at all — the aggregator's per-service
  fetch-isolation means they degrade to an unhealthy/unreachable row rather than crashing the pass,
  but it's still catalog noise nobody asked for. A tag-based *exclude* list (or requiring the target
  to at least answer `/benzene/spec`) would need to replace the current *include* tag to keep the
  catalog meaningful.
- Net: viable, but it's an IAM-widening change with real blast-radius, only solves same-account, and
  needs a deliberate decision on what "everything" is filtered down to before it's useful.

## Option B — admin-managed allowlist (recommended starting point)
Wire the `staticSeed` parameter `MeshDiscoveryRunner.DiscoverAsync` already supports:
1. Add a small config surface to `MeshAggregateHandler` (or `Startup.cs`) — e.g. an
   `MESH_EXTRA_SERVICES` env var (JSON array of `{name, specUrl, healthUrl}`, or one entry per
   `MESH_EXTRA_SERVICE_<N>_*` var, matching the existing env-var convention in `Startup.cs`) —
   parsed into a `MeshServiceRegistry` and passed as `staticSeed` to `DiscoverAsync`.
2. Point one entry at the admin function's `/benzene/spec` and `/benzene/health` (via
   `HttpMeshServiceSource`, already wired for HTTP sources — confirm `AddMeshHttpSource()`-equivalent
   registration exists/add it if the example doesn't already register an HTTP source).
3. The admin function's own repo needs nothing mesh-specific beyond already running
   `UseBenzeneCloudService(...)` and exposing those two endpoints on a URL the mesh Lambda can reach
   (public, or behind whatever auth scheme `HttpMeshServiceSource` supports — check before assuming
   unauthenticated).
- **Works regardless of account/repo** — no IAM change, no assume-role, no wildcard invoke grant.
- **No blast-radius change** — the mesh's AWS permissions are untouched; it only ever reaches the one
  URL an admin explicitly configured, same trust model as any other seed entry today (seed already
  wins over discovery on a name clash, so this can't be silently overridden by a same-name discovered
  Lambda either).
- Directly matches "possibly have an admin way of adding extra services" from the ask, and is
  additive to whatever Option A ends up being — they aren't exclusive.
- Smallest real gap: nothing today builds a `MeshServiceRegistry` from config/env for this seed; that
  parsing + a doc note in `examples/AwsMesh/README.md`/`CLAUDE.md` is the actual net-new work, on top
  of plumbing that already exists.

## Suggested order of work
1. Confirm same-account vs. separate-account for the admin function (see above) — decides whether
   Option A is worth scoping further at all.
2. Implement Option B first regardless: smallest change, no IAM risk, works today. This alone gets
   the admin function onto the mesh.
3. If "discover everything, no allowlist" is still wanted after that, treat Option A as its own
   follow-up with an explicit maintainer decision on the IAM widening and the noise-filtering
   question above — don't fold it into the same change as Option B.
