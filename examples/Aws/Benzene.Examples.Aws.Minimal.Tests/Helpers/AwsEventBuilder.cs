using System.Collections.Generic;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.SNSEvents;
using Amazon.Lambda.SQSEvents;
using Benzene.Core.Messages.BenzeneMessage;
using Newtonsoft.Json;

namespace Benzene.Examples.Aws.Minimal.Tests.Helpers;

/// <summary>
/// Builds a native event for each of the four sources the minimal example hosts, carrying the topic in
/// the location that transport reads: the "topic" message attribute on SQS/SNS, the HTTP route on API
/// Gateway, and the event's <c>detail-type</c> on EventBridge. The point of the example is that the same
/// handler is reached by all of them - so these builders differ only in envelope, never in payload.
/// </summary>
public static class AwsEventBuilder
{
    private const string TopicAttribute = "topic";

    public static BenzeneMessageRequest BenzeneMessage(string topic, object payload)
        => new()
        {
            Topic = topic,
            Body = JsonConvert.SerializeObject(payload),
            Headers = new Dictionary<string, string>()
        };

    public static APIGatewayProxyRequest ApiGateway(string method, string path, object payload)
        => new()
        {
            HttpMethod = method,
            Path = path,
            Body = JsonConvert.SerializeObject(payload),
            Headers = new Dictionary<string, string>()
        };

    public static SQSEvent Sqs(string topic, object payload)
        => new()
        {
            Records = new List<SQSEvent.SQSMessage>
            {
                new()
                {
                    EventSource = "aws:sqs",
                    MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>
                    {
                        { TopicAttribute, new SQSEvent.MessageAttribute { StringValue = topic, DataType = "String" } }
                    },
                    Body = JsonConvert.SerializeObject(payload)
                }
            }
        };

    public static SNSEvent Sns(string topic, object payload)
        => new()
        {
            Records = new List<SNSEvent.SNSRecord>
            {
                new()
                {
                    EventSource = "aws:sns",
                    Sns = new SNSEvent.SNSMessage
                    {
                        MessageAttributes = new Dictionary<string, SNSEvent.MessageAttribute>
                        {
                            { TopicAttribute, new SNSEvent.MessageAttribute { Value = topic, Type = "String" } }
                        },
                        Message = JsonConvert.SerializeObject(payload)
                    }
                }
            }
        };

    // Built as wire-format keys (a dictionary) rather than the EventBridgeEvent type: the test host
    // serializes the event with Newtonsoft, which ignores that type's System.Text.Json
    // [JsonPropertyName("detail-type")] attributes - so a plain object would arrive with the wrong keys
    // and go unrecognised. detail-type IS the topic; source must be present for the event to be routed
    // as EventBridge; detail is the domain payload.
    public static object EventBridge(string detailType, object payload)
        => new Dictionary<string, object>
        {
            ["detail-type"] = detailType,
            ["source"] = "benzene.examples.aws.minimal",
            ["detail"] = payload
        };
}
