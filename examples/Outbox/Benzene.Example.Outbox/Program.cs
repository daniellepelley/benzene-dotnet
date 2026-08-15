using System.Globalization;
using Benzene.Abstractions.Serialization;
using Benzene.Clients;
using Benzene.Core.Middleware;
using Benzene.Example.Outbox.Domain;
using Benzene.Example.Outbox.Store;
using Benzene.Microsoft.Dependencies;
using Benzene.Outbox;
using Benzene.Results;
using Microsoft.Extensions.DependencyInjection;
using JsonSerializer = Benzene.Core.MessageHandlers.Serialization.JsonSerializer;
using Void = Benzene.Abstractions.Results.Void;

// Demonstrates docs/patterns/transactional-outbox.md and the real, shipped Benzene.Outbox package
// (docs/cookbooks/transactional-outbox.md): capturing an outbound send durably instead of sending it
// inline, so a crash or transport outage can never silently lose it.

Console.WriteLine("=== 1) The problem: a naive dual write can lose the send ===");
NaiveDualWrite.Run();

Console.WriteLine();
Console.WriteLine("=== 2) OutboxWriteMode.Immediate: durable capture + reliable relay ===");
await RunImmediateModeScenarioAsync();

Console.WriteLine();
Console.WriteLine("=== 3) OutboxWriteMode.Transactional: the write and the capture commit together ===");
await RunTransactionalModeScenarioAsync();

return;

