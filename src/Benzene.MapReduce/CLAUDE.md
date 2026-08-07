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
