# Benzene.ClaimCheck.Azure.Blob

## What this package does
The Azure production store for `Benzene.ClaimCheck` ("or Blob Storage for Azure" — the owner's own
words approving the feature, `work/claim-check-plan.md` line 4). `BlobClaimCheckStore` implements
`IClaimCheckStore` over an `Azure.Storage.Blobs.BlobContainerClient`: `PutAsync` writes the serialized
wire body that was too large to send inline as a blob and returns an opaque `azblob://{container}/{key}`
reference; `GetAsync` resolves that reference back to the body, or `null` if it is not found/expired.
See `work/claim-check-plan.md` §2 for why this is a **separate** store from
`Benzene.Mesh.Azure.Blob.BlobMeshArtifactStore` (different lifecycle — a durable catalog vs. an
expiring transient — and pulling the mesh aggregator into every message-sending service would be the
wrong dependency), and §3 for the retention/failure posture this package's store honors verbatim (no
delete-on-consume, TTL-based expiry owned by infrastructure, fail loud on a missing claim).

## Key types
- `BlobClaimCheckStore : IClaimCheckStore` — `PutAsync`/`GetAsync` over a `BlobContainerClient`.
  - **Key layout**: `{prefix}{topic}/{yyyy/MM/dd}/{guid}` (UTC date; topic verbatim) — the same shape
    `Benzene.ClaimCheck.Aws.S3.S3ClaimCheckStore` uses, so a lifecycle rule's effect is auditable by
    browsing the container the same way either store organizes it.
  - **Reference format**: `azblob://{container}/{key}`.
  - **404 mapping**: `RequestFailedException.Status == 404` on `DownloadContentAsync` → `null`, the
    same filter `Benzene.Mesh.Azure.Blob.BlobMeshArtifactStore.TryReadAsync` uses for its own 404s.
  - **Mismatch validation**: `GetAsync` throws `ClaimCheckStoreMismatchException` (defined in
    `Benzene.ClaimCheck`) for a reference whose scheme isn't `azblob`, whose container isn't this
    store's own container, or whose key falls outside this store's configured prefix — refusing to
    fetch rather than attempting it, exactly as `IClaimCheckStore.GetAsync`'s contract requires.
  - Both operations forward the caller's `CancellationToken` to the underlying SDK calls.
  - `ContentType` is `application/octet-stream` (the store doesn't parse or care what format the
    serialized body is in — same posture as `S3ClaimCheckStore`).
- `Extensions.AddBlobClaimCheckStore(...)` — two overloads, mirroring
  `Benzene.Mesh.Azure.Blob.Extensions.AddMeshAggregatorWithBlob`:
  - `AddBlobClaimCheckStore(BlobContainerClient container, string prefix = "claim-checks/")` — over a
    caller-supplied client (you own its auth/lifetime).
  - `AddBlobClaimCheckStore(Uri blobServiceUri, string containerName, string prefix = "claim-checks/")`
    — convenience overload; builds the client from the storage account's blob endpoint, authenticated
    with `DefaultAzureCredential` (managed identity in Azure, the developer credential locally).

## Deploying
- **The container must already exist** — this package does not create it (same posture as
  `Benzene.Mesh.Azure.Blob` and every other Benzene store package; infra owns infra).
- **Managed identity / RBAC**: the identity running the service needs **Storage Blob Data
  Contributor** on the container (or the storage account) — read (`GetAsync`) and write (`PutAsync`)
  both need it. This is the same role `Benzene.Mesh.Azure.Blob`'s `CLAUDE.md` documents for the mesh
  aggregator; if a service uses both packages against the *same* storage account, keep the
  claim-check container and the mesh-artifact container distinct (different retention regimes, and
  arguably different audiences — see §2's dependency reasoning) even though the RBAC role is the same.
- **Retention = a Blob lifecycle-management delete rule** scoped to the container/prefix this store
  writes under (`prefix` defaults to `claim-checks/`). This package does not create that policy —
  provision it in Bicep/Terraform, same as the S3 store's bucket lifecycle rule.
- **TTL sizing rule (verbatim from `work/claim-check-plan.md` §3)**: the TTL must exceed the longest
  path from send to last possible consumption — queue/topic retention plus the DLQ (dead-letter)
  redrive window. Size the delete rule's `daysAfterCreationGreaterThan` (or
  `daysAfterModificationGreaterThan`) accordingly for whichever Azure entity feeds the pipeline this
  store backs (see "Pipeline example" below for Service Bus/Queue Storage-specific numbers).
- **At-rest posture**: defers to the storage account's own encryption (SSE, customer-managed keys if
  configured) and the container's IAM — this package builds no key management, matching
  `Benzene.ClaimCheck`'s core posture.

