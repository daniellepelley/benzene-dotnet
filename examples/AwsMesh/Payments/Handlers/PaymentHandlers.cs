using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Clients;
using Benzene.Core.MessageHandlers;
using Benzene.Examples.AwsMesh.Payments.Model;
using Benzene.Examples.AwsMesh.Shared;
using Benzene.Http;
using Benzene.Results;
using Microsoft.Extensions.Logging;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Examples.AwsMesh.Payments.Handlers;

/// <summary>Lists all payments.</summary>
[HttpEndpoint("GET", "/payments")]
[Message("payments:get-all")]
public class GetPaymentsMessageHandler : IMessageHandler<Void, PaymentDto[]>
{
    private static readonly PaymentDto[] Payments =
    {
        new("pay-1", "ord-1", 549.00m, "GBP", "captured"),
        new("pay-2", "ord-2", 24.00m, "GBP", "authorized"),
    };

    public Task<IBenzeneResult<PaymentDto[]>> HandleAsync(Void request)
        => BenzeneResult.Ok(Payments).AsTask();
}

/// <summary>
/// Captures a payment for an order and chains the final hop — asks shipping-api to book (topic
/// <c>shipping:book</c>, routed to its SQS ingress). Best-effort, same posture as orders-api's send.
/// </summary>
[HttpEndpoint("POST", "/payments")]
[Message("payments:capture")]
public class CapturePaymentMessageHandler : IMessageHandler<CapturePayment, PaymentDto>
{
    private readonly IBenzeneMessageSender _sender;
    private readonly ILogger<CapturePaymentMessageHandler> _logger;

    public CapturePaymentMessageHandler(IBenzeneMessageSender sender, ILogger<CapturePaymentMessageHandler> logger)
    {
        _sender = sender;
        _logger = logger;
    }

    public async Task<IBenzeneResult<PaymentDto>> HandleAsync(CapturePayment request)
    {
        // Claim-check dogfood (README "Claim-check: oversized payloads"): orders-api may fold a large
        // demo "supporting document" into OrderId rather than adding a field to this GENERATED contract
        // (see ClaimCheckDemoPayload's remarks). Strip it back off here — payments-api never forwards
        // the attachment further: shipping:book and payment:captured stay small on purpose, since
        // neither of those downstream routes is claim-checked and would otherwise risk tripping
        // SQS/EventBridge's own ~256 KB transport limit.
        var orderId = ClaimCheckDemoPayload.Strip(request.OrderId);
        if (orderId.Length != request.OrderId.Length)
        {
            _logger.LogInformation(
                "payment capture for {orderId} arrived with an attached supporting document ({bytes} bytes) - not forwarded downstream",
                orderId, request.OrderId.Length);
        }

        var payment = new PaymentDto($"pay-{orderId}", orderId, request.Amount, request.Currency, "captured");

        // Point-to-point command over SQS: shipping-api must book this exactly once.
        try
        {
            await _sender.SendAsync<OutboundShipmentBook, Void>("shipping:book",
                new OutboundShipmentBook { OrderId = orderId, Carrier = "DPD" });
            _logger.LogInformation("payment captured for {orderId}; sent shipping:book", orderId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "downstream shipping:book send failed for {orderId}", orderId);
        }

        // Integration event over EventBridge: routed by rule to notifications-api and analytics-api.
        try
        {
            await _sender.SendAsync<OutboundPaymentCaptured, Void>("payment:captured",
                new OutboundPaymentCaptured { OrderId = orderId, PaymentId = payment.Id, Amount = request.Amount, Currency = request.Currency });
            _logger.LogInformation("payment captured for {orderId}; published payment:captured", orderId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "payment:captured publish failed for {orderId}", orderId);
        }

        return BenzeneResult.Created(payment);
    }
}

/// <summary>The request to capture a payment.</summary>
public class CapturePayment
{
    public string OrderId { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "GBP";
}
