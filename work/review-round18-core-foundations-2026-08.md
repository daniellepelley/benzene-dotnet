# Round 18 review — Core / Abstractions / Middleware / Hosting foundations

Scope per this round's brief: `Benzene.Abstractions*` (5 packages), `Benzene.Core*` (5 packages),
`Benzene.Results`, `Benzene.Http`, `Benzene.AspNet.Core`, `Benzene.HostedService`, `Benzene.SelfHost`,
`Benzene.Configuration.Core`, `Benzene.Microsoft.Dependencies`, `Benzene.Autofac`, `Benzene.Testing`,
plus their `test/` coverage. Reviewed against `main`/`7f642b2`. No dotnet SDK was available in this
environment (per this round's constraints) — every finding below is traced by hand, line by line,
against the actual compiled semantics; no test was run. `work/outstanding-bugs.md` and
`work/review-round17-performance-2026-08.md` were read in full first to avoid re-filing anything
already tracked — the round-17 headline findings in this exact territory (`PollyResilienceMiddleware`'s
guard-counter leak #288, `MicrosoftServiceResolverAdapter`/`Factory`'s sync-over-async deadlock #289,
`CompositeBenzeneWorker.StartAsync`'s swallowed-fault hang #291) are all confirmed **already fixed and
tested** in current `main` (verified by reading the fixed source, not just the changelog entry) and are
not re-litigated here.

This is the fourth or fifth consecutive round to sweep this exact territory (rounds 7-10, 10, 14-15,
16, 17 all touched pieces of it), so the honest summary up front: **most of this territory is
genuinely clean.** One real, previously-unfiled correctness gap was found
(`CompiledRoutePath`/`Benzene.Http.Routing`, below); everything else that looked promising on first
read (DI adapter lifetime/disposal, cancellation-token seeding, pipeline caching, versioning-caster
recursion guard, message routing) traced through correctly on close inspection, sometimes only after a
long chain of reasoning that a prior round had already walked. Padding this report with restated
round-16/17 conclusions would misrepresent how much is actually new here, so the "swept, no finding"
section below is intentionally explicit about *why* each area is clean rather than just asserting it.

---

## Finding 1 — `CompiledRoutePath` compiles a route pattern with two or more `{parameter}`s in the
same path segment without error, but the resulting route can then never match any real request — and
nothing in the startup-check pipeline (which exists specifically to catch this class of misconfiguration
for sibling cases) catches it

**Severity: medium.** Not data corruption and not a hidden production incident (a developer testing the
endpoint for the first time gets an immediate, unconditional 404) — but it is a genuine, concrete,
provable defect: a route pattern that looks completely valid, uses a documented feature (named route
parameters) in a completely ordinary way, compiles with **zero** error or warning, and then can
**never** be reached by any HTTP request. Every sibling misconfiguration this same package guards
against (duplicate routes, an `[HttpEndpoint]` handler missing its `[Message]`, an unresolvable
pipeline) gets an explicit, named startup check; this one does not, so the developer's only signal is a
mysterious 404 with nothing in the framework's error surface pointing at the route pattern as the
cause.

Round 16's own review of this exact file (`work/review-round16-core-2026-08.md`, "Other areas swept")
noted: *"single-parameter-per-segment is a documented limitation, not a silent-corruption bug."* That
conclusion is correct as far as it goes — this is not corruption (no wrong parameter value is ever
extracted) — but it characterizes the wrong failure mode. The actual defect isn't that multi-parameter
segments are unsupported (a legitimate, documented design choice); it's that violating that limitation
degrades silently into a permanently-broken route with no diagnostic, in a package whose own author
clearly considers "silent 404 with no diagnostic" enough of a problem to write four dedicated
startup/discovery checks for near-identical situations (`UnroutedHttpEndpointCheck`,
`HttpRouteStartUpCheck`, `ReflectionHttpEndpointFinder`'s duplicate-route check, `RouteFinder`'s
literal-before-parameter specificity ordering). This is the gap those checks leave open.

### The defect

`src/Benzene.Http/Routing/CompiledRoutePath.cs:24-41` (constructor):

```csharp
public CompiledRoutePath(string routerPath)
{
    var routerPathParts = UrlMatcher.SplitPath(routerPath);
    _segments = new Segment[routerPathParts.Length];

    for (var i = 0; i < routerPathParts.Length; i++)
    {
        var routeParts = UrlMatcher.SplitRouterPath(routerPathParts[i]);
        var paramIndex = Array.FindIndex(routeParts, x => x.StartsWith("{"));

        _segments[i] = paramIndex < 0
            ? Segment.CreateLiteral(string.Concat(routeParts))
            : Segment.CreateParameter(
                routeParts[paramIndex].Replace("{", "").Replace("}", ""),
                string.Concat(routeParts.Take(paramIndex)),
                string.Concat(routeParts.Skip(paramIndex + 1)));   // <-- everything after the FIRST
                                                                     //     "{...}" part, including a
                                                                     //     second, unprocessed "{...}"
    }
}
```

`UrlMatcher.SplitRouterPath` (`src/Benzene.Http/Routing/UrlMatcher.cs:52-58`) splits one path segment
into literal/`{param}` runs via `Regex.Split(routerPath, @"(?<=\})|(?=\{)")`. For a segment with a
single parameter (`"example-{id}"`, `"{id}-foo"`) this correctly yields one `{...}` part plus its
literal neighbours, and `CompiledRoutePath` builds a `Segment` with `ParamName="id"` and the correct
literal `Prefix`/`Suffix` — this is the documented, working, single-parameter-per-segment case, and it
is correct.

The break is `Array.FindIndex(routeParts, x => x.StartsWith("{"))`: it finds only the **first** part
that starts with `{`. For a segment with a **second** parameter, `routeParts.Skip(paramIndex + 1)`
still contains that second `{...}` part **untouched** — its literal braces and parameter name are
concatenated verbatim into `Suffix`, a string that is then matched **literally** (never re-parsed as a
parameter) by `Match`:

```csharp
// src/Benzene.Http/Routing/CompiledRoutePath.cs:75-80 (Match)
if (segment.Length < seg.Prefix.Length + seg.Suffix.Length
    || !segment.StartsWith(seg.Prefix, StringComparison.OrdinalIgnoreCase)
    || !segment.EndsWith(seg.Suffix, StringComparison.OrdinalIgnoreCase))
{
    return null;
}
```

Concretely, for the route pattern `/files/{name}.{ext}` — an entirely ordinary "file with an
extension" URL shape:

1. `SplitRouterPath("{name}.{ext}")` → `["{name}", ".", "{ext}"]`.
2. `paramIndex = 0` (the first, and only, part `FindIndex` looks for).
3. `ParamName = "name"`, `Prefix = ""`, `Suffix = string.Concat(["." , "{ext}"]) = ".{ext}"`.
4. At match time, `seg.Suffix` is the **7-character literal string** `".{ext}"` — the incoming path
   segment must literally end with the seven characters `.`, `{`, `e`, `x`, `t`, `}` for a match to
   succeed. No real filename (`report.pdf`, `image.png`, ...) ever does. The route is compiled
   successfully, registered, appears in `RouteFinder`'s route table and in the generated OpenAPI spec —
   and then answers **every** request with a 404, forever, with no error anywhere in the framework's
   own diagnostics pointing at the pattern as the cause.

The same failure hits any multi-parameter segment shape: `/report/{year}-{month}`,
`/orders/{id}v{version}`, `{a}{b}` with no separator at all — in every case the second (and any
subsequent) `{param}` is silently absorbed into a literal suffix string instead of being parsed as a
parameter, and the route becomes permanently unmatchable.

### Why nothing catches it

- `HttpRouteStartUpCheck.Check` (`src/Benzene.Http/Routing/HttpRouteStartUpCheck.cs:27-32`) exists
  *specifically* to force route-table compilation at init instead of on the first request, precisely so
  a route-compilation problem surfaces as a startup failure. `CompiledRoutePath`'s constructor never
  throws for this shape — it happily builds a `Segment` with a garbage `Suffix` — so this check passes
  clean on a route that can never work.
- `ReflectionHttpEndpointFinder`'s duplicate-route check (referenced in `src/Benzene.Http/CLAUDE.md`)
  only compares `(Method, Path)` pairs for exact duplicates; it has no opinion on whether a `Path`
  pattern is well-formed.
- Nothing in `HttpEndpointDefinition`/`HttpEndpointAttribute` validates the `Path` string's shape at
  registration time either.
- `RouteFinder`'s own doc comment (`CountParameterSegments`) and `CompiledRoutePath`'s own remarks
  block explicitly acknowledge "a single parameter per segment" as the supported shape — so the
  authors were aware of the limitation — but there is no corresponding guard that turns a *violation*
  of that limitation into an actionable error instead of a silently dead route.

### Verified reasoning (no dotnet available — traced by hand against the exact source above)

Given route pattern `/files/{name}.{ext}` and an incoming request `GET /files/report.pdf`:

- `RouteFinder.Find` splits the incoming path via `UrlMatcher.SplitPath` → `["files", "report.pdf"]`.
- `route.Path.Match(["files", "report.pdf"])` is called on the `CompiledRoutePath` built from
  `/files/{name}.{ext}`, which has two `Segment`s: a literal `"files"` and the broken parameter segment
  described above (`Prefix=""`, `Suffix=".{ext}"`).
- Segment 0 (`"files"` vs literal `"files"`) matches.
- Segment 1: `segment = "report.pdf"` (10 chars), `seg.Prefix.Length + seg.Suffix.Length = 0 + 6 = 6`.
  `10 >= 6` passes the length guard, but `segment.EndsWith(".{ext}", OrdinalIgnoreCase)` is `false` —
  `"report.pdf"` does not end with the literal text `.{ext}`. `Match` returns `null`.
- `RouteFinder.Find` continues to the next candidate route (none matches) and returns `null` →
  the caller's 404 path.

This holds for **every** possible incoming segment value, not just this example — the literal suffix
`.{ext}` (or any absorbed-second-parameter suffix) can only be satisfied by a URL that contains a
literal, unencoded `{` and `}`, which no real client ever sends.

### Recommended fix + regression test (for whoever picks this up — not run here, no dotnet available)

Fix direction: `CompiledRoutePath`'s constructor should detect more than one part starting with `{` in
`routeParts` and throw a clear, actionable `BenzeneException` naming the offending path and segment
(e.g. *"Route pattern '/files/{name}.{ext}' has more than one parameter in one segment
('{name}.{ext}') — only one {parameter} per path segment is supported."*) instead of silently building
a garbage `Suffix`. Because `HttpRouteStartUpCheck` already forces `IRouteFinder` (and therefore every
`CompiledRoutePath`) to construct at init, this turns the failure into the same fail-fast startup error
every sibling misconfiguration already gets — no new check type needed, just validating in the
constructor that already runs at the right time.

Regression test (extends `test/Benzene.Core.Test/Core/Http/UrlMatcherTest.cs` or
`RouteFinderTest.cs`, neither of which currently has any case with two `{...}` parts in one segment):

```csharp
[Fact]
public void CompiledRoutePath_TwoParametersInOneSegment_ThrowsAtConstructionInsteadOfBuildingAnUnmatchableRoute()
{
    Assert.Throws<BenzeneException>(() => new CompiledRoutePath("/files/{name}.{ext}"));
}

// Pins today's actual (broken) behavior as a red test until the fix lands - proves the route is
// permanently unmatchable rather than merely "not yet tested":
[Fact]
public void CompiledRoutePath_TwoParametersInOneSegment_CurrentlyNeverMatchesAnyInput()
{
    var route = new CompiledRoutePath("/files/{name}.{ext}");
    Assert.Null(route.Match(new[] { "report.pdf" }));
    Assert.Null(route.Match(new[] { "report.{ext}" })); // even a segment containing literal braces
                                                          // fails too, because "name" is empty
                                                          // (Prefix="" means the whole thing up to
                                                          // ".{ext}" is captured as the value, and the
                                                          // empty-value rule at line 83-90 rejects it
                                                          // only for a genuinely empty capture - this
                                                          // case would actually accept "report" as
                                                          // `name`, which is the closest this gets to
                                                          // matching, and still requires a client to
                                                          // send literal '{'/'}' characters).
}
```

---

## Other areas swept — no additional finding clearing the bar

Each of these was read in full (not skimmed) against the failure classes this round's brief calls out
(dispatch order, cancellation-token propagation, scope/disposal lifetime, thread-safety of shared
state, exception-handling consistency), specifically re-applying the same lens the last several rounds
used, since this territory has been reviewed repeatedly:

- **`Benzene.Http.BenzeneMessage.BenzeneMessageHttpMiddleware<TContext>`'s round-17 cancellation fix
  (#285)** — re-verified end to end: `_serviceResolver` is the per-request scoped resolver (confirmed
  by tracing `MiddlewarePipeline<TContext>.CreateChain`, which resolves each middleware factory fresh
  from the per-call `serviceResolver` inside the chain closure, not once at pipeline-build time), so
  `TryGetService<ICancellationTokenAccessor>()` at dispatch time correctly reads the same scope
  `BuildHttpPipeline`'s `SeedCancellationToken` middleware seeded earlier in the same request. No stale-
  scope/cross-request leak.
- **`Benzene.Autofac`** — the round 14-15 `IsGenericTypeDefinition` fix (#210, closed-generic routing)
  and the `IsTypeRegistered`/`_registeredTypes` tracking it depends on were re-traced across all six
  `Type`-based `Add*` overloads; each correctly indexes `_registeredTypes` by the *service* type (not
  implementation type) so `TryAdd*` semantics hold. `AutofacServiceResolverFactory`'s owning-vs-
  non-owning constructor split and `AutofacServiceResolverAdapter`'s lazy `ResolverFactory` fallback
  both check out — no double-`Build()`, no disposal of a scope the adapter doesn't own.
- **`Benzene.Microsoft.Dependencies`** — the round-17 `#289` `SynchronizationContext`-suppression fix
  around `MicrosoftServiceResolverAdapter.Dispose()`/`MicrosoftServiceResolverFactory.Dispose()` is
  present and correctly scoped (suppress → block → restore in a `finally`) at both call sites.
  `AddServiceResolver()` registers `IServiceResolver` as `TryAddTransient`, which is correct, not a
  scope leak: MEL passes the *current* scope's `IServiceProvider` into the factory delegate regardless
  of the *registration's* own lifetime, so each transient-resolved adapter still wraps the right scope.
- **`Benzene.Configuration.Core`** (all 7 source files read in full) — `CachingSecretStore`,
  `CompositeSecretStore`, `FileSecretStore`, `EnvironmentVariableSecretStore`, `InMemorySecretStore`,
  `SecretResolver`, `SecretValidation`: no bug found. The documented "concurrent misses may each fetch
  once" behavior in `CachingSecretStore` is a deliberate, correctly-reasoned tradeoff (no lock on the
  read path), not an oversight.
- **`Benzene.HostedService`/`Benzene.SelfHost`** — `BenzeneHostedServiceAdapter.StartAsync`/`StopAsync`
  and the round-17 `#291` `CompositeBenzeneWorker.StartAsync` fault-race fix were both re-traced in
  full. One secondary, **not filed as a finding**, observation worth recording for a future round:
  `BenzeneHostedServiceAdapter.ObserveFault`'s `catch (Exception ex)` block treats *any* exception
  escaping the worker's `StartAsync` task — including a bare `OperationCanceledException`/
  `TaskCanceledException` that is not a genuine unhandled fault — as a `LogCritical` + optional
  `IHostApplicationLifetime.StopApplication()` event. Concretely, if a future or third-party
  `IBenzeneWorker.StartAsync` implementation let an `await` on the linked `_stoppingCts.Token` (or any
  cancellation) propagate out uncaught during ordinary shutdown (rather than checking
  `cancellationToken.IsCancellationRequested` and returning normally, or explicitly catching
  `OperationCanceledException`, as every shipped worker in this codebase — `SqsConsumer`,
  `BenzeneKafkaWorker`, `RabbitMqWorker`, `AspNetServerWorker` — already does), a routine shutdown would
  be misreported as a critical fault. **Not filed** because every shipped `IBenzeneWorker` in this
  codebase was individually checked and none exhibits the trigger shape (`SqsConsumer` explicitly
  `catch (OperationCanceledException)`s inside its poll loop and exits its `while` cleanly;
  `BenzeneKafkaWorker`/`RabbitMqWorker`/`AspNetServerWorker` all return from `StartAsync` promptly,
  well before any cancellation, and run their real lifetime on a separately-managed background task) —
  this is a latent robustness gap in shared code, not a currently reachable bug, so it does not clear
  this round's "concrete failure scenario" bar. Worth a defensive fix (narrow the catch to exclude an
  `OperationCanceledException` whose token matches `_stoppingCts.Token`) the next time this file is
  touched for another reason.
- **`Benzene.Core.Versioning`** — `CasterFuncBuilder`'s round-14/15 recursion guard (`#226`,
  `RecursionCell<TFrom,TTo>`) re-traced correctly (install-before-recurse, replace-after-compile,
  remove-on-failure). `RequestBodyReader<TContext>`'s per-type compiled-delegate cache and
  `SchemaCastDefinitionsExpander`'s BFS chain composition (visited-set based, provably terminating,
  correctly reconstructs the shortest chain) both checked out with no defect.
