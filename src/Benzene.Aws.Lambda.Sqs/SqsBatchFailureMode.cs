namespace Benzene.Aws.Lambda.Sqs;

/// <summary>
/// Controls how a single SQS message's failure (a thrown exception, or a message handler reporting
/// an unsuccessful result) affects the rest of the batch.
/// </summary>
public enum SqsBatchFailureMode
{
    /// <summary>
    /// The AWS best-practice default. Only the messages that actually failed are reported back via
    /// <c>SQSBatchResponse.BatchItemFailures</c>, so SQS redrives just those messages - the rest of
    /// the batch is treated as successfully processed. Requires <c>ReportBatchItemFailures</c> to be
    /// configured on the SQS event source mapping's <c>FunctionResponseTypes</c>; without it, AWS
    /// ignores <c>BatchItemFailures</c> entirely and treats the whole invocation as either fully
    /// succeeded or fully failed.
    /// </summary>
    PartialBatchFailure = 0,

    /// <summary>
    /// The first failure in the batch fails the entire Lambda invocation (by throwing), so SQS
    /// retries/redrives every message in the batch, not just the ones that failed. Useful when
    /// messages in a batch aren't independent of each other, or when the event source mapping
    /// doesn't have <c>ReportBatchItemFailures</c> configured.
    /// </summary>
    FailWholeBatch = 1,

    /// <summary>
    /// At-most-once: every message in the batch is acknowledged whatever its outcome - a failure
    /// result, no result, or a thrown exception - so SQS deletes it and nothing is redelivered or
    /// dead-lettered. Each failure is still logged. The at-most-once override transport-bindings.md
    /// §1 (obligation 8) requires every one-way binding to offer; use it only for loss-tolerant
    /// workloads. An infrastructure (mis-wiring) failure still fails the invocation, as in every mode:
    /// it is not a message outcome.
    /// </summary>
    AtMostOnce = 2,
}
