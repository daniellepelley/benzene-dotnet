using System;
using System.Collections.Generic;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Aws.Lambda.Core;

namespace Benzene.Aws.Lambda.DynamoDb;

/// <summary>
/// Exposes the stream record's envelope metadata as <c>dynamodb-</c>-prefixed headers (plan
/// decision DS4). Unlike EventBridge there is no embedded Benzene wire-header convention here —
/// these events originate from table writes, not from a Benzene publisher.
/// </summary>
public class DynamoDbMessageHeadersGetter : IMessageHeadersGetter<DynamoDbRecordContext>
{
    /// <summary>
    /// Gets the record's envelope metadata as headers.
    /// </summary>
    /// <param name="context">The DynamoDB record context to extract headers from.</param>
    /// <returns>A dictionary of <c>dynamodb-</c>-prefixed metadata headers.</returns>
    public IDictionary<string, string> GetHeaders(DynamoDbRecordContext context)
    {
        var record = context.Record;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        headers.AddIfPresent("dynamodb-event-name", record.EventName);
        headers.AddIfPresent("dynamodb-event-id", record.EventId);
        headers.AddIfPresent("dynamodb-table", DynamoDbUtils.GetTableName(record.EventSourceArn));
        headers.AddIfPresent("dynamodb-sequence-number", record.Dynamodb?.SequenceNumber);
        headers.AddIfPresent("dynamodb-stream-view-type", record.Dynamodb?.StreamViewType);
        headers.AddIfPresent("dynamodb-event-source-arn", record.EventSourceArn);
        headers.AddIfPresent("dynamodb-aws-region", record.AwsRegion);

        return headers;
    }
}
