using Benzene.Schema.OpenApi.EventService;

namespace Benzene.Schema.OpenApi.Abstractions;

/// <summary>
/// Implemented by spec document builders that carry the deployment-side fields of the contract
/// document (contract-document.md §1: <c>runtime</c>, <c>placement</c>, <c>profile</c>) and its
/// <c>degraded</c> list.
/// </summary>
public interface IConsumesDeployment<out TBuilder>
{
    /// <summary>Stamps the deployment-side fields.</summary>
    TBuilder AddDeployment(ContractDocumentDeployment deployment);

    /// <summary>Names a member (<see cref="ContractDocumentMembers"/>) the producer could not determine.</summary>
    TBuilder AddDegraded(string member);
}
