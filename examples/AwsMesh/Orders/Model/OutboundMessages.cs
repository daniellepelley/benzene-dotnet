namespace Benzene.Examples.AwsMesh.Orders.Model;

// The capture-payment request orders-api sends to payments-api (topic payments:capture) used to be
// hand-written here as OutboundPaymentCapture — a copy of payments-api's own CapturePayment shape,
// kept in step by eye. It is now GENERATED from payments-api's published contract
// (contracts/payments.spec.json → Clients.PaymentsCapture.CapturePayment; see the <BenzeneServiceContract>
// item in the csproj), so the request shape AND the topic id come from the producer's contract rather
// than being duplicated here. Startup still declares the send, which is what puts the orders → payments
// edge in the spec's events and so on the mesh topology.

/// <summary>
/// The <c>order:placed</c> event orders-api publishes to SNS once an order is placed. SNS fans it out
/// to <b>every</b> subscriber — here both inventory-api (reserve stock) and notifications-api (notify the
/// customer) — which is what SNS is for, versus the point-to-point SQS command to payments-api. Declared
/// in the spec's <c>events</c> → the mesh's structural edges orders → inventory and orders → notifications.
/// </summary>
public class OutboundOrderPlaced
{
    public string OrderId { get; set; } = "";
    public string Item { get; set; } = "";
    public int Quantity { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "GBP";
}
