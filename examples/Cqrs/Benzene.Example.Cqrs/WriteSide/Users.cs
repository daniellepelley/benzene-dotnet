namespace Benzene.Example.Cqrs.WriteSide;

/// <summary>The user core service's own store — a separate aggregate, a separate database in a real
/// deployment, referencing its tenant by id only (never a foreign key into the tenant service's own store).</summary>
public sealed class Users
{
    public sealed record Entry(Guid UserId, Guid TenantId, string Email);

    private readonly List<Entry> _users = [];

    public void Add(Guid tenantId, Guid userId, string email) => _users.Add(new Entry(userId, tenantId, email));

    public IReadOnlyList<Entry> ForTenant(Guid tenantId) => _users.Where(u => u.TenantId == tenantId).ToList();
}
