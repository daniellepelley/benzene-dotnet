using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Examples.Versioning.Tests.Helpers;
using Newtonsoft.Json;
using Xunit;
using InvV1 = Benzene.Examples.Versioning.Contracts.Inventory.V1;
using InvV2 = Benzene.Examples.Versioning.Contracts.Inventory.V2;
using InvV3 = Benzene.Examples.Versioning.Contracts.Inventory.V3;

namespace Benzene.Examples.Versioning.Tests;

/// <summary>
/// Mechanism B - transparent payload casting with caster CHAINING (docs/specification/versioning.md §4).
/// The topic <c>inventory:adjust</c> has one handler, on V3, and only the adjacent casters V1&lt;-&gt;V2 and
/// V2&lt;-&gt;V3 are registered. So a V1 producer is upcast by composing V1-&gt;V2-&gt;V3, and the V3 response is
/// downcast V3-&gt;V2-&gt;V1 - there is no direct V1&lt;-&gt;V3 caster. Each hop seeds the field that version
/// introduced (V1-&gt;V2: WarehouseId="wh-main"; V2-&gt;V3: Reason="unspecified"), and the handler echoes both
/// into <c>Trace</c>, which survives the downcast - so a plain V1 request/response proves both hops ran.
/// </summary>
[Collection("Sequential")]
public class CasterChainingTests : VersioningTestBase
{
    [Fact]
    public async Task V1Request_IsUpcastByChainingV1toV2toV3_ThenResponseDowncastToV1()
    {
        // A bare V1 payload: no warehouse, no reason - the fields V2 and V3 added.
        var request = VersionedEventBuilder.BenzeneMessage(
            Topics.InventoryAdjust, Versions.V1,
            new InvV1.InventoryAdjustment { Sku = "ABC", Delta = 5 });

        var response = await TestLambdaHosting.SendEventAsync<BenzeneMessageResponse>(request);

        var body = JsonConvert.DeserializeObject<InvV1.InventoryAdjustment>(response.Body);
        Assert.Equal("ABC", body.Sku);
        Assert.Equal(5, body.Delta);

        // The handler ran on V3 and saw BOTH chain-seeded fields: "wh-main" can only have come from the
        // V1->V2 hop, "unspecified" only from the V2->V3 hop - so the full V1->V2->V3 chain executed.
        Assert.Contains("warehouse=wh-main", body.Trace);
        Assert.Contains("reason=unspecified", body.Trace);

        // The response is genuinely V1-shaped: re-reading it as V3 shows the V2/V3 fields are absent
        // (the V3->V2->V1 downcast dropped them), so the caller never sees a schema it didn't ask for.
        var asV3 = JsonConvert.DeserializeObject<InvV3.InventoryAdjustment>(response.Body);
        Assert.Null(asV3.WarehouseId);
        Assert.Null(asV3.Reason);
    }

    [Fact]
    public async Task V2Request_IsUpcastOneHopToV3_ThenResponseDowncastToV2()
    {
        // A V2 payload carries its own warehouse; only the V2->V3 hop is needed, which seeds Reason.
        var request = VersionedEventBuilder.BenzeneMessage(
            Topics.InventoryAdjust, Versions.V2,
            new InvV2.InventoryAdjustment { Sku = "XYZ", Delta = 2, WarehouseId = "wh-9" });

        var response = await TestLambdaHosting.SendEventAsync<BenzeneMessageResponse>(request);

        var body = JsonConvert.DeserializeObject<InvV2.InventoryAdjustment>(response.Body);
        Assert.Equal("XYZ", body.Sku);
        Assert.Equal("wh-9", body.WarehouseId);
        Assert.Contains("warehouse=wh-9", body.Trace);       // the caller's own warehouse, preserved
        Assert.Contains("reason=unspecified", body.Trace);   // seeded by the single V2->V3 hop

        // V2-shaped response: it has WarehouseId but not V3's Reason.
        var asV3 = JsonConvert.DeserializeObject<InvV3.InventoryAdjustment>(response.Body);
        Assert.Equal("wh-9", asV3.WarehouseId);
        Assert.Null(asV3.Reason);
    }

    [Fact]
    public async Task V3Request_NeedsNoCasting_HandlerSeesItVerbatim()
    {
        var request = VersionedEventBuilder.BenzeneMessage(
            Topics.InventoryAdjust, Versions.V3,
            new InvV3.InventoryAdjustment { Sku = "V3", Delta = 1, WarehouseId = "wh-3", Reason = "restock" });

        var response = await TestLambdaHosting.SendEventAsync<BenzeneMessageResponse>(request);

        var body = JsonConvert.DeserializeObject<InvV3.InventoryAdjustment>(response.Body);
        Assert.Equal("wh-3", body.WarehouseId);
        Assert.Equal("restock", body.Reason);
        Assert.Contains("warehouse=wh-3", body.Trace);
        Assert.Contains("reason=restock", body.Trace);
    }

    [Fact]
    public async Task NoVersion_BypassesCasting_HandlerSeesTheBodyAsItsCanonicalV3()
    {
        // With no version signalled the casting decorator delegates straight through: the body is
        // deserialized directly as the handler's own (V3) type, no caster runs.
        var request = VersionedEventBuilder.BenzeneMessage(
            Topics.InventoryAdjust, null,
            new InvV3.InventoryAdjustment { Sku = "DEF", Delta = 7, WarehouseId = "wh-direct", Reason = "audit" });

        var response = await TestLambdaHosting.SendEventAsync<BenzeneMessageResponse>(request);

        var body = JsonConvert.DeserializeObject<InvV3.InventoryAdjustment>(response.Body);
        Assert.Equal("wh-direct", body.WarehouseId);
        Assert.Equal("audit", body.Reason);
    }

    [Fact]
    public async Task V1Request_IsUpcastByChaining_OverApiGateway()
    {
        // The same chaining works over a different transport: a V1 body posted to the HTTP route, with the
        // version in the benzene-version header, is upcast V1->V2->V3 before the handler and downcast back.
        var request = VersionedEventBuilder.ApiGateway(
            "POST", "/inventory/adjust", Versions.V1,
            new InvV1.InventoryAdjustment { Sku = "HTTP", Delta = 4 });

        var response = await TestLambdaHosting.SendEventAsync<APIGatewayProxyResponse>(request);

        Assert.Equal(200, response.StatusCode);
        var body = JsonConvert.DeserializeObject<InvV1.InventoryAdjustment>(response.Body);
        Assert.Equal("HTTP", body.Sku);
        Assert.Contains("warehouse=wh-main", body.Trace);
        Assert.Contains("reason=unspecified", body.Trace);
    }

    [Fact]
    public async Task V1Request_IsUpcastByChaining_OverSqs()
    {
        // SQS is fire-and-forget, so assert on what the handler recorded: it ran on V3 and saw both
        // chain-seeded fields, i.e. the V1 message was upcast V1->V2->V3 before reaching it - over a
        // transport whose request mapper the casting decorator had to be registered after (see StartUp).
        var sqsEvent = VersionedEventBuilder.SqsEvent(
            Topics.InventoryAdjust, Versions.V1,
            new InvV1.InventoryAdjustment { Sku = "SQS", Delta = 8 });

        await TestLambdaHosting.SendEventAsync(sqsEvent);

        Assert.Contains(ProcessedEntries, x =>
            x.StartsWith("inventory:adjust v3")
            && x.Contains("sku=SQS")
            && x.Contains("warehouse=wh-main")
            && x.Contains("reason=unspecified"));
    }
}
