using System;
using System.Collections.Generic;
using Amazon.Lambda.S3Events;
using Benzene.Abstractions;

namespace Benzene.Aws.Lambda.S3.TestHelpers;

/// <summary>
/// Builds a realistic <see cref="S3Event"/> from an <see cref="IMessageBuilder{T}"/>, for driving
/// S3-triggered handlers in tests.
/// </summary>
public static class MessageBuilderExtensions
{
    /// <summary>
    /// Builds an <see cref="S3Event"/> with one record whose <c>eventName</c> is
    /// <paramref name="source"/>'s topic (matching <c>S3MessageTopicGetter</c>'s routing) and whose
    /// bucket/key are set from <paramref name="bucketName"/>/<paramref name="key"/>.
    /// </summary>
    /// <remarks>
    /// Unlike SQS/SNS/DynamoDB/EventBridge, an S3 event notification carries no arbitrary payload -
    /// <c>S3MessageBodyGetter</c> always builds the body from the record's own bucket/key/event
    /// metadata (an <c>S3Notification</c>), not from a message a producer chose to send. So
    /// <paramref name="source"/>'s <c>Message</c> is not used here; only its <c>Topic</c> (the S3
    /// event name a handler routes on) is meaningful. Use <paramref name="bucketName"/>/
    /// <paramref name="key"/> to control what <c>S3Notification</c> a handler observing this event
    /// actually receives.
    /// </remarks>
    /// <param name="source">The message builder to read the topic (S3 event name) from.</param>
    /// <param name="bucketName">The S3 bucket name to report on the record.</param>
    /// <param name="key">The real (decoded) S3 object key a handler should observe.</param>
    /// <returns>The built S3 event notification batch.</returns>
    /// <remarks>
    /// <paramref name="key"/> is the real key a handler should see after decoding - not the wire
    /// form. Real S3 event notifications carry the key URL-encoded (space -&gt; <c>+</c>, other
    /// reserved/non-ASCII bytes percent-encoded), and production handlers run it through
    /// <see cref="S3ObjectKeyCodec.Decode(string?)"/>. So the record built here stores
    /// <see cref="S3ObjectKeyCodec.Encode(string?)"/> of <paramref name="key"/>, the exact inverse,
    /// meaning any key - including one containing <c>+</c>, <c>%</c>, or non-ASCII characters -
    /// round-trips back to <paramref name="key"/> through the real production getters.
    /// </remarks>
    public static S3Event AsS3<T>(this IMessageBuilder<T> source, string bucketName = "benzene-test-bucket", string key = "benzene-test-object")
    {
        return new S3Event
        {
            Records = new List<S3Event.S3EventNotificationRecord>
            {
                new S3Event.S3EventNotificationRecord
                {
                    EventSource = "aws:s3",
                    EventName = source.Topic,
                    AwsRegion = "eu-west-1",
                    S3 = new S3Event.S3Entity
                    {
                        Bucket = new S3Event.S3BucketEntity { Name = bucketName },
                        Object = new S3Event.S3ObjectEntity { Key = S3ObjectKeyCodec.Encode(key) }
                    },
                    ResponseElements = new S3Event.ResponseElementsEntity { XAmzRequestId = Guid.NewGuid().ToString() }
                }
            }
        };
    }
}
