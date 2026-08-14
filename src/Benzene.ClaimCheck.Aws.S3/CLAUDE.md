# Benzene.ClaimCheck.Aws.S3

An `IClaimCheckStore` (from `Benzene.ClaimCheck`) backed by an Amazon S3 bucket — the production
store for the claim-check pattern: `Benzene.ClaimCheck`'s outbound offload middleware puts an
oversized serialized wire body here and gets an `s3://` reference back; the inbound hydrate
middleware resolves that reference back to the body on the receiving side. Ships alongside
`Benzene.ClaimCheck.Azure.Blob` as the second store package — see `work/claim-check-plan.md` §2 for
why a claim-check store is its own abstraction rather than reusing `Benzene.Mesh.Aws.S3`'s
`IMeshArtifactStore`, and §3 for the full retention/failure reasoning this package implements.

## Shape

- `S3ClaimCheckStore : IClaimCheckStore`
  - `PutAsync` — `PutObjectAsync` with key `{prefix}{topic}/{yyyy/MM/dd}/{guid}` (the topic travels
    into the key verbatim — S3 keys allow the colons a Benzene topic commonly contains — and the
    date segment makes a lifecycle rule's effect on the prefix auditable by eye) and
    `ContentType = "application/octet-stream"` (the claim-check body is an opaque wire string; the
    store does not know or care what format it serializes). Returns `s3://{bucket}/{key}`.
  - `GetAsync` — validates the reference belongs to *this* store (scheme `s3`, and the bucket +
    prefix match this store's own configuration) before ever calling S3, throwing
    `ClaimCheckStoreMismatchException` otherwise — a store must never resolve a reference it did not
    (or could not have) issued. A `GetObjectAsync` 404 (`AmazonS3Exception.StatusCode == NotFound`)
    maps to `null`, exactly like `Benzene.Mesh.Aws.S3`'s `S3MeshArtifactStore.TryReadAsync`. Every
    call forwards its `CancellationToken` to the SDK.
- `Extensions.AddS3ClaimCheckStore(bucket, prefix = "claim-checks/")` — registers it as the
  `IClaimCheckStore`, resolving `IAmazonS3` from DI (the consumer registers the client — this
  package never creates the bucket).

## Retention — the lifecycle rule is the whole contract

This store has **no delete-on-consume** (SNS-style fan-out delivers one offloaded message to several
independent consumers, and at-least-once transports redeliver — deleting at read time would starve
siblings or make a retry permanently unhydratable; see `IClaimCheckStore`'s own remarks). Retention
is therefore **entirely the caller's responsibility**, via an S3 bucket lifecycle rule
(`aws_s3_bucket_lifecycle_configuration` in Terraform, or the console equivalent) that expires
objects under the configured prefix:

```hcl
resource "aws_s3_bucket_lifecycle_configuration" "claim_checks" {
  bucket = aws_s3_bucket.claim_checks.id
  rule {
    id     = "expire-claim-checks"
    status = "Enabled"
    filter { prefix = "claim-checks/" }
    expiration { days = 14 }   # SQS's own retention maxes at 14 days — see the sizing rule below
  }
}
```

**TTL sizing rule (state this wherever the rule is configured):** the expiration must exceed the
longest possible path from send to last possible consumption — queue retention plus any DLQ redirect/
redrive window. Undersizing it means a slow redelivery can find its claim already deleted, which
surfaces as `ClaimCheckNotFoundException` on the receiving side (a fail-loud failure, not silent
data loss — but still one you can avoid by sizing the rule correctly).

This package does **not** create the bucket or the lifecycle rule itself, matching every other
Benzene infrastructure store (`Benzene.Mesh.Aws.S3`, `Benzene.Idempotency.DynamoDb`,
`Benzene.Outbox.DynamoDb`): infra owns infra. Encryption at rest defers to the bucket's own default
settings (SSE-S3/SSE-KMS) — this package does not manage keys. Access control is whatever IAM policy
governs the bucket.

## Notes

- The store does **not** create the bucket — that's the consumer's infra, same posture as
  `S3MeshArtifactStore`.
- A foreign reference (wrong scheme, wrong bucket, or a prefix outside this store's own
  configuration) always throws `ClaimCheckStoreMismatchException` from `GetAsync` — this is a
  security boundary (never fetch an attacker-supplied or otherwise foreign location), not a
  not-found case; a genuinely missing/expired object under this store's own prefix returns `null`
  instead, which the hydrate middleware turns into `ClaimCheckNotFoundException`.
- Two independent services (a sender and a receiver in different deployments) sharing a claim-check
  bucket must agree on the same bucket **and** prefix — that agreement is the same kind of explicit
  deployment contract the wire header itself represents (Tier C, `work/claim-check-plan.md` decision
  2).

## Tests

- `test/Benzene.Core.Test/ClaimCheck/S3/S3ClaimCheckStoreTest.cs` — unit tests with
  `Mock<IAmazonS3>(MockBehavior.Strict)`: put issues the expected key shape and returns the
  reference; get round-trips content; 404 maps to `null`; a foreign bucket/scheme/prefix throws
  `ClaimCheckStoreMismatchException` without calling S3; the `CancellationToken` passed to `PutAsync`/
  `GetAsync` reaches the SDK call.
- `test/Benzene.Integration.Test/ClaimCheck/` — a LocalStack-backed round trip (outbound
  `UseClaimCheck().UseSqs(...)` sending an oversized payload; the standalone `Benzene.Aws.Sqs`
  consumer pipeline's `UseClaimCheck()` hydrating it) proving a payload over the SQS/SNS/EventBridge
  256 KB family limit actually traverses a real queue. See that folder's own notes for whether it ran
  in a given environment (it needs Docker).
