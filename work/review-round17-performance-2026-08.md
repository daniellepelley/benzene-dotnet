# Round 17 review — Performance & Reliability sweep (cross-cutting)

Scope, per this round's brief: (1) re-apply the established hunt list (ambient-cancellation gaps,
unbounded fan-out, `IDisposable`/`IAsyncDisposable` mismatches, thundering-herd/single-flight gaps)
specifically against round 16's own freshest fixes — `MicrosoftServiceResolverAdapter`'s async-bridge
disposal (#266), `MeshCollectorStore`'s per-version keying (#251), and the Polly re-entrancy guard
(#267); (2) a first-ever pass over `Benzene.SelfHost`, `Benzene.Testing`, `Benzene.Descriptor`,
`Benzene.CloudService.Probe`; (3) a repeat `IDisposable`/`IAsyncDisposable`-declared-alone sweep,
including `Benzene.Mesh.*`/`Benzene.GoogleCloud.*` specifically; (4) a benchmarking-gap note where a
package's hot path shows a real (not hypothetical) complexity/allocation change. Reviewed against
`main`/`4389bfb` in the shared `/workspace/benzene-dotnet` checkout (the review agent's actual cwd is
a separate spec-only repo per its own `CLAUDE.md`; the checkout under review lives at
`/workspace/benzene-dotnet`, per this task's explicit repo pointer).

Read-only review. No source file under `src/` or `test/` on `main` was modified. Four verification
test files were written to prove the findings below, run, and then deleted (not committed) — reproduced
in full in each finding so any of them can be re-verified or turned into a permanent regression test by
whoever picks up the fix.

## Note on environment

This checkout is shared with other concurrently-running review agents, exactly as round 16 noted. Over
the course of this review the whole-project test build broke and un-broke itself at least once (another
agent's own `test/Benzene.Core.Test/Idempotency/DynamoDb/RedTest_ExpiryBoundaryTest.cs` scratch file was
referenced by a stale `.csproj` `<Compile Remove>` edit mid-run, and a `test/Benzene.Core.Test/
Plugins/JsonSchema/JsonSchemaVersionStatusMismatchTest.cs` from a different agent didn't compile). Every
finding below was re-run after the transient breakage cleared and confirmed passing/failing exactly as
described; none of the four proof files depended on any other agent's in-flight state once the shared
`.csproj` settled.

---

## Finding 1 (headline) — the round-16 Polly re-entrancy guard (#267) leaks its own counter on the
exact path it exists to protect, then misfires `NotSupportedException` against a later, fully
sequential, non-overlapping attempt in the same `HandleAsync` call

**Severity: high — the round-16 fix for the round-16 finding is itself broken on its own primary
purpose (fail *safely*, not just fail).** `src/Benzene.Resilience.Polly/PollyResilienceMiddleware.cs`
is literally this round's freshest code — a same-day fix (commit `ee33d27`, "#267") for last round's
own headline finding. Per this round's brief ("apply the same lens to round 16's own fixes"), it was
re-examined first.

### The defect

```csharp
// src/Benzene.Resilience.Polly/PollyResilienceMiddleware.cs, HandleAsync
var inFlight = new int[1];   // one counter per HandleAsync call

await _pipeline.ExecuteAsync(static async (state, attemptToken) =>
{
    if (Interlocked.Increment(ref state.inFlight[0]) > 1)
    {
        throw new NotSupportedException(
            "A concurrent-attempt resilience strategy (e.g. a custom hedge) is not " +
            "supported by PollyResilienceMiddleware<TContext>: ...");
    }

    var original = state._accessor.CancellationToken;
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(original, attemptToken);
    state._accessor.CancellationToken = cts.Token;
    try
    {
        await state.next();
        ...
    }
    finally
    {
        state._accessor.CancellationToken = original;
        Interlocked.Decrement(ref state.inFlight[0]);   // <-- only reached when the guard DIDN'T fire
    }
}, (next, context, _isFailure, _accessor, inFlight)).ConfigureAwait(false);
```

The `Interlocked.Increment` happens *before* the `try`/`finally` that decrements it. When the guard
fires (a second, concurrent attempt is detected), the method throws immediately — **outside** the
`try`/`finally` — so that increment is never undone. Trace it through:

1. Attempt A increments `inFlight[0]` from 0 → 1 (passes the guard), proceeds into the `try`.
2. Attempt B (a genuinely concurrent second attempt) increments `inFlight[0]` from 1 → 2, the guard
   fires, B throws `NotSupportedException` **without ever reaching the `finally`** — its increment is
   never paired with a decrement.
3. A eventually finishes and its `finally` decrements `inFlight[0]` from 2 → **1**, not 0.

`inFlight[0]` is now permanently stuck at 1 for the rest of that `HandleAsync` call (a fresh array is
allocated per call, so this never leaks *across* messages/calls — only within one). If anything in the
same pipeline execution invokes the guarded callback again — most obviously an **outer `Retry`
strategy wrapping the offending concurrent-attempt strategy**, which is exactly the composition anyone
reaching for "hedge, then retry the whole thing on failure" would build — that next invocation is a
single, purely sequential attempt (`Increment` from 1 → 2) and is **incorrectly rejected by the same
guard**, even though nothing overlapped with it at all. The very re-entrancy guard added specifically
to make failure safe instead of corrupting instead makes a completely ordinary, correct retry attempt
fail with a misleading "concurrent attempt" error.

### Verified reproduction

```csharp
// test/Benzene.Core.Test/Resilience/PollyGuardCounterLeakRedTest.cs
// (written, run, deleted - not committed; reproduced here in full)

public class PollyGuardCounterLeakRedTest
{
    // Round 1: fires two concurrent sub-attempts (like a hand-rolled hedge) so the guard trips on
    // one of them, leaking the counter. Round 2+: forwards a single, purely sequential attempt -
    // exactly what every out-of-the-box Polly strategy (Retry/Timeout/CircuitBreaker/RateLimiter)
    // does today.
    private sealed class ConcurrentOnFirstRoundStrategy : ResilienceStrategy
    {
        private int _round;

        protected override async ValueTask<Outcome<TResult>> ExecuteCore<TResult, TState>(
            Func<ResilienceContext, TState, ValueTask<Outcome<TResult>>> callback,
            ResilienceContext context,
            TState state)
        {
            var round = Interlocked.Increment(ref _round);
            if (round == 1)
            {
                // Polly v8 strategies communicate failure via Outcome<TResult>.Exception, not by
                // faulting the ValueTask itself - inspect both outcomes explicitly and return
                // whichever failed, mimicking a real hedge surfacing a rejected attempt's failure.
                var firstTask = callback(context, state).AsTask();
                var secondTask = callback(context, state).AsTask();
                await Task.WhenAll(firstTask, secondTask);
                var first = await firstTask;
                var second = await secondTask;
                return second.Exception != null ? second : first;
            }
            return await callback(context, state); // later rounds: single, sequential, no overlap
        }
    }

    private sealed class ConcurrentOnFirstRoundStrategyOptions : ResilienceStrategyOptions { }

    [Fact]
    public async Task HandleAsync_SequentialRetryAfterEarlierConcurrentRace_IncorrectlyThrows()
    {
        var sequentialAttemptRan = false;

        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 1,
                Delay = TimeSpan.Zero,
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(),
            })
            .AddStrategy(_ => new ConcurrentOnFirstRoundStrategy(), new ConcurrentOnFirstRoundStrategyOptions())
            .Build();

        var middleware = new PollyResilienceMiddleware<object>(pipeline);
        var nextCallCount = 0;

        var ex = await Record.ExceptionAsync(() => middleware.HandleAsync(new object(), async () =>
        {
            var call = Interlocked.Increment(ref nextCallCount);
            if (call == 1) { await Task.Delay(50); } // force a genuine async gap so round 1's two
                                                        // sub-attempts actually overlap
            else { sequentialAttemptRan = true; }
        }));

        var notSupported = Assert.IsType<NotSupportedException>(ex);
        Assert.Contains("concurrent-attempt", notSupported.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(sequentialAttemptRan); // round 2's next() never got the chance to run at all
    }
}
```

Result:

```
Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1, Duration: 2.9s - Benzene.Test.dll (net10.0)
```

(The test asserts the *buggy* behavior — a `NotSupportedException` thrown against round 2's purely
sequential attempt — so "Passed" here means the bug reproduces.) The exact exception observed:

```
System.NotSupportedException: A concurrent-attempt resilience strategy (e.g. a custom hedge) is not
supported by PollyResilienceMiddleware<TContext>: attempts share the message's pipeline, context, and
ambient cancellation token - run attempts sequentially, or hedge at a different layer.
```

...thrown against an attempt (`next` call #2/round 2) that never overlapped with anything — Retry's own
single, sequential retry attempt after round 1's rejected race.

### Why this clears the bar (not a nit)

- It's a genuine correctness bug in fresh code, not a style nit: the guard's entire job is to
  fail *safely* on a concurrent attempt without corrupting anything; instead, one concurrent-attempt
  detection **poisons every later attempt in the same call**, including perfectly safe sequential ones.
- The failure mode is a false-positive rejection of legitimate work, dressed up as the exact same
  "unsupported concurrency" error a real bug would produce — actively misleading whoever debugs it,
  since the reported symptom ("concurrent attempt") does not match the actual, later, non-concurrent
  cause.
- The scenario (hedge-like custom strategy nested inside a `Retry`) is squarely what the round-16 fix's
  own new cookbook section ("why concurrent-attempt strategies don't fit this design") anticipates
  someone reaching for despite the warning — it isn't a contrived edge case.

### Recommendation

**REQUEST CHANGES**, loop in `infrastructure-product-owner` (package owner). The minimal fix is to move
`Interlocked.Increment` and the guard check *inside* the `try`, or to decrement in a `finally` that
wraps the guard check too (i.e., pair every increment — including the one that trips the guard — with
exactly one decrement), so the leaked-counter state can never survive the failed attempt. A regression
test asserting a *second*, later, purely sequential attempt after a rejected concurrent one still
succeeds should accompany the fix — the current `PollyResilienceMiddlewareConcurrentAttemptGuardTest`
suite has no coverage of "what happens on the NEXT attempt after the guard fires once."

---

## Finding 2 (headline) — `MicrosoftServiceResolverAdapter.Dispose()`/`MicrosoftServiceResolverFactory
.Dispose()`'s deliberately-unbounded async bridge (#266) can deadlock the calling thread forever under
an ambient, single-thread-affinity `SynchronizationContext`

**Severity: high — an unbounded hang, not merely a slow disposal.** `work/outstanding-bugs.md`'s #266
entry explicitly reasons about the bounded-vs-unbounded tradeoff ("abandoning it early would silently
leak resources by design... this now matches Autofac's own unbounded blocking semantics") but never
considers — and the round-16 review that produced the fix never tested — whether the unbounded
`.AsTask().GetAwaiter().GetResult()` bridge can hang the thread **permanently**, not just for a long
time, under a completely ordinary hosting condition it doesn't control: an ambient
`SynchronizationContext` that requires the very thread now blocked in `GetResult()` to pump the
continuation that would let it finish.

### The defect

```csharp
// src/Benzene.Microsoft.Dependencies/MicrosoftServiceResolverAdapter.cs
public void Dispose()
{
    if (_scope is IAsyncDisposable asyncDisposableScope)
    {
        asyncDisposableScope.DisposeAsync().AsTask().GetAwaiter().GetResult(); // unbounded, blocking
    }
    else
    {
        _scope?.Dispose();
    }
}
```

```csharp
// src/Benzene.Microsoft.Dependencies/MicrosoftServiceResolverFactory.cs — identical shape, for the
// WHOLE ROOT PROVIDER (every container-owned singleton), not just one message's scope
public void Dispose()
{
    if (!_ownsServiceProvider) return;
    if (_serviceProvider is IAsyncDisposable asyncDisposable)
    {
        asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    else if (_serviceProvider is IDisposable disposable) { disposable.Dispose(); }
}
```

`asyncDisposableScope`/`_serviceProvider`'s `DisposeAsync()` is, in general, **the user's own code**
(a container-owned scoped/singleton service's `IAsyncDisposable.DisposeAsync()`, or MS DI's own scope
disposal iterating over such a service). If that method's own body contains an `await` on something
that doesn't complete synchronously, and does **not** call `.ConfigureAwait(false)` (the overwhelming
majority of application code doesn't — it's a caller-side idiom, and this is deep in someone else's
code, not Benzene's), that `await` captures `SynchronizationContext.Current` and posts its continuation
back to it. If that context requires the *exact* thread currently blocked in `GetResult()` to run the
posted callback (a single-thread-affinity context — the same shape as WinForms'/WPF's own message-loop
context, or Blazor Server's per-circuit renderer context; neither is deprecated or unrealistic for a
process that might host a Benzene worker or test runner alongside them), that callback can **never**
run, because the only thread allowed to run it is busy inside `GetResult()` waiting for it. This is the
textbook "sync-over-async" deadlock (Stephen Cleary's "Don't Block on Async Code" is the canonical
writeup), and it is **unbounded** here — unlike every other disposal bridge in this codebase
(`RedisCacheService.Dispose()`, `MeshAnnouncer.Dispose()`,
`InternallyOwnedRateLimiterHolder<TContext>.Dispose()`), which all use a **bounded** `.Wait(TimeSpan
.FromSeconds(5))` specifically so a stuck disposal degrades to "leaked, but the thread keeps going,"
not "the thread — and, for `MicrosoftServiceResolverFactory`, the whole host's shutdown — is gone
forever."

### Verified reproduction

```csharp
// test/Benzene.Core.Test/Core/Core/DI/MicrosoftServiceResolverAdapterSyncContextDeadlockRedTest.cs
// (written, run, deleted - not committed; reproduced here in full)

public class MicrosoftServiceResolverAdapterSyncContextDeadlockRedTest
{
    // Single-thread-affinity SynchronizationContext, the same shape (not implementation) as
    // WinForms'/WPF's message-loop context and Blazor Server's per-circuit renderer context:
    // Post() just enqueues - nothing ever dequeues unless the owning thread explicitly pumps.
    private sealed class SingleThreadAffinitySynchronizationContext : SynchronizationContext
    {
        public readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> Queue = new();
        public override void Post(SendOrPostCallback d, object? state) => Queue.Add((d, state));
    }

    // Bypasses the real MS DI container to isolate exactly the branch under test.
    private sealed class FakeAsyncDisposableScope : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider { get; } = new ServiceCollection().BuildServiceProvider();
        public async ValueTask DisposeAsync()
        {
            await Task.Delay(20); // no ConfigureAwait(false) - deliberately ordinary application code
        }
        public void Dispose() { }
    }

    [Fact]
    public void Dispose_ScopeDisposeAsyncCapturesAmbientSyncContext_DeadlocksCallingThreadForever()
    {
        Exception? threadException = null;
        var disposeReturned = false;

        var thread = new Thread(() =>
        {
            try
            {
                var syncContext = new SingleThreadAffinitySynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(syncContext);
                var adapter = new MicrosoftServiceResolverAdapter(new FakeAsyncDisposableScope());
                adapter.Dispose(); // the call under test
                disposeReturned = true;
            }
            catch (Exception ex) { threadException = ex; }
        });
        thread.IsBackground = true; // don't block the test process from exiting if this truly hangs
        thread.Start();

        var joined = thread.Join(TimeSpan.FromSeconds(3));

        Assert.Null(threadException);
        Assert.False(joined, "expected Dispose() to hang forever, not return within the timeout");
        Assert.False(disposeReturned);
    }
}
```

Result:

```
Passed Benzene.Test.Core.Core.DI.MicrosoftServiceResolverAdapterSyncContextDeadlockRedTest
  .Dispose_ScopeDisposeAsyncCapturesAmbientSyncContext_DeadlocksCallingThreadForever [3 s]
```

(Again, "Passed" means the deadlock reproduced — the thread did not return within the 3s timeout.
`thread.IsBackground = true` was used deliberately so this test itself couldn't hang the CI process;
in the real bug, nothing plays that role — the disposing thread is gone for good.)

### Why this is a genuinely new risk, not the accepted #266 tradeoff restated

The #266 writeup's own risk analysis is entirely about *how long* to wait, weighing "abandon too early
and leak" against "wait too long and stall shutdown" — it never considers that the wait can be
**infinite** through no fault of a slow-but-eventually-completing disposal, purely because of *where*
the blocking call happens to run. Every other disposal bridge in this codebase chose the bounded
pattern specifically to cap this risk; #266's own two call sites are the only two that chose unbounded,
and are therefore the only two exposed to a true hang rather than a bounded stall.

Blast radius differs meaningfully between the two call sites:
- `MicrosoftServiceResolverAdapter.Dispose()` — hangs the thread that tears down **one message's**
  per-request DI scope. Depending on the host's dispatch model this can hang one in-flight
  request/message forever (a leaked thread, not a crashed process) — bad, but contained.
- `MicrosoftServiceResolverFactory.Dispose()` — hangs the thread disposing the **whole root DI
  container** (every registered singleton, app-wide), which is typically called once during process/
  host shutdown. A hang here can mean the **entire application never finishes shutting down**.

### Recommendation

**REQUEST CHANGES**, loop in `infrastructure-product-owner`/`core-product-owner` (this is the shared
disposal-architecture decision from #266, not a one-package call). This does **not** need to become the
bounded-5s pattern (that would reopen the exact "abandon a user's own cleanup mid-way" concern #266's
author already reasoned through) — the standard, narrower fix is to prevent the blocking call itself
from ever observing an ambient `SynchronizationContext` in the first place, e.g.:

```csharp
var previous = SynchronizationContext.Current;
SynchronizationContext.SetSynchronizationContext(null);
try { asyncDisposableScope.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
finally { SynchronizationContext.SetSynchronizationContext(previous); }
```

(the well-known "AsyncHelper.RunSync" mitigation), or offloading the whole disposal onto a thread-pool
thread via `Task.Run(...)` before blocking on it. Either preserves the "never abandon the user's own
disposal" design goal while removing the specific deadlock vector proven above. A permanent regression
test using the technique above (a controllable single-thread-affinity `SynchronizationContext`, no real
UI framework or test-runner dependency needed) should accompany the fix.

---

## Finding 3 — `MeshCollectorStore`'s new per-version `Descriptors` dictionary (#251) has no eviction
policy at all; a service that legitimately re-registers under a new `ServiceVersion` on every deploy
(an entirely ordinary CI/CD practice) grows it — and the `HashMatches` scan cost derived from it —
without bound for the entire lifetime of the collector process

**Severity: medium-high — unbounded memory growth plus a compounding per-query CPU cost, in code that
exists specifically to support "two versions live side by side," i.e. an inherently multi-version
workload.** `src/Benzene.Mesh.Collector/MeshCollectorStore.cs`'s `ServiceState.Descriptors` dictionary
is new in round 16's #251 fix (spec §2.4 side-by-side-version support). Every other bounded structure
in this same file has an explicit eviction policy: `_issues` is capped at `maxIssues` and evicts the
least-recently-seen entry when full; `_ring` is a fixed-capacity circular buffer. `Descriptors` has
neither a cap nor any notion of "this version has had no live instance heartbeat in N minutes, retire
it" — nothing in `Register`, `Heartbeat`, or anywhere else in the file ever removes an entry from it.

### The defect

```csharp
// src/Benzene.Mesh.Collector/MeshCollectorStore.cs
private class ServiceState
{
    public readonly Dictionary<string, MeshServiceDescriptor> Descriptors = new(); // keyed by version
    ...
}

public void Register(MeshServiceDescriptor descriptor)
{
    var versionKey = descriptor.ServiceVersion ?? string.Empty;
    lock (_lock)
    {
        var state = EnsureService(descriptor.Service);
        if (state.Descriptors.TryGetValue(versionKey, out var previous))
        {
            RetractEdges(previous, descriptor.Service); // only replaces the SAME version key
        }
        state.Descriptors[versionKey] = descriptor;      // a NEW version key is just ADDED, forever
        ...
    }
}
```

`src/Benzene.CloudService/MeshAnnouncer.cs` confirms the real-world calling pattern: each process
instance calls `Register` **exactly once** at startup (retried only until it succeeds), then switches
permanently to heartbeating. So every distinct `(service, ServiceVersion)` pair that has *ever*
registered against a given long-running collector process accumulates one permanent `Descriptors`
entry — including versions whose every instance has long since been redeployed away and stopped
heartbeating. A service that deploys under a fresh `ServiceVersion` string on every release (git-SHA or
build-number versioning is common practice, and is exactly what CI/CD encourages) adds one permanent,
un-collectible entry per historical deploy, for the entire uptime of the collector process — which,
per this same file's own `StartedAtUtc` doc comment, is meant to be a long-running, cumulative-since-
start process, not something recycled per deploy.

This compounds into a real (not "one more dictionary lookup") per-query cost too:
`HashMatches`(`src/Benzene.Mesh.Collector/MeshCollectorStore.cs:549`) — called once per instance in
every `Service(name)` query — now scans **every** `Descriptors.Values` entry the service has ever had,
not just its live ones:

```csharp
private static bool? HashMatches(ServiceState state, string? reportedHash)
{
    ...
    var knownHashes = state.Descriptors.Values.Select(d => d.DescriptorHash).Where(h => h != null).ToList();
    return knownHashes.Count == 0 ? null : knownHashes.Contains(reportedHash);
}
```

Before #251 this was an O(1) comparison against the single current descriptor; it is now O(v) where v
is the count of every version the service has ever registered — which, given the growth bug above, is
monotonically increasing for the life of the process, not bounded by the number of *live* versions.

### Verified reproduction

```csharp
// test/Benzene.Mesh.Test/ScratchDescriptorVersionUnboundedGrowthTest.cs
// (written, run, deleted - not committed; reproduced here in full)

public class ScratchDescriptorVersionUnboundedGrowthTest
{
    private static int DescriptorCountFor(MeshCollectorStore store, string serviceName)
    {
        var servicesField = typeof(MeshCollectorStore)
            .GetField("_services", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var services = (IDictionary)servicesField.GetValue(store)!;
        var state = services[serviceName]!;
        var descriptorsField = state.GetType()
            .GetField("Descriptors", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)!;
        var descriptors = (IDictionary)descriptorsField.GetValue(state)!;
        return descriptors.Count;
    }

    [Fact]
    public void Register_SameServiceManyDistinctVersions_DescriptorsDictionaryGrowsWithoutBound()
    {
        var store = new MeshCollectorStore();
        const int deployCount = 5000; // simulates 5,000 CI/CD deploys of one logical service
        for (var i = 0; i < deployCount; i++)
        {
            store.Register(new MeshServiceDescriptor
            {
                Service = "orders-api",
                ServiceVersion = $"deploy-{i}",
                DescriptorHash = $"hash-{i}",
            });
        }

        Assert.Equal(deployCount, DescriptorCountFor(store, "orders-api"));
    }
}
```

Result:

```
Passed Benzene.Test.ScratchDescriptorVersionUnboundedGrowthTest
  .Register_SameServiceManyDistinctVersions_DescriptorsDictionaryGrowsWithoutBound [9 ms]
```

All 5,000 historical versions were retained — none were ever evicted.

### Recommendation

**REQUEST CHANGES**, loop in `infrastructure-product-owner`/whoever owns `Benzene.Mesh.Collector`'s
spec alignment. This needs a retirement policy analogous to `_issues`'s bounded-with-eviction pattern —
the natural signal is already present in the data model: a version with no live `Heartbeat` (i.e., no
`InstanceState` in `state.Instances` reporting that `DescriptorHash`/version) for longer than some
TTL is a legitimate candidate for eviction from `Descriptors`, mirroring how a real fleet naturally
retires an old canary once its instances are gone. This is a design decision (what TTL, whether to key
eviction off instance-heartbeat-absence vs. a simple max-versions-per-service cap like `_issues`'s
`maxIssues`) for whoever owns the collector's spec conformance, not something to fix unilaterally here.

### Benchmarking-gap note

No `BenchmarkDotNet` coverage exists for `Benzene.Mesh.Collector` at all (the suite in
`benchmarks/Benzene.Benchmarks` covers only `MiddlewarePipeline<TContext>.HandleAsync` and
`MultiSerializerOptionsRequestMapper<TContext>.GetBody<T>` per its own README). This one clears the
"more than one more dictionary lookup" bar the brief set: `HashMatches`'s complexity class genuinely
changed (O(1) → O(v), where v grows unboundedly per Finding 3 above), which is a real algorithmic
regression, not a hypothetical one — worth a benchmark once the retirement policy above is designed, so
the fix can be verified to actually bound v rather than just capping memory while leaving the scan cost
unbounded.

---

## Finding 4 (headline) — `CompositeBenzeneWorker.StartAsync` (never previously reviewed) silently and
permanently swallows a sibling worker's startup failure — no rollback, no rethrow, no host-fault signal
— whenever the composite also contains a worker whose `StartAsync` runs its full lifetime inline
(exactly `SqsConsumer`'s own documented shape)

**Severity: high — a documented reliability guarantee (“an unhandled worker fault stops the whole
host”) is completely defeated, silently, for a completely ordinary worker combination.**
`src/Benzene.SelfHost/CompositeBenzeneWorker.cs` was flagged in this round's brief as unreviewed
(round 16's own performance reviewer touched only `StopAsync`). `StartAsync`'s own rollback design is
carefully reasoned about in its doc comment — but that reasoning implicitly assumes every started
worker's task *eventually completes* (successfully or by faulting) so `Task.WhenAll` can observe it.
`src/Benzene.HostedService/BenzeneHostedServiceStartup.cs`'s own doc comment names the counterexample
already present in this codebase: *"some implementations (`BenzeneKafkaWorker`) already background
their run loop and return promptly, but others (`SqsConsumer`) run their loop directly on that task,
which doesn't return until cancelled."*

### The defect

```csharp
// src/Benzene.SelfHost/CompositeBenzeneWorker.cs
public async Task StartAsync(CancellationToken cancellationToken)
{
    var started = _workers.Select(x => (worker: x, task: SafeStart(x, cancellationToken))).ToArray();
    try
    {
        await Task.WhenAll(started.Select(x => x.task));   // <-- waits for EVERY task, no shortcut
    }
    catch
    {
        foreach (var (worker, task) in started)
        {
            if (task.IsCompletedSuccessfully) { try { await worker.StopAsync(cancellationToken); } catch { } }
        }
        throw;
    }
}
```

`Task.WhenAll` only completes once **every** input task completes — it does not shortcut on the first
fault. `SqsConsumer.StartAsync` (`src/Benzene.Aws.Sqs/Consumer/SqsConsumer.cs:67`) runs its entire poll
loop (`do { ... } while (!cancellationToken.IsCancellationRequested)`) directly inline, so its task
never completes until the worker is told to stop — by design, and already documented as such elsewhere
in this codebase. If a composite contains `SqsConsumer` (or anything else with this shape) **and** a
separate worker whose `StartAsync` fails, the failing worker's task faults immediately, but
`Task.WhenAll` still waits — forever — for the long-running worker's task, which has no reason to ever
complete on its own. Consequently:

- The `catch` block — the entire rollback mechanism `StartAsync`'s own doc comment describes at length
  — **never runs**. The worker that already started successfully is never rolled back; more importantly
  the failure is never rethrown.
- `CompositeBenzeneWorker.StartAsync`'s returned task never completes.
- `BenzeneHostedServiceAdapter.ObserveFault` (`src/Benzene.HostedService/BenzeneHostedServiceStartup.cs
  :78`) — which `await`s exactly this task to log `LogCritical` and call
  `IHostApplicationLifetime.StopApplication()`, explicitly "match[ing] `BackgroundService`'s modern
  default... an unhandled worker fault stops the whole host" — never fires either, because the task it
  is awaiting never completes.
- `BenzeneHostedServiceAdapter.StartAsync` returns promptly regardless (by design, for the
  long-running-task case), so the **host believes startup succeeded**.

Net effect: a deployment with, say, an SQS consumer and a Kafka consumer in the same composite, where
the Kafka consumer fails to start (bad broker config, auth failure, whatever), ends up running
**forever** with only the SQS consumer actually processing messages, the Kafka consumer silently dead,
zero log output, zero host-stop, and no task anyone is awaiting will ever tell them — the exact opposite
of "failure degrades, it doesn't cascade" (it doesn't cascade, but it also doesn't fail loud; it fails
*invisible*, indefinitely).

### Verified reproduction

```csharp
// test/Benzene.Core.Test/Hosting/CompositeBenzeneWorkerLongRunningStartRedTest.cs
// (written, run, deleted - not committed; reproduced here in full)

public class CompositeBenzeneWorkerLongRunningStartRedTest
{
    // Mirrors SqsConsumer.StartAsync's actual shape: runs inline, only completes on cancellation.
    private sealed class LongRunningWorker : IBenzeneWorker
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource();
            using var registration = cancellationToken.Register(() => tcs.TrySetResult());
            await tcs.Task;
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ImmediatelyFailingWorker : IBenzeneWorker
    {
        public Task StartAsync(CancellationToken cancellationToken)
            => Task.FromException(new InvalidOperationException("bad connection string"));
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task StartAsync_LongRunningSiblingFailsToStart_FailureIsPermanentlyHiddenBehindNeverCompletingWhenAll()
    {
        var composite = new CompositeBenzeneWorker(new IBenzeneWorker[] { new LongRunningWorker(), new ImmediatelyFailingWorker() });

        // A real deployment never cancels this token during startup - mirrors what
        // BenzeneHostedServiceAdapter.StartAsync actually passes in practice.
        var compositeStartTask = composite.StartAsync(CancellationToken.None);

        var completedWithinTimeout =
            await Task.WhenAny(compositeStartTask, Task.Delay(TimeSpan.FromSeconds(2))) == compositeStartTask;

        Assert.False(completedWithinTimeout,
            "expected CompositeBenzeneWorker.StartAsync to hang forever, silently swallowing the " +
            "sibling worker's startup failure, when a long-running (SqsConsumer-shaped) worker is " +
            "also in the composite");
    }
}
```

Result:

```
Passed Benzene.Test.Hosting.CompositeBenzeneWorkerLongRunningStartRedTest
  .StartAsync_LongRunningSiblingFailsToStart_FailureIsPermanentlyHiddenBehindNeverCompletingWhenAll [2 s]
```

("Passed" means the composite's `StartAsync` did not return within the timeout, proving the hang and
the swallowed failure.) The existing `test/Benzene.Core.Test/Hosting/CompositeBenzeneWorkerTest.cs`
only exercises `FakeWorker`s whose `StartAsync` returns `Task.CompletedTask` immediately or throws
synchronously — it has no coverage at all of the "runs for its full lifetime inline" shape that
`SqsConsumer` (a real, shipped worker) actually has, which is exactly why this gap survived.

### Recommendation

**REQUEST CHANGES**, loop in `core-product-owner`/whoever owns `Benzene.SelfHost` and the
`IBenzeneWorker` contract — this is a composition-semantics question (should `StartAsync`'s "wait for
everyone" model even apply when a worker's own contract permits "runs forever"?), not a one-line fix.
Two directions worth considering, either of which needs a design call rather than a unilateral change
here:
1. Race the failure instead of waiting for everyone: use something like `Task.WhenAll` for the
   successfully-completing subset while separately watching for **any** task faulting (e.g. via a
   `TaskCompletionSource` signalled by a per-task fault continuation), so a fault triggers rollback
   immediately regardless of whether a sibling's task will ever complete on its own.
2. Document (and enforce, if feasible) that `IBenzeneWorker.StartAsync` must not run its full lifetime
   inline for anything composed via `CompositeBenzeneWorker` — pushing the "background and return
   promptly" responsibility down to each worker instead of the composite, mirroring what
   `BenzeneHostedServiceAdapter` already does for a *single* top-level worker. This would also require
   revisiting `SqsConsumer`'s own contract, which is a bigger, cross-cutting change.

Either way, a permanent regression test using the `LongRunningWorker`/`ImmediatelyFailingWorker` shapes
above should be added to `CompositeBenzeneWorkerTest.cs` alongside the fix, since the existing suite's
blind spot (every fake worker's `StartAsync` completing synchronously) is precisely why this survived.

---

## Other areas swept — no additional finding clearing the bar

- **`Benzene.Testing`** (4 files: `BenzeneTestHost.cs`, `MessageBuilder.cs`,
  `MessageBuilderExtensions.cs`, `HttpBuilder.cs`) — checked specifically for the shared-mutable-state/
  test-pollution class of bug the brief called out. Every `static class` here is a stateless factory
  namespace (`Create(...)` methods returning fresh builder instances); no mutable static field anywhere
  in the package. No finding — this is a genuinely clean, low-risk area, not a case where I stopped
  looking early.
- **`Benzene.Descriptor`** — `ServiceLoadContext` (`src/Benzene.Descriptor/ServiceLoadContext.cs`) is a
  non-collectible (`isCollectible: false`) `AssemblyLoadContext` created fresh, and never unloaded, on
  every `DescriptorEmitter.Emit(...)` call. In the shipped CLI (`Program.cs`), this is a one-shot
  process that exits immediately after, so the "leak" is irrelevant. `DescriptorEmitter.Emit` is also
  called directly in-process from `test/Benzene.Core.Test/Autogen/Descriptor/DescriptorEmitterTest.cs`
  (6 call sites) for testability — each call permanently loads one more non-collectible ALC + assembly
  for the rest of that test process's lifetime. At 6 calls in one file this is real but far too small
  to matter (nowhere near "unbounded" or "per-message"), so it does not clear this round's bar for a
  filed finding; noting it here only so a future reviewer doesn't need to re-derive that it's bounded
  and low-risk before moving on.
- **`Benzene.CloudService.Probe`** — `CloudServiceProbe.RunAsync` performs 4 sequential (not
  concurrent) HTTP calls against the target service being probed; each honors both the caller's
  `CancellationToken` and the supplied `HttpClient`'s own per-request `Timeout`, correctly
  distinguished via `catch (OperationCanceledException) when (!ct.IsCancellationRequested)` (matching
  the class's own documented contract: "an externally-requested cancellation propagates, an internal
  per-request timeout does not"). This is a rarely-invoked diagnostic tool, not a hot path; no
  reliability or performance gap found.
- **`Benzene.SelfHost.CompositeBenzeneWorker.StopAsync`** — re-confirmed round 16's own conclusion
  (bare `Task.WhenAll(_workers.Select(x => x.StopAsync(...)))`, no per-worker isolation): every shipped
  `IBenzeneWorker.StopAsync` is `async Task` (one exception, `SqsConsumer.StopAsync`, is a trivial
  `return Task.CompletedTask`), so a synchronous throw can't escape the `.Select(...).ToArray()`
  enumeration — this is still not a bug, and separately, `StopAsync`'s reuse of the composite's own
  `cancellationToken` parameter for a rollback call inside `StartAsync`'s `catch` block was considered
  (does reusing a possibly-already-cancelled token for "please clean up what you started" abbreviate a
  worker's own cleanup?) but not filed: the concrete token flow (`BenzeneHostedServiceAdapter.StartAsync`
  links a fresh CTS off the *host's own startup token*, which in practice is essentially never cancelled
  absent an explicitly configured host startup timeout) makes this a narrow, compounding-conditions
  scenario rather than a directly reachable one — noted here as a secondary observation, not a filed
  finding, in case a future change to the host's startup-token lifecycle makes it more reachable.
- **`IDisposable`/`IAsyncDisposable`-declared-alone sweep, repeated** — `grep -rln "IAsyncDisposable"
  src/ --include=*.cs | xargs grep -L "IDisposable"` (i.e., "mentions `IAsyncDisposable` anywhere in the
  file but never `IDisposable`") returns exactly the same three files round 16 already reviewed and
  cleared: `RabbitMqConnectionProvider`, `RateLimitingMiddleware<TContext>`,
  `PartitionedRateLimitingMiddleware<TContext>` — all already assessed as not DI-registered, so not at
  risk of the crash this bug class describes. **Zero new instances** introduced by round 16's own
  changes (`RedisCacheService` now correctly implements both; `MicrosoftServiceResolverAdapter`/
  `MicrosoftServiceResolverFactory` both already implemented `IDisposable` before and after #266 — their
  fix only changed what `Dispose()` does internally, not the type's declared interface surface).
  A targeted pass over `src/Benzene.Mesh.*` and `src/Benzene.GoogleCloud.*` specifically (per the
  brief's explicit ask) found **zero** classes declaring either interface at all in `Benzene.GoogleCloud
  .*`, and only the two pre-existing interface *definitions* (`IMeshTraceExporter`/`IMeshIssueExporter`)
  mentioning `IAsyncDisposable` in `Benzene.Mesh.Wire` — no concrete class in either area declares one
  without the other. Sweep is clean.

## Summary

| # | Finding | Severity | Status |
|---|---------|----------|--------|
| 1 | `PollyResilienceMiddleware<TContext>`'s round-16 re-entrancy guard (#267) leaks its counter on the exact path it exists to protect, then misfires against a later, fully sequential attempt in the same call | High — reliability | New, proven by xUnit repro |
| 2 | `MicrosoftServiceResolverAdapter`/`MicrosoftServiceResolverFactory`'s round-16 unbounded async-disposal bridge (#266) can deadlock the calling thread (and, for the factory, whole-app shutdown) forever under an ambient single-thread-affinity `SynchronizationContext` | High — reliability (unbounded hang) | New, proven by xUnit repro |
| 3 | `MeshCollectorStore`'s round-16 per-version `Descriptors` dictionary (#251) has no eviction policy; grows without bound across the collector's lifetime under ordinary CI/CD re-deploy practice, and turns `HashMatches` from O(1) into an unboundedly-growing O(v) per-query scan | Medium-high — memory + CPU growth | New, proven by xUnit repro |
| 4 | `CompositeBenzeneWorker.StartAsync` (never previously reviewed) silently and permanently swallows a sibling worker's startup failure whenever the composite also contains an `SqsConsumer`-shaped ("runs its full lifetime inline") worker — no rollback, no rethrow, no host-stop | High — reliability (silent, permanent failure) | New, proven by xUnit repro |

All four findings are in code this round's brief specifically called out as either freshly-landed
(#266, #251, #267) or never previously reviewed (`Benzene.SelfHost`'s `StartAsync`) — consistent with
the pattern this whole review series has established: fresh, not-yet-battle-tested code is the
highest-yield place to look. `Benzene.Testing`, `Benzene.Descriptor`, and `Benzene.CloudService.Probe`
were all genuinely swept (not skipped) and did not clear the bar for a filed finding; that is reported
plainly above rather than manufacturing findings to fill out the checklist.

**Recommendation overall: REQUEST CHANGES** on Findings 1, 2, and 4 (all high severity, all proven, all
in fresh or first-reviewed code); Finding 3 also warrants REQUEST CHANGES but is more clearly a design
decision (retirement/TTL policy) than a one-line fix. Loop in `infrastructure-product-owner` for
Findings 1-3 (their packages: `Benzene.Resilience.Polly`, `Benzene.Microsoft.Dependencies`,
`Benzene.Mesh.Collector`) and `core-product-owner` for Finding 4 (`Benzene.SelfHost`'s
`IBenzeneWorker`/`CompositeBenzeneWorker` composition contract) before any fix lands, per this review
role's own charter of advising rather than overriding a domain owner's design.
