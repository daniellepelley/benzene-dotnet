# Round 18 — Mesh Core Review (2026-09-01)

**Scope, per the brief:** `Benzene.Mesh.Contracts`, `Benzene.Mesh.Aggregator`, `Benzene.Mesh.Artifacts`,
`Benzene.Mesh.Ui`, `Benzene.Mesh.Discovery.Kubernetes`, `Benzene.Mesh.Wire`, `Benzene.Mesh.Reporting`,
`Benzene.Spec.Ui` — catalog generation, UI serving, discovery. A companion agent covers mesh
dispatch/collector/tracing/auth in parallel.

**Method:** read every package's `CLAUDE.md` first, then the source, with particular attention to
`MeshSnapshotBuilder`, `MeshAggregator`'s version-compatibility/drift-diff logic, the
`AsyncApiCompositor`'s namespacing, and — per the brief's specific steer — whether any artifact's
absence/empty-array shape is indistinguishable between "genuinely nothing" and "a fetch/computation
step failed silently" (the class of bug this session already found client-side in `benzene-ui`).
Cross-checked every candidate against `work/outstanding-bugs.md` and `work/review-round17-mesh-composition-2026-08.md`
(read in full first) to avoid re-reporting. No `dotnet` in this environment — every finding below is
traced by hand through the actual production code paths; each includes the concrete regression test
that would prove it for a future round with CI access, described but not written to disk.

Four findings, all novel (none overlap round 16/17's mesh-composition findings or anything in
`work/outstanding-bugs.md`). Findings 1 and 2 are the headline results — both land squarely on the
brief's two named hunts (the "empty vs. failed" ambiguity, and a genuine `AsyncApiCompositor` collision).

---

## Finding 1 — A spec fetched successfully but structurally garbage is completely invisible: no `Error`, no `ErrorClass`, and `topics.json`/`topology.json`/`manifest.json.transports` all silently read exactly like a service that legitimately has none

**Where:** `src/Benzene.Mesh.Aggregator/MeshAggregator.cs` — `BuildServiceAsync` (~998-1022),
`FetchSpecAsync` (~1024-1039), and the three best-effort parsers `ParseTopics`/`ParseOutboundTopics`/
`ParseTransports` (~1172-1297).

**The gap.** `FetchSpecAsync` only records `Error`/`ErrorClass` when `source.FetchSpecAsync` **throws**:

```csharp
private static async Task<(string? SpecJson, string? Error, string? ErrorClass)> FetchSpecAsync(IMeshServiceSource source, MeshServiceRegistryEntry entry)
{
    try
    {
        using var timeout = new CancellationTokenSource(PerServiceFetchTimeout);
        return (await source.FetchSpecAsync(entry, timeout.Token), null, null);
    }
    catch (Exception ex)
    {
        return (null, ex.GetType().Name, ClassifyError(ex));
    }
}
```

The shipped `HttpMeshServiceSource.FetchSpecAsync` is `_httpClient.GetStringAsync(entry.SpecUrl, ...)` —
this only throws on a **non-success HTTP status**. A 200 response whose body is not the expected spec
JSON at all (an nginx/ALB maintenance page, a misconfigured `error_page` directive that returns 200, a
truncated body from a load-balancer idle-timeout mid-response, an auth portal's login HTML returned
behind a misrouted proxy) comes back as a non-null string with **no exception thrown at all** — so
`error`/`errorClass` are both `null`.

That `specJson` (garbage, but non-null) then flows into two independent places:

1. `MeshSnapshotBuilder.BuildAsync` computes `specHash = MeshHashing.ComputeHash(specJson)` — this is a
   bare HMAC-SHA256 over the raw string, it doesn't parse or validate JSON, so it succeeds on garbage
   too. The resulting `MeshServiceSnapshot.Error` stays `null` — the manifest shows this service with
   **no error at all**.
2. `BuildServiceAsync` unconditionally calls `ParseTopics(specJson)` / `ParseOutboundTopics(specJson)` /
   `ParseTransports(specJson)`. Each does its own `JsonDocument.Parse(specJson)` inside a `try/catch
   (JsonException)` that returns `Array.Empty<...>()` on failure — silently, with **no signal
   propagated back into `ServiceResult`, `MeshServiceSnapshot`, or `MeshManifestEntry` at all**:

