using System.Globalization;
using Amazon.DynamoDBv2.Model;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Clients;
using Benzene.Core.MessageHandlers;
// Generated at build time from contracts/payments.spec.json — see the csproj's <BenzeneServiceContract>.
using Benzene.Examples.AwsMesh.Orders.Clients.PaymentsCapture;
using Benzene.Examples.AwsMesh.Orders.Model;
using Benzene.Examples.AwsMesh.Shared;
using Benzene.Http;
using Benzene.Outbox.DynamoDb;
using Benzene.Results;
using Microsoft.Extensions.Logging;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Examples.AwsMesh.Orders.Handlers;

/// <summary>Lists all orders.</summary>
[HttpEndpoint("GET", "/orders")]
[Message("orders:get-all")]
public class GetOrdersMessageHandler : IMessageHandler<Void, OrderDto[]>
{
    private static readonly OrderDto[] Orders =
    {
        new("ord-1", "Espresso Machine", 1),
        new("ord-2", "Coffee Beans (1kg)", 3),
    };

    public Task<IBenzeneResult<OrderDto[]>> HandleAsync(Void request)
        => BenzeneResult.Ok(Orders).AsTask();
}

/// <summary>
/// Places a new order and chains the next hop, atomically: <c>payments:capture</c> (SQS) and
/// <c>order:placed</c> (SNS) are captured by <c>Benzene.Outbox</c> (<c>UseOutbox()</c> on both routes,
/// wired in <c>Startup</c>) rather than sent inline, and committed in <b>one</b> DynamoDB
/// <c>TransactWriteItems</c> together with the order row itself
/// (<see cref="IDynamoDbOutboxTransaction.CommitAsync"/>) — all-or-nothing. This is the fix for the
/// best-effort hole this handler used to document: previously each downstream send was wrapped in its
/// own swallow-and-log try/catch, so a transport hiccup silently lost the send while the order still
/// "succeeded". Now a persistence failure on either the order row or a captured envelope fails the
/// whole request loudly (the caller sees the failure, exactly as before a transport failure would),
/// and once the transaction commits, both downstream sends are durable — a relay Lambda (see
/// <c>Handlers/OutboxHandlers.cs</c>) delivers them out of band, retried with backoff, parked after
/// <c>OutboxOptions.MaxAttempts</c>. See <c>work/outbox-plan.md</c> §2.3's "Transactional" mode for
/// exactly what this does and does not guarantee (delivery is still at-least-once, never
/// exactly-once).
/// </summary>
[HttpEndpoint("POST", "/orders")]
[Message("orders:create")]
public class CreateOrderMessageHandler : IMessageHandler<CreateOrder, OrderDto>
{
    private readonly IBenzeneMessageSender _sender;
    private readonly IDynamoDbOutboxTransaction _outboxTransaction;
    private readonly string _ordersTableName;
    private readonly ILogger<CreateOrderMessageHandler> _logger;

    public CreateOrderMessageHandler(
        IBenzeneMessageSender sender,
        IDynamoDbOutboxTransaction outboxTransaction,
        ILogger<CreateOrderMessageHandler> logger)
    {
        _sender = sender;
        _outboxTransaction = outboxTransaction;
        _ordersTableName = Environment.GetEnvironmentVariable("ORDERS_TABLE_NAME") ?? "orders";
        _logger = logger;
    }