static async Task RunImmediateModeScenarioAsync()
{
    var orders = new Orders();
    var downstream = new List<string>();

    var services = new ServiceCollection();
    services.AddTransient<ISerializer, JsonSerializer>();
    var container = new MicrosoftBenzeneServiceContainer(services);
    container.AddOutbox(); // WriteMode defaults to Immediate
    container.AddInMemoryOutboxStore();
    container.AddOutboundRouting(routing => routing
        .Route("order:created", pipeline => pipeline
            .UseOutbox() // capture point - terminal on a fresh send, pass-through on a relay dispatch
            .OnRequest(context =>
            {
                // only ever reached on a relay dispatch (capture short-circuits before this runs)
                var payload = (CreateOrderRequest)context.Request;
                downstream.Add($"downstream received order:created for {payload.Id} ({payload.Customer}, {FormatUsd(payload.Total)})");
                context.Response = BenzeneResult.Accepted<Void>();
            })));

    var provider = services.BuildServiceProvider();
    var resolver = new MicrosoftServiceResolverAdapter(provider);
    var sender = resolver.GetService<IBenzeneMessageSender>();
    var store = resolver.GetService<IOutboxStore>();
    var dispatcher = resolver.GetService<IOutboxDispatcher>();

    async Task<string> CreateOrderAsync(string customer)
    {
        var order = new Order { Id = Guid.NewGuid(), Customer = customer, Total = 249.99m };
        orders.Add(order); // (1) state write
        var request = new CreateOrderRequest { Id = order.Id, Customer = order.Customer, Total = order.Total };
        var result = await sender.SendAsync<CreateOrderRequest, Void>("order:created", request); // (2) captured, not sent yet
        return $"{result.Status} (order {order.Id})";
    }

    Console.WriteLine($"  handler: {await CreateOrderAsync("Acme Ltd")}");
    Console.WriteLine("  captured durably - from this point on the send survives a crash, even though writing the");
    Console.WriteLine("  order and capturing the send were still two separate operations, not one atomic commit.");

    Console.WriteLine();
    Console.WriteLine("  --- simulated crash here: process dies before the relay ever runs ---");
    Console.WriteLine("  --- simulated restart: a fresh relay pass (IOutboxDispatcher.RunOnceAsync) finds it ---");
    var firstPass = await dispatcher.RunOnceAsync();
    Console.WriteLine($"  dispatched {firstPass.Dispatched}, rescheduled {firstPass.Rescheduled}, parked {firstPass.Parked}");
    downstream.ForEach(line => Console.WriteLine($"  {line}"));
    downstream.Clear();

    Console.WriteLine();
    Console.WriteLine("  --- a second relay pass, nothing new happened: correctly a no-op ---");
    var secondPass = await dispatcher.RunOnceAsync();
    Console.WriteLine($"  dispatched {secondPass.Dispatched}");

    Console.WriteLine();
    Console.WriteLine("  --- a second order, to show at-least-once redelivery ---");
    Console.WriteLine($"  handler: {await CreateOrderAsync("Wayne Enterprises")}");
    Console.WriteLine("  now suppose the relay claims it, sends it successfully, but crashes before marking it dispatched");
    Console.WriteLine("  (mirroring exactly what OutboxDispatcher.DispatchEnvelopeAsync does internally, minus the final mark):");
    var claimed = await store.ClaimDueAsync(batchSize: 10, lease: TimeSpan.FromMilliseconds(1));
    foreach (var envelope in claimed)
    {
        // A relay dispatch needs its own scope, marked via OutboxDispatchScope.Begin(...), so
        // OutboxMiddleware passes the send through instead of re-capturing it as a fresh envelope -
        // exactly what IOutboxDispatcher does internally before it calls IBenzeneMessageSender.
        using var dispatchScope = provider.CreateScope();
        var dispatchResolver = new MicrosoftServiceResolverAdapter(dispatchScope.ServiceProvider);
        dispatchResolver.GetService<OutboxDispatchScope>().Begin(envelope.Headers);

        var payload = (CreateOrderRequest)dispatchResolver.GetService<ISerializer>()
            .Deserialize(typeof(CreateOrderRequest), envelope.Payload)!;
        await dispatchResolver.GetService<IBenzeneMessageSender>()
            .SendAsync<CreateOrderRequest, Void>(envelope.Topic, payload); // succeeds...
        // ...but store.MarkDispatchedAsync is deliberately never called - simulating the crash right there.
    }
    downstream.ForEach(line => Console.WriteLine($"  {line}"));
    downstream.Clear();

    await Task.Delay(10); // let the deliberately tiny lease above lapse, so it's claimable again
    var redeliveryPass = await dispatcher.RunOnceAsync();
    Console.WriteLine($"  next relay pass: dispatched {redeliveryPass.Dispatched} (again)");
    downstream.ForEach(line => Console.WriteLine($"  {line}"));
    Console.WriteLine("  at-least-once, not exactly-once: the same order:created was delivered twice.");
    Console.WriteLine("  a consumer needs to be idempotent for that to be safe - Benzene.Outbox stamps an");
    Console.WriteLine("  idempotency-key header by default, so Benzene.Idempotency's UseIdempotency() dedups");
    Console.WriteLine("  redeliveries like this one for free (see docs/cookbooks/idempotency.md).");
}