```csharp
private static IReadOnlyList<ServiceTopic> ParseTopics(string? specJson)
{
    ...
    try { using var doc = JsonDocument.Parse(specJson); ... }
    catch (JsonException) { return Array.Empty<ServiceTopic>(); }
}
```

So a service whose spec endpoint returns 200-with-garbage ends up, in the published artifact set:
- `manifest.json`: `Status` computed purely from the (unrelated) health check — can show `Healthy`,
  `ContractDrift` computed from a meaningless hash comparison, **`Error: null`**, `Transports: []`.
- `topics.json`: contributes **zero** topic entries — identical to a service that legitimately has none.
- `topology.json`: contributes **zero** structural edges — identical to a service with no inbound/outbound topics.

There is no artifact anywhere in the published set that distinguishes "this service's spec is garbage"
from "this service genuinely publishes no topics." Contrast this with the fetch-failure path (a 5xx or
connection failure), which **is** faithfully recorded via `Error`/`ErrorClass` on the manifest — an
operator can already tell that case apart. It's specifically the "fetched fine, parsed badly" case that
falls through every net. This is the server-side sibling of the exact bug class the brief called out
from this session's earlier `benzene-ui` client-side finding (a failed fetch reads as "empty" instead
of "unreadable") — here it's not a client-side fetch failure but a spec that IS fetched, just isn't
valid, and the aggregator has no way to say so anywhere in what it publishes.

