using System;
using System.Collections.Generic;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.SNSEvents;
using Amazon.Lambda.SQSEvents;
using Benzene.Core.Messages.BenzeneMessage;
using Newtonsoft.Json;

namespace Benzene.Examples.Versioning.Tests.Helpers;

/// <summary>
/// Builds a native event for each transport the example hosts, carrying the topic and - the point of
/// these tests - the payload schema version in the <c>benzene-version</c> location that transport reads:
/// a message attribute for SQS/SNS, a header for API Gateway and the envelope. Passing <c>version = null</c>
/// omits the signal entirely, which is how the "no version -&gt; default" cases are exercised.
/// </summary>
public static class VersionedEventBuilder
{
    private const string TopicAttribute = "topic";
    private const string VersionHeader = "benzene-version";

    public static BenzeneMessageRequest BenzeneMessage(string topic, string version, object payload)
    {
        var headers = new Dictionary<string, string>();
        if (version != null)
        {
            headers[VersionHeader] = version;
        }

        return new BenzeneMessageRequest
        {
            Topic = topic,
            Body = JsonConvert.SerializeObject(payload),
            Headers = headers
        };
    }

    public static APIGatewayProxyRequest ApiGateway(string method, string path, string version, object payload)
    {
        var headers = new Dictionary<string, string> { { "x-correlation-id", Guid.NewGuid().ToString() } };
        if (version != null)
        {
            headers[VersionHeader] = version;
        }

        return new APIGatewayProxyRequest
        {
            HttpMethod = method,
            Path = path,
            Body = JsonConvert.SerializeObject(payload),
            Headers = headers
        };
    }

    public static SQSEvent SqsEvent(string topic, string version, object payload)
    {
        var attributes = new Dictionary<string, SQSEvent.MessageAttribute>
        {
            { TopicAttribute, new SQSEvent.MessageAttribute { StringValue = topic, DataType = "String" } }
        };
        if (version != null)
        {
            attributes[VersionHeader] = new SQSEvent.MessageAttribute { StringValue = version, DataType = "String" };
        }

        return new SQSEvent
        {
            Records = new List<SQSEvent.SQSMessage>
            {
                new()
                {
                    EventSource = "aws:sqs",
                    MessageAttributes = attributes,
                    Body = JsonConvert.SerializeObject(payload)
                }
            }
        };
    }

    public static SNSEvent SnsEvent(string topic, string version, object payload)
    {
        var attributes = new Dictionary<string, SNSEvent.MessageAttribute>
        {
            { TopicAttribute, new SNSEvent.MessageAttribute { Value = topic, Type = "String" } }
        };
        if (version != null)
        {
            attributes[VersionHeader] = new SNSEvent.MessageAttribute { Value = version, Type = "String" };
        }

        return new SNSEvent
        {
            Records = new List<SNSEvent.SNSRecord>
            {
                new()
                {
                    EventSource = "aws:sns",
                    Sns = new SNSEvent.SNSMessage
                    {
                        MessageAttributes = attributes,
                        Message = JsonConvert.SerializeObject(payload)
                    }
                }
            }
        };
    }
}
