namespace Benzene.Example.Cqrs.Domain;

/// <summary>
/// "A tenant and all its users" — the read model's whole reason to exist: a shape no single
/// share-nothing core service is allowed to hold (the tenant service may not know its users; see
/// core-services.md's directional-dependency rule), assembled once, at event time, so a query never
/// has to fan out and stitch it together at read time.
/// </summary>
public sealed class TenantWithUsersView
{
    public required Guid TenantId { get; init; }
    public string CompanyName { get; set; } = "(pending)";
    public List<UserSummary> Users { get; } = [];
}

public sealed class UserSummary
{
    public required Guid UserId { get; init; }
    public required string Email { get; init; }
}

public sealed class GetTenantWithUsersRequest
{
    public required Guid TenantId { get; init; }
}
