namespace Benzene.Example.Cqrs.Domain;

/// <summary>The domain events the two write-side core services emit — the read model's only input.</summary>
public sealed class TenantCreated
{
    public required Guid TenantId { get; init; }
    public required string CompanyName { get; init; }
}

public sealed class UserCreated
{
    public required Guid TenantId { get; init; }
    public required Guid UserId { get; init; }
    public required string Email { get; init; }
}
