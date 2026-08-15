using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Example.Cqrs.Domain;
using Benzene.Results;

namespace Benzene.Example.Cqrs.ReadSide;

/// <summary>The query side: "a tenant and all its users" answered by one indexed read against a store
/// shaped for exactly this question — no fan-out to the tenant service and the user service, no stitching.</summary>
[Message("tenant:users:list")]
public class GetTenantWithUsersHandler(ReadStore view) : IMessageHandler<GetTenantWithUsersRequest, TenantWithUsersView>
{
    public Task<IBenzeneResult<TenantWithUsersView>> HandleAsync(GetTenantWithUsersRequest request)
    {
        var found = view.Find(request.TenantId);
        return Task.FromResult(found is null
            ? BenzeneResult.NotFound<TenantWithUsersView>()
            : BenzeneResult.Ok(found));
    }
}
