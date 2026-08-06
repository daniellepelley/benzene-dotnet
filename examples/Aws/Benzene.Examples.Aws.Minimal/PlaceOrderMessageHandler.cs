using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Results;

namespace Benzene.Examples.Aws.Minimal;

/// <summary>
/// The whole point of the example: ONE handler, reached over four AWS event sources. It knows nothing
/// about API Gateway, SNS, SQS or EventBridge - it just handles the <c>order:placed</c> topic. The
/// <see cref="StartUp"/> wires each transport to route an <c>order:placed</c> message here; over API
/// Gateway the returned <see cref="OrderAccepted"/> becomes the HTTP response body, and over the
/// fire-and-forget sources (SNS/SQS/EventBridge) it is recorded so a test can observe the handler ran.
/// </summary>
[Message("order:placed")]
[HttpEndpoint("POST", "/orders")]
public class PlaceOrderMessageHandler : IMessageHandler<OrderPlaced, OrderAccepted>
{
    private readonly IProcessedLog _log;

    public PlaceOrderMessageHandler(IProcessedLog log)
    {
        _log = log;
    }

    public Task<IBenzeneResult<OrderAccepted>> HandleAsync(OrderPlaced message)
    {
        _log.Record($"order:placed {message.OrderId} for {message.Customer}");
        return Task.FromResult(BenzeneResult.Ok(new OrderAccepted
        {
            OrderId = message.OrderId,
            Status = "accepted"
        }));
    }
}

public class OrderPlaced
{
    public string OrderId { get; set; }
    public string Customer { get; set; }
}

public class OrderAccepted
{
    public string OrderId { get; set; }
    public string Status { get; set; }
}
