using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Benzene.Abstractions;
using Benzene.Core.MessageHandlers.Serialization;

namespace Benzene.Aws.Lambda.Kinesis.TestHelpers;

/// <summary>
/// Builds a realistic <see cref="KinesisEvent"/> from an <see cref="IMessageBuilder{T}"/>, for
/// driving Kinesis-triggered stream handlers in tests.
/// </summary>
public static class MessageBuilderExtensions
{
    /// <summary>
    /// Builds a <see cref="KinesisEvent"/> with <paramref name="numberOfRecords"/> records, each
    /// carrying <paramref name="source"/>'s message JSON-serialized into
    /// <see cref="KinesisRecordData.Data"/> (base64, matching how Lambda actually delivers Kinesis
    /// record data) - decode it back with <see cref="KinesisRecordData.GetDataAsString"/>.
    /// </summary>
    /// <remarks>
    /// Unlike SQS/SNS/S3/DynamoDB/EventBridge, Kinesis is fan-in/streaming: there is no per-record
    /// topic routing to a message handler (see <c>Benzene.Aws.Lambda.Kinesis/CLAUDE.md</c>'s "Fan-in
    /// (streaming), not fan-out"), so <paramref name="source"/>'s <c>Topic</c>/<c>Headers</c> are not
    /// used here - only its <c>Message</c>, which every record carries a copy of. Every record shares
    /// the same partition key by default, so they land on the same simulated shard in order; pass a
    /// distinct <paramref name="partitionKey"/> per call for a multi-partition batch built from
    /// several calls concatenated together.
    /// </remarks>
    /// <param name="source">The message builder to read the message from.</param>
    /// <param name="numberOfRecords">How many records to put in the batch, each with a fresh sequence number.</param>
    /// <param name="partitionKey">The partition key every record in this batch is reported under.</param>
    /// <returns>The built Kinesis event batch.</returns>
    public static KinesisEvent AsKinesis<T>(this IMessageBuilder<T> source, int numberOfRecords = 1, string partitionKey = "benzene-test-partition")
    {
        var json = new JsonSerializer().Serialize(source.Message);
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        return new KinesisEvent
        {
            Records = Enumerable.Range(1, numberOfRecords).Select(sequence => new KinesisEventRecord
            {
                EventSource = "aws:kinesis",
                EventId = $"shardId-000000000000:{sequence}",
                EventName = "aws:kinesis:record",
                AwsRegion = "eu-west-1",
                Kinesis = new KinesisRecordData
                {
                    PartitionKey = partitionKey,
                    SequenceNumber = sequence.ToString(),
                    Data = data
                }
            }).ToList()
        };
    }
}
