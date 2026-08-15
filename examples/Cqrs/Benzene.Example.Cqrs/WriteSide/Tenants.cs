namespace Benzene.Example.Cqrs.WriteSide;

/// <summary>The tenant core service's own store — share-nothing, one aggregate, never touched by anything else.</summary>
public sealed class Tenants
{
    private readonly Dictionary<Guid, string> _companyNames = [];

    public void Add(Guid tenantId, string companyName) => _companyNames[tenantId] = companyName;

    public string? GetCompanyName(Guid tenantId) => _companyNames.GetValueOrDefault(tenantId);
}