## Pipeline example: tuning `ThresholdBytes` per Azure transport
`Benzene.ClaimCheck`'s default `ThresholdBytes` (192 KiB) is sized off the *smallest common* limit
across the transport families the core package targets (SQS/SNS/EventBridge, all 256 KB). Two Azure
transports motivate the same middleware pair, at two very different thresholds:

- **Azure Service Bus (standard tier)**: a **256 KB** message-size cap (Premium raises this to
  100 MB) — the default 192 KiB threshold already gives Service Bus standard the same headroom SQS
  gets, with no route-level override needed.
- **Azure Queue Storage**: a **64 KB** message cap — well under the default threshold. A route
  sending on Queue Storage needs a tighter, transport-specific override:
  ```csharp
  routing.Route("orders:archive", pipeline => pipeline
      .UseClaimCheck(o => o.ThresholdBytes = 48 * 1024)   // headroom under Queue Storage's 64 KB cap
      .UseQueueStorage(queueName));
  ```
  This is the documented example of `ClaimCheckOptions.ThresholdBytes` tuning per route (the option
  is per-route already — see `Benzene.ClaimCheck`'s `CLAUDE.md` — this package just gives it a
  concrete Azure number).

**Hydration is not wired for any Azure transport in this package.** `UseClaimCheck<TContext>()`
(hydrate) needs an `IMessageBodySetter<TContext>` for the receiving transport's context type — see
`Benzene.ClaimCheck`'s `CLAUDE.md` and `work/claim-check-plan.md` Phase 2. The AWS Lambda transports
(`Benzene.Aws.Lambda.Sqs`/`.Sns`/`.EventBridge`) already ship theirs because their event POCOs are
plain mutable objects (`context.SqsMessage.Body = body` is the whole implementation). Azure Service
Bus's inbound context is not that simple: `Benzene.Azure.Function.ServiceBus.ServiceBusContext` wraps
an `Azure.Messaging.ServiceBus.ServiceBusReceivedMessage`, whose `Body` property has **no public
setter** (it is produced by the SDK/runtime from the wire message and is read-only by design — verified
against `Azure.Messaging.ServiceBus` 7.18.2). Hydrating a Service Bus message therefore needs
`ServiceBusContext` itself to grow a body-override slot that `ServiceBusMessageBodyGetter` (and the
request mapper) consult ahead of `Message.Body` - a real, if small, change to
`Benzene.Azure.Function.ServiceBus`'s body-read contract, not the 5-line pattern the Lambda POCOs
allow. That's out of scope for this package (which owns the store, not the transport contexts) and
is not done here — **claiming Service Bus hydration works without it would be dishonest per the
plan's own rule**. Whoever picks up Phase 2 step 4 for Service Bus should start from
`Benzene.Azure.Function.ServiceBus/CLAUDE.md`'s "Claim-check hydration" note, which records this same
blocker. Queue Storage and Azure Functions' other triggers are unstarted for the same reason. Offload
(this package's actual job) works today regardless — only hydration on the receiving side needs a
setter.

## Tests
- `test/Benzene.Core.Test/ClaimCheck/Blob/BlobClaimCheckStoreTest.cs` — mocks `BlobContainerClient`/
  `BlobClient` (both have virtual members precisely so Moq can proxy them, the same technique
  `test/Benzene.Core.Test/Clients/Azure/QueueStorage/QueueStorageHealthCheckTest.cs` uses for
  `QueueClient`). Covers: `PutAsync` issues the expected `{prefix}{topic}/{yyyy/MM/dd}/{guid}` key and
  `azblob://{container}/{key}` reference; `GetAsync` round-trips stored content; a 404
  (`RequestFailedException.Status == 404`) maps to `null`; a foreign container, foreign scheme, and a
  key outside the configured prefix each throw `ClaimCheckStoreMismatchException`; the caller's
  `CancellationToken` is forwarded to both the upload and download calls.
- **No Azurite integration test in this pass, deliberately** — `work/claim-check-plan.md` Phase 5
  says so explicitly: the emulator fixtures are heavy, and
  `Benzene.ClaimCheck.Aws.S3`'s LocalStack integration test already proves the offload→hydrate
  middleware pair end to end against a real object store; a second emulator-backed integration test
  for the same middleware behavior against a different store's SDK would not exercise anything new.
  If an Azurite-backed test is added later, it belongs beside the S3 one under
  `test/Benzene.Integration.Test/ClaimCheck/`.

## Dependencies on other Benzene packages
- **Benzene.ClaimCheck** — `IClaimCheckStore`, `ClaimCheckPutContext`, `ClaimCheckStoreMismatchException`.
- **Benzene.Abstractions** (transitive via `Benzene.ClaimCheck`) — `IBenzeneServiceContainer`.
- NuGet: `Azure.Storage.Blobs`, `Azure.Identity` (pinned to the same versions as
  `Benzene.Mesh.Azure.Blob`: `12.22.2` / `1.13.1`).
