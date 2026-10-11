using Benzene.Abstractions.Schema;

// contract-document-cases.json namingCases: two distinct types with the same simple name, Item,
// keyed by qualified name in the fixture (orders.Item, payments.Item).
namespace Benzene.Conformance.Test.Naming.Orders
{
    public class Item
    {
        public string Sku { get; set; } = string.Empty;
    }
}

namespace Benzene.Conformance.Test.Naming.Payments
{
    public class Item
    {
        public string PaymentId { get; set; } = string.Empty;
    }
}

// The same two types with declared schema names (declared-names-resolve-a-collision). .NET declares a
// name with an attribute on the type, so the declared variants are the same shapes in their own
// namespaces.
namespace Benzene.Conformance.Test.Naming.Declared.Orders
{
    [SchemaName("OrderItem")]
    public class Item
    {
        public string Sku { get; set; } = string.Empty;
    }
}

namespace Benzene.Conformance.Test.Naming.Declared.Payments
{
    [SchemaName("PaymentItem")]
    public class Item
    {
        public string PaymentId { get; set; } = string.Empty;
    }
}
