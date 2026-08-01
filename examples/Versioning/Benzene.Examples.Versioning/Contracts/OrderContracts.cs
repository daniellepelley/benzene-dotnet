// Payloads for the handler-version-dispatch demo (docs/specification/versioning.md §3, "Mechanism A").
//
// The topic "order:create" has TWO live payload versions, each served by its OWN handler
// (CreateOrderV1MessageHandler / CreateOrderV2MessageHandler). The incoming benzene-version header
// selects which handler runs - no casting is involved, the two shapes are genuinely different
// implementations. Each version keeps its request and response types in a per-version namespace.

namespace Benzene.Examples.Versioning.Contracts.Order.V1
{
    /// <summary>V1 of the create-order request: a flat customer name.</summary>
    public class CreateOrder
    {
        public string CustomerName { get; set; }
        public int Quantity { get; set; }
    }

    public class OrderAccepted
    {
        public string OrderId { get; set; }
        public string HandledBy { get; set; }
    }
}

namespace Benzene.Examples.Versioning.Contracts.Order.V2
{
    /// <summary>
    /// V2 split the single customer name into first/last and added a currency - a genuinely different
    /// request shape, which is exactly when a second handler (rather than a caster) is the right tool.
    /// </summary>
    public class CreateOrder
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public int Quantity { get; set; }
        public string Currency { get; set; }
    }

    public class OrderAccepted
    {
        public string OrderId { get; set; }
        public string HandledBy { get; set; }
        public string Currency { get; set; }
    }
}
