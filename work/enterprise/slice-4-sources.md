# Slice 4 — New sources: Prometheus/OTel and Elasticsearch

**Status: DESIGN FIRST — do not build from this brief.**
**Depends on:** slice 1 (the config catalog is where a new source becomes reachable).
**Owner of the decisions below:** `observability-product-owner`, with `mesh-product-owner` on scope.

## Why this brief exists, and why it stops short

Every other slice in this set is a promotion of code that already exists into configuration. This one
is not: it adds genuinely new adapters, and the shape of those adapters depends on questions nobody
has answered yet. A brief detailed enough to hand to an implementer would have to invent those
answers, and an invented answer here is expensive — a usage source with the wrong query shape is not
a bug you find in a test, it is a number on a dashboard that is quietly wrong.

So this file enumerates the decisions, names their owner, and stops. **If you have been handed this
slice to build, the correct first action is to get the questions in §2 answered, not to start
writing an adapter.**

## 1. What is actually being asked for

From the research: an enterprise's observability stack may not be CloudWatch. The two named
candidates are *"some sort of OTel store"* and *Elasticsearch*. Today the shipped usage sources are
CloudWatch and Application Insights, and the shipped trace sources are X-Ray, Tempo and Jaeger.

Note that "a new source" is ambiguous in a way that matters: the mesh has **three** distinct source
ports a backend could plug into, and Elasticsearch could plausibly serve any of them.

| Port | What it answers | Shipped implementations |
|---|---|---|
| `IMeshUsageSource` | how often was each topic exercised, over a window | CloudWatch, Application Insights, in-memory collector |
| `IMeshTraceSource` | individual flows, correlation, recent traces | X-Ray, Tempo, Jaeger |
| *(none — does not exist)* | issues: what is failing, grouped and counted | in-memory collector only |

## 2. The decisions that must be made first

**D1 — Which port(s) does each new backend implement?** "Add Elasticsearch" is not a buildable
instruction. Is it a usage source (aggregating counts from log/metric documents), a trace source
(if the customer ships OTel traces to Elastic APM), or both? Same question for the OTel store —
"OTel store" most likely means Prometheus-compatible metrics *and/or* an OTLP trace backend, which
are two different adapters.

**D2 — The cross-port usage metric name.** `CloudWatchUsageSource` reads back a counter named
`benzene.messages.processed` with dimensions `topic`/`transport`/`result`. **That name appears
nowhere in the specification** — it is a .NET OTel convention. Any Prometheus/OTel usage source
reads the same counter, so if benzene-go, benzene-typescript or benzene-python emit a differently
named counter, a metrics-store usage source works for .NET services and silently reports nothing for
the others. This must be settled and written down somewhere authoritative *before* a second
metrics-store adapter is written, or the adapter bakes in the ambiguity.

The research document's position: this is **not** mesh-spec material (the spec-native usage signal is
TraceEvent counting), but the convention needs a documented home. Deciding where that home is, is
part of this slice's design work.

**D3 — Does the issue port get built?** If Elasticsearch is to serve issues, `IMeshIssueSource` has
to exist first — it does not today, and `CompositeMeshFleetReadModel` marks issues as a permanently
missing feed on every non-push deployment. That is a design task of its own (what does an issue feed
look like when it is queried rather than pushed? fingerprinting is currently a collector-side
concern). Scope it in or out explicitly; do not let it arrive by accident.

**D4 — Authentication to the backend.** CloudWatch and X-Ray use the ambient AWS credential chain.
Elasticsearch and a self-hosted Prometheus do not have an ambient credential model — they need an
API key, a bearer token, or basic credentials. Per the house rules, **those are secrets and must not
live in `mesh.json`**, so the config schema needs a documented convention for referencing a secret
from the environment. Slice 1 does not solve this, because none of its sources need it. This is the
first slice that does, and the convention it picks will be the one every later source inherits.

**D5 — The standing unverified caveat.** The Tempo adapter's metric and label names are a documented
convention that has **never been verified against a live Tempo backend**. This has been carried as a
caveat on every estimate touching Tempo. Before adding a second metrics-store adapter that will be
built by analogy with it, verify the first one. Building the second from an unverified template
doubles the exposure rather than testing it.

## 3. Shape of the work, once the decisions land

Recorded so the estimate is not re-derived, not as an instruction to build:

- One new project per backend, following the existing adapter layout (`Benzene.Mesh.Usage.*` /
  `Benzene.Mesh.Fleet.*`), each with an options class, an `Add*` extension, and a mapper type — the
  three-file shape every shipped adapter already has.
- Registration in slice 1's name→registration map, so the source is reachable as
  `"usage": [{ "source": "prometheus", ... }]` with no host code change.
- The per-source least-privilege matrix in the host README gains a row.
- Fetch isolation must be re-confirmed, not assumed: `performance-champion` flagged that as sources
  multiply under configuration, a misconfigured endpoint must degrade its own slice and never stall
  the catalog. The aggregator already isolates per-service fetches; the question is whether the same
  holds for a slow source added by config.

## 4. Do NOT

- Do not start with the adapter. Start with D1 and D2.
- Do not resolve D2 by picking a name and moving on. The whole point of the question is that the
  answer has to hold across four language ports, which means it is not a .NET decision.
- Do not add an issue source without deciding D3 deliberately — an `IMeshIssueSource` that exists
  only to satisfy one adapter will shape the port badly for every later one.
- Do not put any of this in the language-neutral spec. See the house rules in
  [`README.md`](README.md).

## 5. Report back with

A short design note in `work/` (living, per `work/README.md`) answering D1–D5, listing the projects
to be created, and naming which of them are in scope for a first increment. That note — not this
file — becomes the buildable brief.