**Why it matters concretely:** an operator watching the mesh catalog for a service that quietly lost all
its topics (e.g., after a reverse-proxy config regression started serving an error page with `200 OK`)
sees a perfectly healthy-looking manifest row and a topic catalog that looks like the service was
always topic-free — nothing prompts them to look. The "gap"/"deprecation-candidate" status computation
in `DetermineTopicStatus` doesn't help either: for other services that legitimately still declare and
consume that service's topics, this now reads as a `gap` (nobody produces it) rather than what actually
happened (the producer's spec became unparseable) — actively pointing the investigation in the wrong
direction.

**Regression test that would prove this (not written, per the read-only brief):** register an
`IMeshServiceSource` fake whose `FetchSpecAsync` returns a non-JSON 200 body (e.g.
`"<html>502 Bad Gateway</html>"`) and whose `FetchHealthAsync` returns a normal healthy
`HealthCheckResponse`; run `MeshAggregator.RunOnceAsync`; assert `manifest.Entries[0].Status ==
MeshServiceStatus.Healthy` **and** `MeshServiceSnapshot.Error == null` for that service (proving no
error is recorded anywhere), then separately assert the same run against a source returning a
genuinely spec-less-but-valid document (`"{}"`) produces byte-identical `topics.json`/`topology.json`
contributions for that service — proving the two states are indistinguishable from the published
artifacts alone.

**Assessment:** genuine, concrete, and precisely the class of bug the brief asked to hunt for. Fix
shape (not applied, several viable): thread a "parse failed" signal out of `ParseTopics`/
`ParseOutboundTopics`/`ParseTransports` (they'd need to distinguish "no `requests`/`events` field" —
legitimately empty — from "the document didn't parse as JSON at all" — which they already can
internally, they just discard it) and surface it as an additive `SpecParseError` (or similar) on
`MeshServiceSnapshot`/`MeshManifestEntry`, mirroring the existing `Error`/`ErrorClass` treatment for
fetch-layer failures.

---

## Finding 2 — `AsyncApiCompositor`'s per-service namespace is a lossy slug, not the service name: two service names that normalize to the same slug silently overwrite each other's channels and schemas in the composite `asyncapi.json`

**Where:** `src/Benzene.Mesh.Aggregator/AsyncApiCompositor.cs` — `Slug` (393-404), `SchemaKey`
(385-391), and the channel/schema merge loop (61-159, especially the unguarded
`channels[referenced] = channelObj;` at 157 and `schemas[schemaRenames[schema.Key]] = ...` at 80).

**The gap.** The compositor's whole correctness claim — stated explicitly in its own XML doc — is:

> "so each service's content is *namespaced*... and every `$ref`... is rewritten to match... so nothing
> overwrites."

The namespace it actually uses is not the service name; it's `Slug(service.ServiceName)`:

```csharp
private static string Slug(string value)
{
    var lowered = new string((value ?? string.Empty).Trim().ToLowerInvariant()
        .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
    while (lowered.Contains("--")) { lowered = lowered.Replace("--", "-"); }
    lowered = lowered.Trim('-');
    return lowered.Length == 0 ? "service" : lowered;
}
```

Every non-alphanumeric character collapses to the same `-`, so two **distinct** `MeshServiceRegistryEntry.Name`
values that a mesh operator would consider obviously different can produce the **identical** slug — no
naming-scheme constraint on `Name` prevents this (`Benzene.Mesh.Contracts`'s own `CLAUDE.md` documents
it as a free-form human-edited string). Concretely:

- `"Orders API"` → lower → `"orders api"` → non-alnum→`-` → `"orders-api"`
- `"orders_api"` → lower → `"orders_api"` → non-alnum→`-` → `"orders-api"`

Same slug from a purely cosmetic naming-convention difference (space vs. underscore — an entirely
ordinary real-world inconsistency between two teams' registry entries, or even one deploy renaming a
service's display label while a stale registry entry with the old spelling lingers). Both are valid,
independently registered services with independently generated AsyncAPI docs.

Once two services collide on `ns = Slug(...)`:

1. **Channels have no collision guard at all.** Only operation keys go through `UniqueKey` (line 148);
   channels are written directly: `channels[referenced] = channelObj;` (line 157). If both colliding
   services happen to name a channel the same thing locally (a very ordinary occurrence — `"default"`,
   `"events"`, `"created"`, or simply the same topic id used as the channel address by convention),
   both compute the identical namespaced key `${ns}_${channel.Key}` and the second one processed
   silently overwrites the first's channel definition in the shared `channels` object. Operations from
   the first service that were already rewritten to reference that key now point at the second
   service's (structurally different) channel.
2. **Schema keys collide whenever both services declare a component with the same bare type name** —
   extremely common for generic names (`Error`, `Pagination`, `Envelope`, `Order`) that recur across
   independently-written OpenAPI-ish specs:
   ```csharp
   schemas[schemaRenames[schema.Key]] = schema.Value?.DeepClone();
   ```
   is likewise unguarded. The later-processed service's schema silently replaces the earlier one's
   under the same `${PrefixFromSlug}_${TypeName}` key — but the earlier service's own `$ref`s were
   already rewritten (`RewriteSchemaRefs`, run *before* the union) to point at that exact key, so its
   messages now resolve, in the published composite document, to the **other** service's schema.

This directly contradicts the class's own doc comment ("each service's content is namespaced ... so
nothing overwrites") and its remarks block ("Two services that share a topic stay as two distinct,
attributed channels+operations rather than being forced into one") — that claim only holds when the two
services' *slugs* don't collide, which nothing in the type enforces or even checks.

**Regression test that would prove this (not written):** two `AsyncApiCompositor.ServiceDocument`s named
`"Orders API"` and `"orders_api"`, each a minimal valid AsyncAPI 3.0 doc with one channel `"created"`
and one component schema `"Order"` with **different** `properties` (e.g. one has `id: string`, the
other `id: integer, amount: number`). Call `AsyncApiCompositor.Merge`. Assert the merged document
contains only **one** `orders-api_created` channel (not two, one per service) and that
`components.schemas.OrdersApi_Order` matches only the second service's shape — proving the first
service's channel/schema is gone from the composite document rather than living alongside the second's
under a distinct key, which is what the class's own contract promises.

**Assessment:** a real, concrete data-integrity bug in the merge — not hypothetical, since `Name` is a
free-form field with no uniqueness-of-slug guarantee anywhere in `Benzene.Mesh.Contracts`. It's exactly
the kind of "genuine collision" the brief asked this territory to hunt for in the compositor. Fix shape
(not applied): key channels/schemas by the **raw service name** (or a name→ordinal index, guaranteed
unique by construction since `entries`/`services` is already a list) rather than a lossy slug used only
for the human-readable prefix, or at minimum extend the existing `UniqueKey` collision-guard (already
used for operations) to channels and schemas too, so a collision degrades to a disambiguating suffix
instead of a silent overwrite.

---

## Finding 3 — `ApplyCrossVersionCompatibility` assumes ordinal string sort of a topic's version strings puts them in true chronological order; it doesn't, once a topic reaches a double-digit version, so the wrong pair of versions gets compared and the wrong "does this break the previous version" verdict is shown

**Where:** `src/Benzene.Mesh.Aggregator/MeshAggregator.cs` — the sort at line 514
(`.ThenBy(x => x.Topic, StringComparer.Ordinal).ThenBy(x => x.Version, StringComparer.Ordinal)`), and
`ApplyCrossVersionCompatibility` (541-573), specifically `var index = Array.IndexOf(siblings, entry);`
(560) and `return WithCompatibility(entry, CompareVersions(siblings[index - 1], entry));` (570).

**The gap.** Per this feature's own doc comment (541-573) and its `CLAUDE.md` section ("Cross-version
compatibility"), the whole point of `ApplyCrossVersionCompatibility` is: for a topic with several live
versions, tell the reader whether each version **breaks a consumer still on the version published
immediately before it**. The code's own comment states the mechanism plainly:

> "`topics` is already ordered `ThenBy(Version, Ordinal)`, so a version's predecessor is the entry
> immediately before it within the same topic."

That assumption is false whenever a topic's version strings aren't all the same digit-length, because
`StringComparer.Ordinal` sorts codepoint-by-codepoint, not numerically. For a topic versioned
`"v1"`..`"v11"` (an entirely ordinary numbering scheme — no zero-padding is required or enforced
anywhere on `MeshTopicEntry.Version`, whose own doc comment just says "the topic's handler version"),
ordinal sort produces:

```
v1, v10, v11, v2, v3, v4, v5, v6, v7, v8, v9
```

`ApplyCrossVersionCompatibility` walks this array treating `siblings[index - 1]` as "the version
published before" `siblings[index]`. Concretely, from the above ordering:

- `v10` (index 1) is compared against baseline `v1` (index 0) — **v2 through v9 are skipped entirely**;
  the "does v10 break the version before it" verdict is actually "does v10 break v1," a comparison
  spanning nine real releases, not one.
- `v2` (index 3) is compared against baseline `v11` (index 2) — this is not "the version before v2," it's
  the version **nine releases later**. A field v11 added that v2 never had reads as "removed" in this
  backwards comparison; a genuinely breaking change between the true v1→v2 transition is never looked at
  at all, because v2's baseline here is v11, not v1.
- Only `v11` (correctly following `v10`) and `v3`..`v9` (each correctly following its true single-digit
  predecessor) happen to get a meaningful comparison — purely by the coincidence of where the "1"-prefixed
  block landed in ordinal order.

The `MeshTopicCompatibility.BaselineVersion` field does get set to whatever `siblings[index-1].Version`
was — so the wrong baseline is at least stated, not hidden — but the entire "does this break the
previous consumer" premise the reader opens this panel to check is answered against a version that was
never actually the topic's immediate predecessor, with no indication anything is off. The verdict shown
(`compatible`/`warning`/`breaking`) is computed from a real schema diff, so it isn't fabricated data —
it's a **true diff between the wrong pair of versions**, presented with the same confidence as a diff
between the right pair.

This is not a hypothetical edge case for a mesh feature explicitly aimed at long-lived services: any
domain topic that survives ten-plus versioned releases (plausible over a few years for a busy topic)
silently starts producing wrong-baseline comparisons for two of its versions, and the wrongness compounds
(gets worse, covering more of the version range) the more the version count grows past ten.

**Confirmed untested:** `test/Benzene.Mesh.Test/MeshAggregatorTest.cs`'s existing cross-version-compat
coverage (`RunOnceAsync_SecondRun_ADriftedTopicKeepsItsCrossVersionVerdict` et al., ~1442-1463) only
ever exercises `"v1"`/`"v2"` — single-digit versions, where ordinal and numeric order agree, so this
gap is invisible to the current suite.

**Regression test that would prove this (not written):** register a topic `order:create` at eleven
versions `v1`..`v11`, each declaring a distinct `request` schema (e.g. `id: string` for v1..v9,
`id: string, note: string` for v10, `id: string` again for v11 — anything that lets a v1-vs-v10
comparison and a true-v9-vs-v10 comparison disagree). Run `MeshAggregator.RunOnceAsync`, read
`topics.json`, and assert `catalog.Topics.Single(t => t.Version == "v10").Compatibility!.BaselineVersion
== "v9"` — it will actually be `"v1"`, proving the wrong pair was compared.

**Assessment:** a genuine, demonstrable correctness bug in a feature whose entire stated value
proposition is "tell the reader whether this specific version transition breaks anyone" — the transition
it reports on is, for any topic past nine versions, frequently not the transition that actually
happened. Fix shape (not applied): either constrain/require topic version strings to a defined,
declared ordering scheme the way `Benzene.Mesh.Contracts.MeshVersionOrder`/`MeshVersionScheme` already
do for **service** versions (mesh.md §2.5) — that machinery already exists in this same package family,
just not wired to topic versions — or, short of that, stop relying on sort-adjacency for "predecessor"
and instead require/parse a numeric ordering explicitly, falling back to "not comparable" (mirroring
`MeshVersionOrdering.NotOrderable`'s own precedent) rather than silently picking a sort-adjacent
non-predecessor.

---

## Finding 4 — `MeshSelfReportMiddleware`'s deliberately-unawaited publish is unreliable on AWS Lambda, the package's own named primary use case

**Where:** `src/Benzene.Mesh.Reporting/MeshSelfReportMiddleware.cs` — `HandleAsync` (55-63),
specifically `_ = PublishBestEffortAsync();` (61).

**The gap.** `HandleAsync` awaits `next()` (the real request/message), then — best-effort, deliberately
not blocking the caller — fires the actual report publish without awaiting it:

```csharp
public async Task HandleAsync(TContext context, Func<Task> next)
{
    await next();

    if (ShouldPublish())
    {
        _ = PublishBestEffortAsync();
    }
}
```

This is a *tested*, deliberate design choice — `MeshSelfReportMiddlewareTest.HandleAsync_DoesNotBlockOnASlowPublisher`
explicitly proves `HandleAsync` returns without waiting for a publisher that never completes. That's the
right behavior for a long-lived host (ASP.NET Core, self-host): the detached task keeps running on the
thread pool after the response is sent.

But this package's own `CLAUDE.md` names its reason for existing as precisely the opposite kind of host:
"for services with **no** synchronous entry point... e.g. an AWS Lambda whose only event source is
SQS/SNS/EventBridge." On AWS Lambda's .NET execution model, the runtime freezes the execution
environment shortly after the handler's **returned `Task`** completes; any work not part of that awaited
chain (exactly what `_ = PublishBestEffortAsync()` is — a detached task the outer pipeline's `Task`
does not wait on) is not guaranteed to run to completion. Once `MeshSelfReportMiddleware.HandleAsync`
returns (which happens immediately after firing, not completing, the publish), the surrounding
pipeline's `Task` — and with it, on Lambda, the whole function invocation — can complete and the
container can be frozen mid-flight through `PublishBestEffortAsync`'s first real `await` (the
`SpecProvider()`/`HealthProvider()` call, or the HTTP POST inside `_publisher.PublishAsync`), with no
guarantee it resumes before the container is reused, frozen indefinitely, or torn down.

**Why it matters concretely:** for the flagship deployment this package exists for (a queue-only Lambda
with literally no other way for `Benzene.Mesh.Aggregator` to observe it), the self-report — the entire
mechanism this package provides — is likely to be silently dropped on a cold-ish/bursty invocation
pattern (exactly when a fresh container handles one message and freezes right after), while appearing to
work reliably in any local/integration test (which runs on a normal thread pool with no freeze
semantics) and on a warm, sustained-traffic container (where the background task has time to complete
before the next freeze). This is the inverse of the false-negative testing gap: the test suite's own
`HandleAsync_DoesNotBlockOnASlowPublisher` *proves* the exact property that is safe off Lambda and
unsafe on it, so the passing test gives false confidence for the platform the package is written for.

**Not previously flagged:** the package's own "Known gap" section only names the staleness-representation
gap (`MeshServiceStatus` has no `Stale` value); it does not mention this Lambda-freeze interaction, and
no example wires `UseMeshSelfReport()` into an actual Lambda host to have surfaced it empirically.

**Regression test that would demonstrate the risk (not written, and can only demonstrate the mechanism —
proving the Lambda freeze itself needs a real Lambda runtime, out of reach here):** construct
`MeshSelfReportMiddleware` with a publisher whose `PublishAsync` yields on an uncompleted
`TaskCompletionSource` before ever calling a recording delegate; call `HandleAsync`; assert it returns
(as the existing test already does) *without* the publisher's recording delegate ever having been
invoked — i.e., assert the publish genuinely has not started/completed by the time the caller regains
control, which is exactly the window a Lambda freeze can land in.

**Assessment:** a real tension between an explicitly-tested design choice and the package's own stated
primary target platform, not a simple one-line bug — flagging it as a `[DECISION]`-shaped finding for a
future round, since a fix has real trade-offs: awaiting the publish (defeating "never delays the
response," the other explicit design goal) vs. documenting the caveat and steering Lambda users toward
an explicit "flush before response ends" hook if the underlying Lambda host exposes one, vs. accepting
the current best-effort framing but stating plainly in the package's own docs that "opportunistic" on
Lambda specifically means "frequently doesn't happen," which the current text does not say.

---

## Ruled out / not re-reported

- **`Benzene.Mesh.Artifacts.MeshArtifactMiddleware.IsArtifact`'s traversal allow-list.** Its
  `key.StartsWith("services/") && key.EndsWith(".json")` check does not itself reject a `.json`-suffixed
  traversal path like `services/../../../etc/aws-credentials.json` (the package's own test suite only
  covers non-`.json`-suffixed traversal shapes, e.g. `/services/../../../etc/passwd`) — so the class's
  own claim that "the middleware itself never forwards a traversal-shaped key at all" is not quite true
  for every shape. Traced through to `FileSystemMeshArtifactStore.ResolveWithinRoot`
  (`src/Benzene.Mesh.Aggregator/FileSystemMeshArtifactStore.cs`, the #242 fix), which independently
  rejects any `.`/`..` path segment before touching disk, regardless of caller — and the cloud stores
  (S3/Blob/GCS, outside this territory) treat object keys as flat strings with no path-traversal
  semantics at all. No exploitable path found; not written up as a finding, just noted here so a future
  round doesn't re-walk the same trace.
- **`Benzene.Mesh.Discovery.Kubernetes`.** Deliberately hardened against label-driven SSRF
  (`SanitizeScheme`/`SanitizePath` both explicitly reject anything that could restructure the
  `{scheme}://{authority}{path}` URL); pagination correctly follows `continue` to exhaustion. Nothing
  found.
- **`Benzene.Mesh.Ui`/`Benzene.Spec.Ui` server-side C#** (`MeshUiPage`/`MeshUiMiddleware`/
  `MeshUiExtensions`, `SpecUiPage`/`SpecUiMiddleware`/`SpecUiExtensions`). Small, symmetric with each
  other, and already the subject of round 14-15's WP-J vendoring-doc fix and three tracked upstream
  `benzene-ui` items (#205-#207). No new issue found in the C# wrapper code itself.
- **`Benzene.Mesh.Wire`.** Heavily spec-pinned (conformance fixtures) and already the subject of
  several prior rounds' fixes (#233, #246-#248 wiring, the issue-feed work). Read `MeshDescriptorFactory`,
  `MeshSchemaGenerator`, `MeshOutboundRegistry`, `HttpMeshTraceExporter`/`HttpMeshIssueExporter` against
  their own `CLAUDE.md`'s claims; nothing contradicted what's documented, and the package's own test list
  is unusually thorough (direct per-branch coverage of `MeshSchemaGenerator.Derive`). No new issue found.

## Where I had to read source

- `Benzene.Mesh.Contracts.MeshServiceVersion`/`MeshVersionOrder` (mesh.md §2.5's scheme-aware version
  ordering for a **service's** overall release version) already exists in this exact package family and
  already solves the "ordinal string sort misorders double-digit versions" problem correctly — but it is
  wired to nothing in `Benzene.Mesh.Aggregator` today (confirmed via repo-wide grep: only
  `Benzene.Descriptor`/its own tests reference it). It is a different concept from a topic/message's
  per-request `version` field (`MeshTopicEntry.Version`), which is what Finding 3 is about — but its
  existence in the same package family shows the ordinal-sort trap is already a known, named problem the
  project has solved once, just not for this call site.
- `HttpMeshServiceSource.FetchSpecAsync`'s use of `GetStringAsync` (vs. `FetchHealthAsync`'s deliberate
  `GetAsync` + always-read-body, chosen specifically because health can legitimately answer 503) is what
  makes Finding 1's "200 with garbage body" scenario reachable at all — a fetch failure on the spec side
  really does need an exception to be thrown to be caught, and a 200 status never throws one regardless
  of body content.