static async Task RunTransactionalModeScenarioAsync()
{
    var orders = new Orders();
    var downstream = new List<string>();

    var services = new ServiceCollection();
    services.AddTransient<ISerializer, JsonSerializer>();
    services.AddSingleton(orders);
    services.AddScoped<TransactionalOrders>();
    var container = new MicrosoftBenzeneServiceContainer(services);
    container.AddOutbox(o => o.WriteMode = OutboxWriteMode.Transactional);
    container.AddInMemoryOutboxStore();
    container.AddOutboundRouting(routing => routing
        .Route("order:created", pipeline => pipeline
            .UseOutbox()
            .OnRequest(context =>
            {
                var payload = (CreateOrderRequest)context.Request;
                downstream.Add($"downstream received order:created for {payload.Id} ({payload.Customer}, {FormatUsd(payload.Total)})");
                context.Response = BenzeneResult.Accepted<Void>();
            })));

    var provider = services.BuildServiceProvider();

    async Task<string> CreateOrderAsync(string customer, bool reject = false)
    {
        // One DI scope = one "unit of work", mirroring what a real scoped handler invocation looks
        // like - IOutboxStage/BufferedOutboxStage are registered scoped for exactly this reason.
        using var scope = provider.CreateScope();
        var resolver = new MicrosoftServiceResolverAdapter(scope.ServiceProvider);
        var sender = resolver.GetService<IBenzeneMessageSender>();
        var transactionalOrders = scope.ServiceProvider.GetRequiredService<TransactionalOrders>();

        var order = new Order { Id = Guid.NewGuid(), Customer = customer, Total = 89.00m };
        var request = new CreateOrderRequest { Id = order.Id, Customer = order.Customer, Total = order.Total };

        // Transactional mode stages the envelope instead of writing it - nothing durable has happened yet.
        var captureResult = await sender.SendAsync<CreateOrderRequest, Void>("order:created", request);

        if (reject || !captureResult.IsSuccessful)
        {
            // Never commit: the scope disposes with the staged envelope still undrained, so it's
            // discarded - and we never called TransactionalOrders.CommitAsync, so no order either.
            return "rejected - nothing committed (no order row, no phantom outbox envelope)";
        }

        await transactionalOrders.CommitAsync(order); // the order AND the drained envelope, together
        return $"{captureResult.Status} (order {order.Id}) - committed together";
    }

    Console.WriteLine($"  handler: {await CreateOrderAsync("Acme Ltd")}");
    Console.WriteLine($"  committed together: {orders.All.Count} order row(s)");

    var dispatcher = new MicrosoftServiceResolverAdapter(provider).GetService<IOutboxDispatcher>();
    var relayPass = await dispatcher.RunOnceAsync();
    Console.WriteLine($"  relay dispatched {relayPass.Dispatched} envelope(s)");
    downstream.ForEach(line => Console.WriteLine($"  {line}"));

    Console.WriteLine();
    Console.WriteLine("  --- a rejected order: the handler decides not to commit ---");
    Console.WriteLine($"  handler: {await CreateOrderAsync("On Hold", reject: true)}");
    Console.WriteLine($"  still {orders.All.Count} order row(s) - the rejected attempt added nothing");
    var secondPass = await dispatcher.RunOnceAsync();
    Console.WriteLine($"  relay dispatched {secondPass.Dispatched} envelope(s) - nothing to send for the rejected order");

    Console.WriteLine();
    Console.WriteLine("  note: the two writes above (Orders.Add and IOutboxStore.AddAsync in TransactionalOrders)");
    Console.WriteLine("  are still two in-memory operations, not a real database transaction - see README.md.");
}

static string FormatUsd(decimal amount) => $"${amount.ToString("N2", CultureInfo.InvariantCulture)}";

/// <summary>
/// The dual-write problem docs/patterns/transactional-outbox.md opens with, made concrete: a write and
/// a send as two separately-failable actions, with a real crash point between them. No Benzene
/// machinery here on purpose - this is the "before" the rest of this example fixes.
/// </summary>
internal static class NaiveDualWrite
{
    public static void Run()
    {
        var store = new List<Order>();
        var delivered = new List<string>();

        void InsertOrder(Order order) => store.Add(order); // (1) committed
        void SendEvent(Order order, bool crashBeforeSend)
        {
            if (crashBeforeSend)
            {
                Console.WriteLine("  --- crash: the process dies here, between the write and the send ---");
                return; // (2) never runs
            }

            delivered.Add($"downstream received order:created for {order.Id}");
        }

        var order = new Order { Id = Guid.NewGuid(), Customer = "Acme Ltd", Total = 99.50m };
        InsertOrder(order);
        Console.WriteLine($"  order {order.Id} committed to the store (store now has {store.Count} row(s))");
        SendEvent(order, crashBeforeSend: true);

        Console.WriteLine($"  downstream events received: {delivered.Count}");
        Console.WriteLine("  the order is real and committed, but nothing downstream will ever hear about it.");
    }
}
