using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Examples.App.Handlers;
using Benzene.Examples.App.Model.Messages;
using Benzene.Results;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Examples.Aws;

/// <summary>
/// The ingress half of the egress demo: <see cref="PublishOrderCreatedMessageHandler"/> publishes
/// <see cref="OrderCreatedEvent"/> onto the SQS queue this function's own SQS trigger consumes, and
/// this handler is what that trigger routes it to - so one deployment shows the whole loop, an API
/// call handing follow-up work to its queue. It stands in for the real follow-up (an email, a
/// warehouse call) by recording the order it was told about.
/// </summary>
[Message(MessageTopicNames.OrderCreated)]
public class OrderCreatedNotificationHandler : IMessageHandler<OrderCreatedEvent, Void>
{
    /// <summary>The orders this function has been notified of (in-memory, like the demo order store).</summary>
    public static ConcurrentBag<Guid> Notified { get; } = new();

    public Task<IBenzeneResult<Void>> HandleAsync(OrderCreatedEvent request)
    {
        Notified.Add(request.Id);
        return Task.FromResult(BenzeneResult.Ok<Void>());
    }
}
