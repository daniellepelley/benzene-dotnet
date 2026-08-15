using Benzene.Example.Cqrs.Domain;

namespace Benzene.Example.Cqrs.ReadSide;

/// <summary>
/// The read model's own denormalized store — separate, derived, disposable, rebuildable by replaying
/// the events that fed it. Nobody but this service's own projection handlers writes to it; nobody but
/// this service's own query handlers reads from it.
/// </summary>
public sealed class ReadStore
{
    private readonly Dictionary<Guid, TenantWithUsersView> _tenants = [];

    /// <summary>Idempotent upsert: re-projecting the same event just overwrites the same field, safe under
    /// at-least-once redelivery and full replay.</summary>
    public void UpsertTenant(Guid tenantId, string companyName)
    {
        Get(tenantId).CompanyName = companyName;
    }

    /// <summary>Idempotent: adding the same user twice is a no-op, not a duplicate append.</summary>
    public void AddUserToTenant(Guid tenantId, Guid userId, string email)
    {
        var view = Get(tenantId);
        if (view.Users.All(u => u.UserId != userId))
        {
            view.Users.Add(new UserSummary { UserId = userId, Email = email });
        }
    }

    public TenantWithUsersView? Find(Guid tenantId) => _tenants.GetValueOrDefault(tenantId);

    // Events can arrive out of order (Benzene.Outbox makes no ordering guarantee across envelopes) -
    // a user:created for a tenant we haven't projected yet still needs somewhere to land. The
    // eventual tenant:created upsert fills in the real company name whenever it arrives.
    private TenantWithUsersView Get(Guid tenantId)
    {
        if (!_tenants.TryGetValue(tenantId, out var view))
        {
            view = new TenantWithUsersView { TenantId = tenantId };
            _tenants[tenantId] = view;
        }

        return view;
    }
}
