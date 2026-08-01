// Payloads for the transparent-casting demo (docs/specification/versioning.md §4, "Mechanism B").
//
// The topic "inventory:adjust" has THREE live payload versions of a SINGLE type family
// (InventoryAdjustment), and ONE handler - written against the newest, V3. Producers on V1 or V2 are
// transparently up-cast to V3 before the handler sees them, and the V3 response is down-cast back to the
// caller's version on the way out. The example registers ONLY adjacent casters (V1->V2 and V2->V3), so a
// V1 producer is upcast by CHAINING V1->V2->V3 (SchemaCastDefinitionsExpander composes the hops); there
// is deliberately no direct V1->V3 caster.
//
// Each version lives in its own namespace with the SAME type name - the convention
// SchemaTypeMatcher relies on to map fields between versions by name. Newer versions only ADD fields;
// the up-caster seeds each newly-introduced field with a default (see StartUp), which is how we can
// prove, from a plain V1 request, that both hops of the chain ran.

namespace Benzene.Examples.Versioning.Contracts.Inventory.V1
{
    public class InventoryAdjustment
    {
        public string Sku { get; set; }
        public int Delta { get; set; }

        // Present in every version, so a value the V3 handler writes here survives the downcast all the
        // way back to a V1 caller - that is how the round-trip is asserted end to end.
        public string Trace { get; set; }
    }
}

namespace Benzene.Examples.Versioning.Contracts.Inventory.V2
{
    public class InventoryAdjustment
    {
        public string Sku { get; set; }
        public int Delta { get; set; }
        public string Trace { get; set; }

        // Introduced in V2. The V1->V2 up-caster seeds it with a default, so a V1 producer that never
        // sent a warehouse still reaches the handler with one.
        public string WarehouseId { get; set; }
    }
}

namespace Benzene.Examples.Versioning.Contracts.Inventory.V3
{
    public class InventoryAdjustment
    {
        public string Sku { get; set; }
        public int Delta { get; set; }
        public string Trace { get; set; }
        public string WarehouseId { get; set; }

        // Introduced in V3 - the version the handler is written against. The V2->V3 up-caster seeds it.
        public string Reason { get; set; }
    }
}
