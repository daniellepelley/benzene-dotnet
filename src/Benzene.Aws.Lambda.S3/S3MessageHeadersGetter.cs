using System;
using System.Collections.Generic;
using Benzene.Abstractions.Messages.Mappers;

namespace Benzene.Aws.Lambda.S3;

/// <summary>
/// Exposes an S3 record's bucket, key, event name, and region as message headers.
/// </summary>
public class S3MessageHeadersGetter : IMessageHeadersGetter<S3RecordContext>
{
    /// <summary>
    /// Gets the S3 event metadata as headers.
    /// </summary>
    /// <param name="context">The S3 record context to extract headers from.</param>
    /// <returns>A dictionary of header names to values, omitting any that aren't present on the record.</returns>
    public IDictionary<string, string> GetHeaders(S3RecordContext context)
    {
        var record = context.S3EventNotificationRecord;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (record.EventName != null)
        {
            headers["eventName"] = record.EventName;
        }

        if (record.AwsRegion != null)
        {
            headers["awsRegion"] = record.AwsRegion;
        }

        if (record.S3?.Bucket?.Name != null)
        {
            headers["bucketName"] = record.S3.Bucket.Name;
        }

        if (record.S3?.Object?.Key != null)
        {
            // S3 URL-encodes the key on the event notification (space -> '+', reserved/non-ASCII
            // bytes percent-encoded). "key" carries the decoded form so it's usable directly with
            // GetObjectAsync; "keyRaw" preserves the original wire encoding for callers who need it
            // (e.g. constructing a pre-signed URL that expects the encoded form).
            headers["key"] = S3ObjectKeyCodec.Decode(record.S3.Object.Key);
            headers["keyRaw"] = record.S3.Object.Key;
        }

        return headers;
    }
}
