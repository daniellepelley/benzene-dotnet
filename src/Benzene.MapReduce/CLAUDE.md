# Benzene.MapReduce

The thin, supported form of scatter-gather (map-reduce) over Benzene's topic-routed sender. Benzene
has no built-in scatter-gather primitive because it composes from parts already present; this package
is that composition, packaged so apps don't hand-roll it each time.

## Shape

- `IBenzeneMessageSender.ScatterGatherAsync<TShard, TPartial, TAccum>(topic, shards, seed, reduce, options?)`
  - **map:** one `SendAsync` per shard, run concurrently through `BoundedFanOut.WhenAllAsync` (bounded
    by `MaxDegreeOfParallelism`, results in source order). On AWS each shard resolves to a
    Lambda-to-Lambda invoke through the routing table.
  - **reduce:** an app-owned fold over the *successful* partial responses.
- `ScatterGatherOptions` — `MaxDegreeOfParallelism` (null = unbounded) and `PartialFailureMode`.
- `PartialFailureMode` — `ThrowOnAnyFailure` (default; throws `ScatterGatherPartialFailureException`
  if any shard failed, so an incomplete total is never mistaken for complete) or `BestEffort`
  (reduce over successes, expose the failed shards on the result so reduced coverage *says so*).
- `ScatterGatherResult<TShard, TAccum>` — `Value`, `FailedShards`, `IsComplete`.

## Notes

- A shard "fails" if its worker returns an unsuccessful result **or** throws — either way the policy
  above decides what that means; a failed shard is never silently folded into the total.
- No third-party dependencies — just `Benzene.Clients` (the sender) + `Benzene.Core.Middleware`
  (`BoundedFanOut`). Keep it minimal: one method, options, and a result — not a framework.
- For very large fan-outs, shard hierarchically (a coordinator scatters to partition workers that each
  scatter and reduce locally) — the same call at each level.
- An empty `shards` collection reduces cleanly to `seed`, reports `IsComplete == true`, and never
  calls the sender at all. The `reduce` delegate itself throwing (it runs synchronously, after every
  shard has already completed, outside the per-shard try/catch) propagates that exception directly —
  it is not folded into `FailedShards`/`ScatterGatherPartialFailureException`, since every worker call
  genuinely succeeded.

## Tests
`test/Benzene.Core.Test/MapReduce/ScatterGatherTest.cs` — all-succeed reduces to the sum;
`ThrowOnAnyFailure`/`BestEffort` failure handling (result and thrown-exception shards, including a
`OperationCanceledException` propagating as cancellation rather than a reported failure, and five
concurrently-thrown distinct exception types each individually preserved); an empty `shards`
collection (`#259`); the `reduce` delegate itself throwing mid-fold (`#259`); and
`MaxDegreeOfParallelism` actually bounding how many worker calls are in flight at once, end to end
through `ScatterGatherAsync` — deterministic via a shared gate every admitted worker parks on, not a
timing-based approximation (`#259`).
