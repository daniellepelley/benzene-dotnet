using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Examples.Versioning.Services;
using Benzene.Results;
using V2 = Benzene.Examples.Versioning.Contracts.Order.V2;

namespace Benzene.Examples.Versioning.Handlers;

/// <summary>
/// Handler-version dispatch (Mechanism A): the V2 implementation of <c>order:create</c>. Registered for
/// version "v2". Because "v2" is the highest registered version, it is also the one the default
/// <see cref="Benzene.Core.MessageHandlers.VersionSelector"/> falls back to when a producer sends no
/// version at all - i.e. V2 is the topic's default handler.
/// </summary>
[Message(Topics.OrderCreate, Versions.V2)]
public class CreateOrderV2MessageHandler : IMessageHandler<V2.CreateOrder, V2.OrderAccepted>
{
    private readonly IProcessedLog _log;

    public CreateOrderV2MessageHandler(IProcessedLog log)
    {
        _log = log;
    }

    public Task<IBenzeneResult<V2.OrderAccepted>> HandleAsync(V2.CreateOrder request)
    {
        _log.Record(
            $"order:create v2 | customer={request.FirstName} {request.LastName} qty={request.Quantity} currency={request.Currency}");

        return Task.FromResult(BenzeneResult.Ok(new V2.OrderAccepted
        {
            OrderId = "order-1",
            HandledBy = "CreateOrderV2MessageHandler",
            Currency = request.Currency
        }));
    }
}
