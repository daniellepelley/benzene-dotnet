using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Results;
using Microsoft.Extensions.Logging;

namespace Benzene.Examples.K8sTransports.Domain;

public record PlaceOrderRequest(string CustomerId, string Sku, int Quantity);

public record OrderPlaced(string OrderId, string Status);

/// <summary>
/// The one piece of business logic this example is about. It is reached three different ways, all
/// from the same running container - <c>App/</c> hosts an ASP.NET Core server that maps
/// <c>POST /orders</c> straight onto it (the <see cref="HttpEndpointAttribute"/> below), AND polls an
/// SQS queue, AND consumes a Kafka topic, dispatching every message/record from either straight onto
/// it too - all three registering it by the same <c>[Message("order-place")]</c>. Nothing in this
/// class knows which of the three called it: no transport type, no queue/topic name, no HTTP context.
/// That's deliberate - it's what "write the handler once, reach it from whichever transport carries
/// the traffic you actually have, without standing up a separate service per transport" means in
/// practice, not just as a slogan. See <c>docs/getting-started-kubernetes.md</c> for the full
/// walkthrough, and <c>App/Startup.cs</c> for how one process hosts all three.
/// </summary>
[Message("order-place")]
[HttpEndpoint("POST", "/orders")]
public class PlaceOrderMessageHandler : IMessageHandler<PlaceOrderRequest, OrderPlaced>
{
    private readonly ILogger<PlaceOrderMessageHandler> _logger;

    public PlaceOrderMessageHandler(ILogger<PlaceOrderMessageHandler> logger)
    {
        _logger = logger;
    }

    public Task<IBenzeneResult<OrderPlaced>> HandleAsync(PlaceOrderRequest request)
    {
        var orderId = $"order-{Guid.NewGuid():N}"[..13];

        // The same line, unmodified, is what you'll find in `kubectl logs` for all three
        // Deployments - proof it's the same code path, not a transport-specific copy of it.
        _logger.LogInformation(
            "order placed: {OrderId} - {Quantity}x {Sku} for {CustomerId}",
            orderId, request.Quantity, request.Sku, request.CustomerId);

        return BenzeneResult.Created(new OrderPlaced(orderId, "placed")).AsTask();
    }
}