- **`Benzene.Core.MessageHandlers`** — `MessageRouter<TContext>.HandleAsync` (the dispatch entry
  point), `MessageGetter<TContext>`/`ResolvedTopicCache<TContext>` (per-scope memoization, correctly
  generic-per-`TContext` so no cross-transport contamination), `HandlerPipelineStructureCache`
  (structure-once/instances-per-request split, correctly resolves middleware from the per-call
  resolver inside the chain closure, not eagerly), `MediaFormatNegotiator<TContext>` (scoped,
  correctly memoizes per-message) and `DI/Extensions.cs`'s registration composition
  (`RegisterHandlerFinderInfrastructure`'s lazy union-of-candidate-types finder) were all read in full;
  no ordering, disposal, or thread-safety defect found.
- **`Benzene.Testing`** — re-confirmed round 17's own conclusion: all four files are stateless factory
  namespaces, no mutable shared state.
- **`Benzene.Results`** — `BenzeneResultExtensions`'s `Task<IBenzeneResult<T>>`-returning `As<...>`
  overloads read `source.Result` after `await source` (not before) — safe (task is already complete),
  just an unusual style choice, not a sync-over-async bug.

## Summary

| # | Finding | Severity | Status |
|---|---------|----------|--------|
| 1 | `CompiledRoutePath` compiles a multi-parameter-per-segment route pattern (e.g. `/files/{name}.{ext}`) with no error, but the route can then never match any real request, and no startup check (unlike every sibling misconfiguration in the same package) catches it | Medium | New — traced by hand, not previously filed (round 16 examined this file and correctly ruled out corruption, but did not flag the missing-startup-validation angle) |

One finding this round, plus one secondary/latent observation (`BenzeneHostedServiceAdapter
.ObserveFault`'s cancellation-vs-fault classification) recorded above but deliberately not filed as a
finding, since no shipped code currently reaches it. This territory has had five consecutive rounds of
scrutiny; the yield reflects that — most of what looked promising on first read (DI adapter lifetimes,
cancellation seeding, pipeline/topic caching, the versioning recursion guard) traced through correctly,
often only after re-deriving conclusions a prior round had already reached. That is reported plainly
rather than manufacturing findings to fill out the doc.

**Recommendation: REQUEST CHANGES** on Finding 1 (loop in whoever owns `Benzene.Http`'s routing
package) — low urgency (no shipped route pattern in this repo currently uses this shape, confirmed via
`grep` across `src/`), but worth fixing before a real application hits it silently, since the failure
mode (permanently dead route, zero diagnostic) is exactly the class of bug this package's own startup
checks exist to prevent for every other misconfiguration shape.
