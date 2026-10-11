using System.Linq;
using System.Threading.Tasks;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Examples.Versioning.Tests.Helpers;
using Newtonsoft.Json;
using Xunit;
using OrderV1 = Benzene.Examples.Versioning.Contracts.Order.V1;
using OrderV2 = Benzene.Examples.Versioning.Contracts.Order.V2;

namespace Benzene.Examples.Versioning.Tests;

/// <summary>
/// Mechanism A - handler-version dispatch (docs/specification/versioning.md §3). The topic
/// <c>order:create</c> has a V1 and a V2 handler; the incoming <c>benzene-version</c> selects which one
/// runs, across every transport. When no version is signalled the router uses the topic's declared default
/// (V1, <c>[Message(..., IsDefault = true)]</c>); a version no handler serves is not-found.
/// </summary>
[Collection("Sequential")]
public class HandlerVersionRoutingTests : VersioningTestBase
{
    [Fact]
    public async Task V1Version_RoutesToTheV1Handler_OverBenzeneMessage()
    {
        var request = VersionedEventBuilder.BenzeneMessage(
            Topics.OrderCreate, Versions.V1,
            new OrderV1.CreateOrder { CustomerName = "Jo Bloggs", Quantity = 3 });

        var response = await TestLambdaHosting.SendEventAsync<BenzeneMessageResponse>(request);

        var accepted = JsonConvert.DeserializeObject<OrderV1.OrderAccepted>(response.Body);
        Assert.Equal("CreateOrderV1MessageHandler", accepted.HandledBy);
        Assert.Contains(ProcessedEntries, x => x.StartsWith("order:create v1"));
    }

    [Fact]
    public async Task V2Version_RoutesToTheV2Handler_OverBenzeneMessage()
    {
        var request = VersionedEventBuilder.BenzeneMessage(
            Topics.OrderCreate, Versions.V2,
            new OrderV2.CreateOrder { FirstName = "Jo", LastName = "Bloggs", Quantity = 3, Currency = "GBP" });

        var response = await TestLambdaHosting.SendEventAsync<BenzeneMessageResponse>(request);

        var accepted = JsonConvert.DeserializeObject<OrderV2.OrderAccepted>(response.Body);
        Assert.Equal("CreateOrderV2MessageHandler", accepted.HandledBy);
        Assert.Equal("GBP", accepted.Currency);
        Assert.Contains(ProcessedEntries, x => x.StartsWith("order:create v2"));
    }

    [Fact]
    public async Task NoVersion_RoutesToTheDeclaredDefault_V1()
    {
        // No version signalled: the topic's default handler - V1, declared with IsDefault = true - not
        // the newest version.
        var request = VersionedEventBuilder.BenzeneMessage(
            Topics.OrderCreate, null,
            new OrderV1.CreateOrder { CustomerName = "Jo Bloggs", Quantity = 1 });

        var response = await TestLambdaHosting.SendEventAsync<BenzeneMessageResponse>(request);

        var accepted = JsonConvert.DeserializeObject<OrderV1.OrderAccepted>(response.Body);
        Assert.Equal("CreateOrderV1MessageHandler", accepted.HandledBy);
        Assert.Contains(ProcessedEntries, x => x.StartsWith("order:create v1"));
    }

    [Fact]
    public async Task UnknownVersion_IsNotFound_NotTheNearestOrNewestHandler()
    {
        var request = VersionedEventBuilder.BenzeneMessage(
            Topics.OrderCreate, "v9",
            new OrderV2.CreateOrder { FirstName = "Jo", LastName = "Bloggs", Quantity = 1, Currency = "USD" });

        var response = await TestLambdaHosting.SendEventAsync<BenzeneMessageResponse>(request);

        Assert.Equal("not-found", response.StatusCode);
        Assert.Empty(ProcessedEntries);
    }

    [Fact]
    public async Task V1Version_RoutesToTheV1Handler_OverSqs()
    {
        // SQS is fire-and-forget (no response body), so the assertion is on the side effect the V1
        // handler recorded - which only happens if the "v1" attribute routed to it.
        var sqsEvent = VersionedEventBuilder.SqsEvent(
            Topics.OrderCreate, Versions.V1,
            new OrderV1.CreateOrder { CustomerName = "Sqs Sender", Quantity = 9 });

        await TestLambdaHosting.SendEventAsync(sqsEvent);

        Assert.Contains(ProcessedEntries, x => x.StartsWith("order:create v1") && x.Contains("qty=9"));
        Assert.DoesNotContain(ProcessedEntries, x => x.StartsWith("order:create v2"));
    }

    [Fact]
    public async Task V2Version_RoutesToTheV2Handler_OverSns()
    {
        var snsEvent = VersionedEventBuilder.SnsEvent(
            Topics.OrderCreate, Versions.V2,
            new OrderV2.CreateOrder { FirstName = "Sns", LastName = "Sender", Quantity = 4, Currency = "EUR" });

        await TestLambdaHosting.SendEventAsync(snsEvent);

        Assert.Contains(ProcessedEntries, x => x.StartsWith("order:create v2") && x.Contains("currency=EUR"));
        Assert.DoesNotContain(ProcessedEntries, x => x.StartsWith("order:create v1"));
    }
}
