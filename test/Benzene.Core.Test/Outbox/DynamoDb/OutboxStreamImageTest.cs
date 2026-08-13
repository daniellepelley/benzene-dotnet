using System;
using System.Text.Json;
using Benzene.Outbox;
using Benzene.Outbox.DynamoDb;
using Xunit;

namespace Benzene.Test.Outbox.DynamoDb;

public class OutboxStreamImageTest
{
    // Shaped exactly like what Benzene.Aws.Lambda.DynamoDb's DynamoDbMessageBodyGetter hands a
    // handler: DynamoDbAttributeValueConverter.ToJson's plain-JSON unmarshalling of a NewImage built
    // from DynamoDbOutboxItemMapper.ToItem's attribute layout.
    private const string StoreShapedJson = """
        {
            "id": "env-1",
            "topic": "payments:capture",
            "payload": "{\"amount\":100}",
            "payloadType": "MyApp.CapturePaymentRequest, MyApp",
            "headers": { "traceparent": "00-abc-def-01", "idempotency-key": "env-1" },
            "createdAtUtc": "2026-01-01T00:00:00.0000000Z",
            "attemptCount": 0,
            "status": "Pending",
            "gsiPk": "pending",
            "gsiSk": "2026-01-01T00:00:00.0000000Z"
        }
        """;

    [Fact]
    public void Deserialize_RoundTripsAStoreShapedItem()
    {
        var image = JsonSerializer.Deserialize<OutboxStreamImage>(StoreShapedJson)!;

        Assert.Equal("env-1", image.Id);
        Assert.Equal("payments:capture", image.Topic);
        Assert.Equal("{\"amount\":100}", image.Payload);
        Assert.Equal("MyApp.CapturePaymentRequest, MyApp", image.PayloadType);
        Assert.Equal("00-abc-def-01", image.Headers["traceparent"]);
        Assert.Equal("env-1", image.Headers["idempotency-key"]);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), image.CreatedAtUtc);
        Assert.Equal(0, image.AttemptCount);
        Assert.Null(image.NextAttemptAtUtc);
        Assert.Equal("Pending", image.Status);
        Assert.Null(image.LastError);
    }

    [Fact]
    public void Deserialize_WithNextAttemptAndLastError_RoundTrips()
    {
        const string json = """
            {
                "id": "env-2",
                "topic": "order:placed",
                "payload": "{}",
                "payloadType": "System.String",
                "headers": {},
                "createdAtUtc": "2026-01-01T00:00:00.0000000Z",
                "attemptCount": 3,
                "nextAttemptAtUtc": "2026-01-01T00:05:00.0000000Z",
                "status": "Pending",
                "lastError": "TimeoutException: boom"
            }
            """;

        var image = JsonSerializer.Deserialize<OutboxStreamImage>(json)!;

        Assert.Equal(3, image.AttemptCount);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero), image.NextAttemptAtUtc);
        Assert.Equal("TimeoutException: boom", image.LastError);
    }

    [Fact]
    public void ToEnvelope_MapsEveryFieldOntoAnOutboxEnvelope()
    {
        var image = JsonSerializer.Deserialize<OutboxStreamImage>(StoreShapedJson)!;

        var envelope = image.ToEnvelope();

        Assert.Equal(image.Id, envelope.Id);
        Assert.Equal(image.Topic, envelope.Topic);
        Assert.Equal(image.Payload, envelope.Payload);
        Assert.Equal(image.PayloadType, envelope.PayloadType);
        Assert.Equal(OutboxStatus.Pending, envelope.Status);
        Assert.Equal(image.CreatedAtUtc, envelope.CreatedAtUtc);
    }

    [Fact]
    public void ToEnvelope_UnknownStatus_FallsBackToPending()
    {
        var image = new OutboxStreamImage { Id = "env-3", Status = "SomethingUnknown" };

        var envelope = image.ToEnvelope();

        Assert.Equal(OutboxStatus.Pending, envelope.Status);
    }
}
