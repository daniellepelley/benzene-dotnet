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
/// version "v2"; the router runs it only when the incoming <c>benzene-version</c> is "v2". Being the
/// newest version does not make it the default - a message with no version goes to
/// <see cref="CreateOrderV1MessageHandler"/>, the handler declared default.
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
