using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Examples.Versioning.Services;
using Benzene.Http;
using Benzene.Results;
using V3 = Benzene.Examples.Versioning.Contracts.Inventory.V3;

namespace Benzene.Examples.Versioning.Handlers;

/// <summary>
/// Transparent payload casting (Mechanism B): the ONE handler for <c>inventory:adjust</c>, written
/// against the newest schema, V3. It is unversioned - <c>[Message(topic)]</c> with no version - because
/// the version axis is handled by the casting pipeline, not by picking a handler: a V1 or V2 producer is
/// up-cast to V3 (V1 by chaining V1->V2->V3) before this method runs, so it only ever sees V3.
///
/// It never references V1/V2 or any caster. To make the round trip observable it writes the two
/// chain-seeded fields (<c>WarehouseId</c> from the V1->V2 hop, <c>Reason</c> from the V2->V3 hop) into
/// <c>Trace</c>, which exists in every version and therefore survives the response downcast back to the
/// caller's version.
/// </summary>
[HttpEndpoint("POST", "/inventory/adjust")]
[Message(Topics.InventoryAdjust)]
public class AdjustInventoryMessageHandler : IMessageHandler<V3.InventoryAdjustment, V3.InventoryAdjustment>
{
    private readonly IProcessedLog _log;

    public AdjustInventoryMessageHandler(IProcessedLog log)
    {
        _log = log;
    }

    public Task<IBenzeneResult<V3.InventoryAdjustment>> HandleAsync(V3.InventoryAdjustment request)
    {
        _log.Record(
            $"inventory:adjust v3 | sku={request.Sku} delta={request.Delta} warehouse={request.WarehouseId} reason={request.Reason}");

        return Task.FromResult(BenzeneResult.Ok(new V3.InventoryAdjustment
        {
            Sku = request.Sku,
            Delta = request.Delta,
            WarehouseId = request.WarehouseId,
            Reason = request.Reason,
            // Stamped by the (only) V3 handler; carries the chain-seeded values back out so even a V1
            // caller - whose response is downcast to V1 - can see that both hops of the upcast ran.
            Trace = $"handled-on-v3;warehouse={request.WarehouseId};reason={request.Reason}"
        }));
    }
}
