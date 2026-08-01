using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Examples.Versioning.Services;
using Benzene.Results;
using V1 = Benzene.Examples.Versioning.Contracts.Order.V1;

namespace Benzene.Examples.Versioning.Handlers;

/// <summary>
/// Handler-version dispatch (Mechanism A): the V1 implementation of <c>order:create</c>. Registered for
/// version "v1" via the second argument of <see cref="MessageAttribute"/>; the router runs it only when
/// the incoming <c>benzene-version</c> is "v1". It shares the topic with
/// <see cref="CreateOrderV2MessageHandler"/> - two genuinely different request shapes, two handlers, no
/// casting between them.
/// </summary>
[Message(Topics.OrderCreate, Versions.V1)]
public class CreateOrderV1MessageHandler : IMessageHandler<V1.CreateOrder, V1.OrderAccepted>
{
    private readonly IProcessedLog _log;

    public CreateOrderV1MessageHandler(IProcessedLog log)
    {
        _log = log;
    }

    public Task<IBenzeneResult<V1.OrderAccepted>> HandleAsync(V1.CreateOrder request)
    {
        _log.Record($"order:create v1 | customer={request.CustomerName} qty={request.Quantity}");

        return Task.FromResult(BenzeneResult.Ok(new V1.OrderAccepted
        {
            OrderId = "order-1",
            HandledBy = "CreateOrderV1MessageHandler"
        }));
    }
}
