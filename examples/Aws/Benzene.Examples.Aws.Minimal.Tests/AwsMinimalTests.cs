using System.Linq;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Benzene.Aws.Lambda.Core.TestHelpers;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Examples.Aws.Minimal.Tests.Helpers;
using Benzene.Testing;
using Newtonsoft.Json;
using Xunit;

namespace Benzene.Examples.Aws.Minimal.Tests;

/// <summary>
/// Boots the example's real <see cref="StartUp"/> into an in-memory AWS Lambda host - the same
/// construction a deployed function performs - and pushes a native event through the front door of each
/// of the four sources it hosts. The one <see cref="PlaceOrderMessageHandler"/> answers all of them: over
/// API Gateway and the envelope it returns the ack synchronously; over SNS/SQS/EventBridge it records to
/// the shared <see cref="IProcessedLog"/> a fire-and-forget send can then be asserted against.
/// </summary>
public class AwsMinimalTests
{
    private const string Topic = "order:placed";

    private readonly AwsLambdaBenzeneTestHost _host;

    public AwsMinimalTests()
    {
        InMemoryProcessedLog.Clear();
        _host = new AwsLambdaBenzeneTestHost(BenzeneTestHost.Create<StartUp>().BuildAwsLambdaHost());
    }

    private static OrderPlaced AnOrder(string id = "ORD-1") =>
        new() { OrderId = id, Customer = "acme" };

    [Fact]
    public async Task ApiGateway_PostOrders_ReturnsAccepted()
    {
        var response = await _host.SendEventAsync<APIGatewayProxyResponse>(
            AwsEventBuilder.ApiGateway("POST", "/orders", AnOrder()));

        Assert.InRange(response.StatusCode, 200, 299);
        var ack = JsonConvert.DeserializeObject<OrderAccepted>(response.Body);
        Assert.Equal("ORD-1", ack.OrderId);
        Assert.Equal("accepted", ack.Status);
        Assert.Contains("order:placed ORD-1 for acme", ProcessedEntries());
    }

    [Fact]
    public async Task BenzeneMessage_Envelope_ReturnsAccepted()
    {
        var response = await _host.SendEventAsync<BenzeneMessageResponse>(
            AwsEventBuilder.BenzeneMessage(Topic, AnOrder("ORD-2")));

        var ack = JsonConvert.DeserializeObject<OrderAccepted>(response.Body);
        Assert.Equal("ORD-2", ack.OrderId);
        Assert.Equal("accepted", ack.Status);
    }

    [Fact]
    public async Task Sqs_RoutesToTheHandler()
    {
        await _host.SendEventAsync(AwsEventBuilder.Sqs(Topic, AnOrder("ORD-3")));
        Assert.Contains("order:placed ORD-3 for acme", ProcessedEntries());
    }

    [Fact]
    public async Task Sns_RoutesToTheHandler()
    {
        await _host.SendEventAsync(AwsEventBuilder.Sns(Topic, AnOrder("ORD-4")));
        Assert.Contains("order:placed ORD-4 for acme", ProcessedEntries());
    }

    [Fact]
    public async Task EventBridge_RoutesToTheHandler()
    {
        await _host.SendEventAsync(AwsEventBuilder.EventBridge(Topic, AnOrder("ORD-5")));
        Assert.Contains("order:placed ORD-5 for acme", ProcessedEntries());
    }

    private static string[] ProcessedEntries() => new InMemoryProcessedLog().Entries.ToArray();
}
