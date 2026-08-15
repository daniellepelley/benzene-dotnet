using Benzene.Abstractions.DI;
using Benzene.Abstractions.Serialization;
using Benzene.Clients;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.BenzeneMessage;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.Example.Cqrs.Domain;
using Benzene.Example.Cqrs.ReadSide;
using Benzene.Example.Cqrs.WriteSide;
using Benzene.Microsoft.Dependencies;
using Benzene.Outbox;
using Benzene.Results;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using JsonSerializer = Benzene.Core.MessageHandlers.Serialization.JsonSerializer;
using Void = Benzene.Abstractions.Results.Void;

// Demonstrates docs/patterns/cqrs-read-models.md's worked example: "a tenant and all its users" - a
// cross-aggregate query no single share-nothing core service is allowed to answer (the tenant service
// may not know its users), served instead by a read model projected from the domain events the write
// side emits, reliably relayed via Benzene.Outbox (see examples/Outbox for that half on its own).

var tenants = new Tenants();
var users = new Users();
var readStore = new ReadStore();

var services = new ServiceCollection();
services.AddLogging();
services.AddTransient<ISerializer, JsonSerializer>();
services.AddSingleton(readStore);
var container = new MicrosoftBenzeneServiceContainer(services);
container.AddOutbox(); // WriteMode defaults to Immediate - see examples/Outbox for Transactional mode
container.AddInMemoryOutboxStore();
container.AddBenzeneMessage();

// The read side: an ordinary Benzene message-handler pipeline (ProjectTenantHandler,
// ProjectUserHandler, GetTenantWithUsersHandler), entirely separate from the write side above - it
// only happens to share this process because this is a single-file demo.
var readPipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(container);
readPipeline.UseMessageHandlers(
    [typeof(ProjectTenantHandler), typeof(ProjectUserHandler), typeof(GetTenantWithUsersHandler)]);
var readApplication = new BenzeneMessageApplication(readPipeline.Build());
IServiceResolverFactory? readResolverFactory = null; // assigned once the provider is built, below

async Task DeliverToReadSideAsync(string topic, object payload)
{
    var request = new BenzeneMessageRequest { Topic = topic, Body = JsonConvert.SerializeObject(payload) };
    await readApplication.HandleAsync(request, readResolverFactory!);
}

// The relay path for both events: capture durably (UseOutbox), then - once actually dispatched -
// deliver into the read side's own pipeline. This is the "choreography" leg docs/patterns/choreography.md
// describes: the write side has no idea a read model exists on the other end of these topics.
container.AddOutboundRouting(routing => routing
    .Route("tenant:created", pipeline => pipeline
        .UseOutbox()
        .Use(async (context, _) =>
        {
            await DeliverToReadSideAsync(context.Topic, context.Request);
            context.Response = BenzeneResult.Accepted<Void>();
        }))
    .Route("user:created", pipeline => pipeline
        .UseOutbox()
        .Use(async (context, _) =>
        {
            await DeliverToReadSideAsync(context.Topic, context.Request);
            context.Response = BenzeneResult.Accepted<Void>();
        })));

var provider = services.BuildServiceProvider();
var resolver = new MicrosoftServiceResolverAdapter(provider);
readResolverFactory = new MicrosoftServiceResolverFactory(provider);
var sender = resolver.GetService<IBenzeneMessageSender>();
var dispatcher = resolver.GetService<IOutboxDispatcher>();

async Task<TenantWithUsersView?> QueryReadModelAsync(Guid tenantId)
{
    var request = new BenzeneMessageRequest
    {
        Topic = "tenant:users:list",
        Body = JsonConvert.SerializeObject(new GetTenantWithUsersRequest { TenantId = tenantId }),
    };
    var response = await readApplication.HandleAsync(request, readResolverFactory);
    return response.StatusCode == "not-found" ? null : JsonConvert.DeserializeObject<TenantWithUsersView>(response.Body);
}

var tenantId = Guid.NewGuid();
var user1Id = Guid.NewGuid();
var user2Id = Guid.NewGuid();

Console.WriteLine("=== 1) The write side: two share-nothing core services ===");
tenants.Add(tenantId, "Acme Ltd");
await sender.SendAsync<TenantCreated, Void>("tenant:created", new TenantCreated { TenantId = tenantId, CompanyName = "Acme Ltd" });
users.Add(tenantId, user1Id, "ada@acme.example");
await sender.SendAsync<UserCreated, Void>("user:created", new UserCreated { TenantId = tenantId, UserId = user1Id, Email = "ada@acme.example" });
Console.WriteLine("  tenant + one user written and captured (Immediate mode) - not sent to the read side yet");

Console.WriteLine();
Console.WriteLine("=== 2) Query the read model right now: it lags ===");
var beforeRelay = await QueryReadModelAsync(tenantId);
Console.WriteLine(beforeRelay is null
    ? "  not found - the read model hasn't seen either event yet (eventual consistency)"
    : $"  found early: {beforeRelay.CompanyName}, {beforeRelay.Users.Count} user(s)");
Console.WriteLine("  the core services themselves are current right now, if you need that path instead:");
Console.WriteLine($"  Tenants (direct): {tenants.GetCompanyName(tenantId)}");
Console.WriteLine($"  Users (direct, one query per aggregate - no cross-aggregate join available here): {users.ForTenant(tenantId).Count} user(s)");

Console.WriteLine();
Console.WriteLine("=== 3) Relay the events - IOutboxDispatcher.RunOnceAsync() ===");
var firstPass = await dispatcher.RunOnceAsync();
Console.WriteLine($"  dispatched {firstPass.Dispatched}");
var afterRelay = await QueryReadModelAsync(tenantId);
Console.WriteLine($"  read model, one indexed read, no fan-out: {afterRelay!.CompanyName}, users: {string.Join(", ", afterRelay.Users.Select(u => u.Email))}");

Console.WriteLine();
Console.WriteLine("=== 4) A second user, and idempotent replay ===");
users.Add(tenantId, user2Id, "grace@acme.example");
await sender.SendAsync<UserCreated, Void>("user:created", new UserCreated { TenantId = tenantId, UserId = user2Id, Email = "grace@acme.example" });
await dispatcher.RunOnceAsync();
var afterSecondUser = await QueryReadModelAsync(tenantId);
Console.WriteLine($"  read model now has {afterSecondUser!.Users.Count} user(s)");

Console.WriteLine("  replaying the very same user:created event again (a redelivery, or a full rebuild)...");
readStore.AddUserToTenant(tenantId, user2Id, "grace@acme.example"); // the same idempotent fold, called again
var afterReplay = await QueryReadModelAsync(tenantId);
Console.WriteLine($"  ...still {afterReplay!.Users.Count} user(s) - the upsert converges, it doesn't duplicate");

Console.WriteLine();
Console.WriteLine("=== 5) A second tenant, events arriving out of order ===");
var tenant2Id = Guid.NewGuid();
var tenant2UserId = Guid.NewGuid();
Console.WriteLine("  user:created lands before tenant:created has been projected at all:");
readStore.AddUserToTenant(tenant2Id, tenant2UserId, "bob@wayne.example");
var midway = await QueryReadModelAsync(tenant2Id);
Console.WriteLine($"  read model so far: \"{midway!.CompanyName}\" with {midway.Users.Count} user(s) - a placeholder shell, not an error");
readStore.UpsertTenant(tenant2Id, "Wayne Enterprises");
var settled = await QueryReadModelAsync(tenant2Id);
Console.WriteLine($"  once tenant:created arrives: \"{settled!.CompanyName}\" with {settled.Users.Count} user(s) - the join lands correctly regardless of arrival order");
