namespace Benzene.Example.Cqrs.Domain;

/// <summary>The write-side commands — deliberately distinct shapes from the events they cause, even
/// though this demo's fields happen to match; a command names an intent, an event names a fact.</summary>
public sealed class CreateTenantRequest
{
    public required Guid TenantId { get; init; }
    public required string CompanyName { get; init; }
}

public sealed class CreateUserRequest
{
    public required Guid TenantId { get; init; }
    public required Guid UserId { get; init; }
    public required string Email { get; init; }
}
