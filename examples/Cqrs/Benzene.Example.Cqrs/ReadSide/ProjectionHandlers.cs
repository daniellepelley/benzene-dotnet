using Benzene.Abstractions.MessageHandlers;
using Benzene.Core.MessageHandlers;
using Benzene.Example.Cqrs.Domain;

namespace Benzene.Example.Cqrs.ReadSide;

/// <summary>Ordinary message handlers, same as any inbound handler — the only thing distinguishing a
/// "projection" is that it folds an event into this service's own read store instead of answering a request.</summary>
[Message("tenant:created")]
public class ProjectTenantHandler(ReadStore view) : IMessageHandler<TenantCreated>
{
    public Task HandleAsync(TenantCreated e)
    {
        view.UpsertTenant(e.TenantId, e.CompanyName);
        return Task.CompletedTask;
    }
}

[Message("user:created")]
public class ProjectUserHandler(ReadStore view) : IMessageHandler<UserCreated>
{
    public Task HandleAsync(UserCreated e)
    {
        view.AddUserToTenant(e.TenantId, e.UserId, e.Email);
        return Task.CompletedTask;
    }
}
