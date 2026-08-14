# Migration: Alpha to 1.0

Benzene is pre-1.0 (`version.txt` is currently `0.0.2`) and everything published so far is alpha.
The project's compatibility posture during this period is a **clean break, no dual-accept shim**:
when a wire shape or a public API needs to change to get the design right before 1.0, it changes
outright rather than carrying a deprecated old path alongside the new one. `CHANGELOG.md` is the
complete, chronological record of every change; this page is a narrower, task-oriented companion —
just the **breaking** API changes a consumer of the packages below needs to react to, one
before/after example per change, grouped by the feature that introduced them.

If you're only consuming Benzene through message handlers and the `BenzeneResult` factory (the
overwhelming majority of application code), most of what follows doesn't touch you — the factory
methods you already call (`BenzeneResult.NotFound(...)`, `.ValidationError(...)`, etc.) compile and
behave unchanged. This page matters if your code **reads** `IBenzeneResult.Errors` directly, or
**constructs/reads** `Benzene.Results.ProblemDetails` / the old `ErrorPayload` type by hand.

## `IBenzeneResult.Errors`: `string[]` → `IReadOnlyList<BenzeneError>`

**What changed.** `IBenzeneResult.Errors` (`Benzene.Abstractions.Results`) used to be a plain
`string[]`. It's now `IReadOnlyList<BenzeneError>`, where `BenzeneError` (also
`Benzene.Abstractions.Results`) is `{ Message, Field?, Code? }` — a message plus, when the producer
has one, the field the error belongs to and a machine-readable code a caller can branch on. This
implements the "structured result errors" design (`work/benzene-result-errors-ruling.md`) and
underpins the `errors` array in the RFC 9457 [problem document](message-result.md#problem-documents-rfc-9457)
described below.

**Who this breaks.** Only *readers* of `.Errors`. Every result-*construction* call site is unaffected
— `BenzeneResult.NotFound("message")`, `.BadRequest("message")`, `.ValidationError("message")`, and
every other `params string[] errors` factory overload still compiles unchanged; each string is
projected to a message-only `BenzeneError` internally. If your code only ever *builds* results, you
have nothing to change here.

```csharp
// Before (Errors: string[])
foreach (var message in result.Errors)
{
    logger.LogWarning(message);
}

var joined = string.Join(", ", result.Errors);
```

```csharp
// After (Errors: IReadOnlyList<BenzeneError>) — two equally valid fixes:

// 1. Read the structured shape directly, using Field/Code where you have a use for them.
foreach (var error in result.Errors)
{
    logger.LogWarning("{Message} (field={Field}, code={Code})", error.Message, error.Field, error.Code);
}

// 2. Or, if you only ever wanted the text: BenzeneError.ToString() returns Message, so a bare
// string.Join(", ", result.Errors) still compiles and produces the same output as before with no
// change at all. Where you want an explicit string[], project with the new ErrorMessages()
// extension (Benzene.Results) instead of hand-rolling a .Select(e => e.Message).
var joined = string.Join(", ", result.Errors);       // unchanged — BenzeneError.ToString() == Message
string[] messages = result.ErrorMessages();          // new: the pre-BenzeneError shape back
```

**If you implement `IBenzeneResult` yourself** (outside this repo — e.g. a hand-rolled test double or
adapter), change your `Errors` member's declared type to `IReadOnlyList<BenzeneError>`. A success (or
otherwise error-less) result's `Errors` should be an empty list, never `null`.