    public async Task<IBenzeneResult<OrderDto>> HandleAsync(CreateOrder request)
    {
        var order = new OrderDto($"ord-{request.Item.GetHashCode():x}", request.Item, request.Quantity);
        var amount = request.Quantity * 10m;

        // Both sends hit UseOutbox() routes (Startup wires it on payments:capture + order:placed), so
        // neither one goes out over the wire here — each is staged on this scope's BufferedOutboxStage
        // (drained by the CommitAsync below) and returns Accepted<Void> immediately. Point-to-point
        // command to payments-api: the topic id and request shape come from the GENERATED client's
        // request type (payments-api's own contract, see the csproj's <BenzeneServiceContract>), so this
        // call site cannot drift from what payments-api actually serves the way a hand-copied mirror DTO
        // could — sent directly via the sender (not the generated client's CapturePaymentsAsync) because
        // an outboxed/SQS route is fire-and-forget only (SendAsync<TRequest, Void>; see
        // OutboundSqsContextConverter's remarks), and the generated client's method returns a typed
        // PaymentDto that no fire-and-forget transport can ever produce.
        //
        // Claim-check dogfood (README "Claim-check: oversized payloads", work/claim-check-plan.md Phase
        // 6): this route is also ClaimChecked=true (Startup). When the caller attaches a
        // SupportingDocument, ClaimCheckDemoPayload folds it into OrderId rather than adding a new field
        // to CapturePayment — see that helper's remarks for why. Ordinary calls with no
        // SupportingDocument stay small and exercise the normal under-threshold bypass path.
        await _sender.SendAsync<CapturePayment, Void>("payments:capture", new CapturePayment
        {
            OrderId = ClaimCheckDemoPayload.Embed(order.Id, request.SupportingDocument),
            // (double) because the contract's JSON Schema says "number" and the generator maps that to
            // double — payments-api's own CapturePayment.Amount is decimal. See the README note.
            Amount = (double)amount,
            Currency = "GBP",
        });

        // Fan-out event over SNS: every subscriber (inventory-api, notifications-api) gets it.
        await _sender.SendAsync<OutboundOrderPlaced, Void>("order:placed",
            new OutboundOrderPlaced { OrderId = order.Id, Item = order.Item, Quantity = order.Quantity, Amount = amount, Currency = "GBP" });

        var orderPut = new TransactWriteItem
        {
            Put = new Put
            {
                TableName = _ordersTableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["id"] = new AttributeValue { S = order.Id },
                    ["item"] = new AttributeValue { S = order.Item },
                    ["quantity"] = new AttributeValue { N = order.Quantity.ToString(CultureInfo.InvariantCulture) },
                    ["amount"] = new AttributeValue { N = amount.ToString(CultureInfo.InvariantCulture) },
                    ["currency"] = new AttributeValue { S = "GBP" },
                },
            },
        };

        // ONE TransactWriteItems: the order row + both staged outbox envelopes, all-or-nothing.
        await _outboxTransaction.CommitAsync([orderPut]);

        _logger.LogInformation(
            "order {orderId} created; committed order row + payments:capture + order:placed atomically",
            order.Id);

        return BenzeneResult.Created(order);
    }
}

/// <summary>The request to create an order.</summary>
public class CreateOrder
{
    public string Item { get; set; } = "";
    public int Quantity { get; set; }

    /// <summary>
    /// Optional, demo-only: a large "attached document" blob (e.g. a receipt/invoice a caller wants
    /// captured alongside the payment). Not a real order-processing concern — it exists purely to
    /// dogfood <c>Benzene.ClaimCheck</c>'s offload path on the <c>payments:capture</c> SQS route (see
    /// README "Claim-check: oversized payloads"). When present, <see cref="ClaimCheckDemoPayload"/>
    /// folds its bytes into <c>CapturePayment.OrderId</c> rather than adding a new field to that
    /// GENERATED contract type — <c>contracts/payments.spec.json</c> stays untouched (see the README's
    /// "Contract note"). Omit it (the common case) to exercise the ordinary, under-threshold send: a
    /// value of roughly 200&#8211;300&#160;KB pushes the serialized <c>payments:capture</c> body over
    /// <c>ClaimCheckOptions.DefaultThresholdBytes</c> (192&#160;KiB, 196,608 bytes) and triggers a real
    /// offload — while staying clear of DynamoDB's unrelated 400&#160;KB item cap, which also applies
    /// here because this route is outboxed (see README "Claim-check: oversized payloads" for the exact
    /// sizing the demo uses and why).
    /// </summary>
    public string? SupportingDocument { get; set; }
}
