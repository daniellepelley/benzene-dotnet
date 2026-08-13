using System.Text.Json;
using Benzene.Aws.Lambda.EventBridge;
using Xunit;

namespace Benzene.Test.Aws.EventBridge;

public class EventBridgeGettersTest
{
    private static EventBridgeContext CreateContext(string detailJson = "{\"name\":\"some-name\"}")
    {
        using var document = JsonDocument.Parse(detailJson);
        return new EventBridgeContext(new EventBridgeEvent
        {
            Version = "0",
            Id = "event-id-1",
            DetailType = "order.created",
            Source = "com.example.orders",
            Account = "123456789012",
            Region = "eu-west-1",
            Time = "2026-01-01T00:00:00Z",
            Detail = document.RootElement.Clone()
        });
    }

    [Fact]
    public void Topic_IsTheDetailType()
    {
        var topic = new EventBridgeMessageTopicGetter().GetTopic(CreateContext());

        Assert.Equal("order.created", topic.Id);
    }

    [Fact]
    public void Body_IsTheRawDetailJson()
    {
        var body = new EventBridgeMessageBodyGetter().GetBody(CreateContext("{\"name\":\"some-name\"}"));

        Assert.Equal("{\"name\":\"some-name\"}", body);
    }

    [Fact]
    public void Body_UndefinedDetail_ReturnsNull()
    {
        var context = new EventBridgeContext(new EventBridgeEvent
        {
            DetailType = "order.created",
            Source = "com.example.orders"
            // Detail left as the JsonElement default (ValueKind.Undefined).
        });

        var body = new EventBridgeMessageBodyGetter().GetBody(context);

        Assert.Null(body);
    }

    [Fact]
    public void Headers_ContainPrefixedEnvelopeMetadata()
    {
        var headers = new EventBridgeMessageHeadersGetter().GetHeaders(CreateContext());

        Assert.Equal("com.example.orders", headers["eventbridge-source"]);
        Assert.Equal("event-id-1", headers["eventbridge-id"]);
        Assert.Equal("123456789012", headers["eventbridge-account"]);
        Assert.Equal("eu-west-1", headers["eventbridge-region"]);
        Assert.Equal("order.created", headers["eventbridge-detail-type"]);
    }

    [Fact]
    public void Headers_LiftEmbeddedBenzeneHeadersFromDetail()
    {
        var context = CreateContext(
            "{\"name\":\"some-name\",\"_benzeneHeaders\":{\"x-correlation-id\":\"abc-123\",\"traceparent\":\"00-trace-span-01\"}}");

        var headers = new EventBridgeMessageHeadersGetter().GetHeaders(context);

        Assert.Equal("abc-123", headers["x-correlation-id"]);
        Assert.Equal("00-trace-span-01", headers["traceparent"]);
    }

    [Fact]
    public void Headers_WithoutEmbeddedHeaders_OnlyEnvelopeMetadataIsPresent()
    {
        var headers = new EventBridgeMessageHeadersGetter().GetHeaders(CreateContext());

        Assert.DoesNotContain(headers, x => !x.Key.StartsWith("eventbridge-"));
    }

    [Fact]
    public void Headers_BenzeneHeadersKeyIsNotAnObject_IsIgnored()
    {
        var context = CreateContext("{\"name\":\"some-name\",\"_benzeneHeaders\":\"not-an-object\"}");

        var headers = new EventBridgeMessageHeadersGetter().GetHeaders(context);

        Assert.DoesNotContain(headers, x => !x.Key.StartsWith("eventbridge-"));
    }

    [Fact]
    public void Headers_EmbeddedNonStringValue_IsSkipped()
    {
        var context = CreateContext(
            "{\"name\":\"some-name\",\"_benzeneHeaders\":{\"x-correlation-id\":\"abc-123\",\"x-retry-count\":3}}");

        var headers = new EventBridgeMessageHeadersGetter().GetHeaders(context);

        Assert.Equal("abc-123", headers["x-correlation-id"]);
        Assert.DoesNotContain("x-retry-count", headers.Keys);
    }

    [Fact]
    public async System.Threading.Tasks.Task BodySetter_ReEmbedsOriginalBenzeneHeaders_SoTheyStillReadBackAfterHydration()
    {
        // The pre-hydration event carries the claim-check placeholder in detail, with the real wire
        // headers (including _benzeneHeaders) embedded exactly as the outbound converter puts them -
        // see OutboundEventBridgeContextConverter.BuildDetail.
        var context = CreateContext(
            "{\"_benzeneClaimCheck\":\"memory://claim-check/abc\"," +
            "\"_benzeneHeaders\":{\"x-correlation-id\":\"abc-123\",\"traceparent\":\"00-trace-span-01\"}}");

        await new EventBridgeMessageBodySetter().SetBody(context, "{\"name\":\"some-name\"}");

        // The body getter now returns the hydrated payload...
        var body = new EventBridgeMessageBodyGetter().GetBody(context);
        Assert.Equal("some-name", JsonDocument.Parse(body).RootElement.GetProperty("name").GetString());

        // ...and the headers getter still lifts the original _benzeneHeaders out of it.
        var headers = new EventBridgeMessageHeadersGetter().GetHeaders(context);
        Assert.Equal("abc-123", headers["x-correlation-id"]);
        Assert.Equal("00-trace-span-01", headers["traceparent"]);
    }

    [Fact]
    public async System.Threading.Tasks.Task BodySetter_NoOriginalBenzeneHeaders_HydratesBodyAsIs()
    {
        var context = CreateContext("{\"_benzeneClaimCheck\":\"memory://claim-check/abc\"}");

        await new EventBridgeMessageBodySetter().SetBody(context, "{\"name\":\"some-name\"}");

        var body = new EventBridgeMessageBodyGetter().GetBody(context);
        Assert.Equal("{\"name\":\"some-name\"}", body);
    }

    [Fact]
    public async System.Threading.Tasks.Task BodySetter_HydratedBodyIsNotAJsonObject_OriginalHeadersAreNotEmbedded()
    {
        var context = CreateContext(
            "{\"_benzeneClaimCheck\":\"memory://claim-check/abc\"," +
            "\"_benzeneHeaders\":{\"x-correlation-id\":\"abc-123\"}}");

        // A non-object hydrated body (e.g. a JSON array or scalar) can't have headers re-embedded into
        // it - the setter leaves it as-is rather than forcing it into an object.
        await new EventBridgeMessageBodySetter().SetBody(context, "[1,2,3]");

        var body = new EventBridgeMessageBodyGetter().GetBody(context);
        Assert.Equal("[1,2,3]", body);
    }
}