**Validation integrations now populate `Field`/`Code`.** If you use `Benzene.FluentValidation`,
`Benzene.DataAnnotations`, or `Benzene.JsonSchema`, a validation failure's `BenzeneError`s now carry
`Field`/`Code` (previously only `Message` existed, because there was no structured shape to put them
in). See [Fluent Validation — Structured errors on the wire](fluent-validation.md#structured-errors-on-the-wire-field-and-code)
/ [Data Annotations — Structured errors on the wire](data-annotations.md#structured-errors-on-the-wire-field-and-code)
for exactly what each integration populates.

## `Benzene.Results.ProblemDetails`: reshaped into a real RFC 9457 document; `ErrorPayload` deleted

**What changed.** The failure body every Benzene service serializes moved from a Benzene-specific
`{ status, detail }` shape (`Benzene.Results.ErrorPayload`) to a real
[RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) problem document. `ErrorPayload` is **deleted
entirely, with no shim** — its two jobs (building the failure body; the client-side deserialization
target) both moved onto `Benzene.Results.ProblemDetails`, which gained the RFC's members:

```csharp
// Before — Benzene.Results.ErrorPayload (deleted, does not exist anymore)
public class ErrorPayload
{
    public string Status { get; set; }    // the Benzene status string, e.g. "validation-error"
    public string Detail { get; set; }
}
```

```csharp
// After — Benzene.Results.ProblemDetails (evolved in place, not a new type)
public class ProblemDetails
{
    public string? Type { get; set; }          // new
    public string? Title { get; set; }         // new
    public int? Status { get; set; }           // type change: was `string` (the Benzene status) —
                                                // now the numeric HTTP status, HTTP bindings only
    public string? Detail { get; set; }        // unchanged meaning: the joined error messages
    public string? Instance { get; set; }       // new
    public string? BenzeneStatus { get; set; } // new — carries what the old `Status` string used to
    public BenzeneError[]? Errors { get; set; } // new — the result's structured errors, when present
}
```

**The one breaking *member* change: `Status` is now `int?`, not `string`.** It used to hold the
Benzene status string (e.g. `"validation-error"`); that string now lives on the new `BenzeneStatus`
member instead. `Status` is now the real RFC 9457 member: the numeric HTTP status code, present only
on an HTTP-bound response (never fabricated for an envelope over a queue or a direct invocation —
absent from the wire there, not emitted as `null`).

```csharp
// Before — Status held the Benzene status string
var problem = new ErrorPayload { Status = "validation-error", Detail = "Name is required" };
if (problem.Status == "validation-error") { /* ... */ }
```

```csharp
// After — BenzeneStatus holds the string; Status is the numeric HTTP code (int?, HTTP-only)
var problem = ProblemTypes.From(BenzeneResult.ValidationError("Name is required"));
if (problem.BenzeneStatus == BenzeneResultStatus.ValidationError) { /* ... */ }
// problem.Status is null here — ProblemTypes.From never sets it; it's filled in later, only for
// an HTTP-facing context, by the framework's own HTTP response pipeline.
```

**If you built an `ErrorPayload`/`ProblemDetails` by hand** (e.g. inside a custom
`UseExceptionHandler(...)` callback — see [Global Error Handling](cookbooks/global-error-handling.md)),
switch to `ProblemTypes.From(result)` to get the registry-backed `Type`/`Title`/`Detail`/
`BenzeneStatus`/`Errors` filled in for you, and set `Status` yourself if you're responding over HTTP:

```csharp
// Before
var body = JsonSerializer.Serialize(new ErrorPayload
{
    Status = BenzeneResultStatus.UnexpectedError,
    Detail = "An unexpected error occurred.",
});
```

```csharp
// After
var problem = ProblemTypes.From(BenzeneResult.UnexpectedError("An unexpected error occurred."));
problem.Status = 500; // HTTP responses only — set explicitly here; nothing does it for a hand-built document
var body = JsonSerializer.Serialize(problem);
```

**If you deserialized the old `{ status, detail }` shape on the client side** (e.g. a hand-rolled HTTP
client, not `Benzene.Clients`), deserialize `ProblemDetails` instead and read `BenzeneStatus` where
you used to read the old string `Status`; `Detail` is unchanged. Every Benzene-shipped client
(`Benzene.Clients.Common.ClientResultExtensions`) was updated for you — this only matters for code
you wrote yourself against the old body shape.

**Wire-level, for services outside this repo (other language ports, hand-rolled consumers).** The
failure body's `status` member changed meaning (string Benzene status → numeric HTTP status,
HTTP-only) and a new `benzeneStatus` member carries what `status` used to. A consumer that only reads
`detail` is unaffected either way — that member's meaning and content are unchanged. See the
cross-language spec's [wire contracts](https://benzene.app/docs/specification/wire-contracts.html)
for the full normative shape.

## See also

- [Message Results — Problem documents](message-result.md#problem-documents-rfc-9457) — the current
  document shape, the problem-type registry, `BenzeneResult.Problem(...)` for a handler-authored
  problem, and `GetProblem()` for reading one back.
- [Result & Status Reference — Problem-type registry](reference/results.md#problem-type-registry).
- [Deprecations & removals](deprecations.md) — API removals unrelated to the error/problem-document
  model (e.g. `Benzene.SelfHost.Http`), with their own migration steps.
- [`CHANGELOG.md`](../CHANGELOG.md) — the complete, chronological record every entry on this page
  summarizes.
