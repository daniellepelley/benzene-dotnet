using System.Text.Json;
using Benzene.Abstractions.DI;
using Benzene.HealthChecks.Core;
using Benzene.Schema.OpenApi;

namespace Benzene.HealthChecks.Schema;

/// <summary>
/// A provider-side contract health check: it publishes the <c>contractHash</c> of the contract
/// document this service <b>serves</b> at <c>benzene:spec</c> (contract-document.md §6), so consumers
/// can tell whether the contract still matches what their generated client was built against.
/// </summary>
/// <remarks>
/// The hash is read from the served document, never recomputed by a private rule: a generated
/// client with no include-list embeds that same value (<c>{Service}ServiceClient.HashCode</c>), so
/// equal means no drift and different means the contract changed. The document comes from the
/// process's <see cref="SpecCache"/> when <c>UseSpec</c> registered one - so it is built once - and is
/// otherwise derived the way <c>benzene:spec</c> derives it. The consumer side of the loop is
/// <c>Benzene.Clients.HealthChecks.ClientHealthCheckProcessor</c>.
/// </remarks>
public class SchemaHealthCheck : IHealthCheck
{
    private readonly Func<string> _servedDocument;

    /// <summary>
    /// Initializes a check that reads the document <c>benzene:spec</c> serves from <paramref name="resolver"/>.
    /// </summary>
    /// <param name="resolver">Resolves the registrations the served document is derived from.</param>
    public SchemaHealthCheck(IServiceResolver resolver)
        : this(() => ServedDocument(resolver))
    {
    }

    /// <summary>Initializes a check over an already-served contract document's JSON.</summary>
    /// <param name="servedDocument">Returns the served contract document.</param>
    public SchemaHealthCheck(Func<string> servedDocument)
    {
        _servedDocument = servedDocument;
    }

    /// <inheritdoc />
    public string Type => SchemaHealthCheckConstants.Type;

    /// <inheritdoc />
    public Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(_servedDocument());
        var hashCode = document.RootElement.TryGetProperty("contractHash", out var hash) && hash.ValueKind == JsonValueKind.String
            ? hash.GetString()!
            : string.Empty;

        var data = new Dictionary<string, object>
        {
            [SchemaHealthCheckConstants.HashCodeKey] = hashCode,
        };

        return Task.FromResult(HealthCheckResult.CreateInstance(!string.IsNullOrEmpty(hashCode), SchemaHealthCheckConstants.Type, data));
    }

    private static string ServedDocument(IServiceResolver resolver)
    {
        var request = new SpecRequest(SpecBuilder.BenzeneType, SpecBuilder.JsonFormat);
        var cache = resolver.TryGetService<SpecCache>();
        return cache != null
            ? cache.GetOrBuild(request, r => new SpecBuilder().CreateSpec(resolver, r))
            : new SpecBuilder().CreateSpec(resolver, request);
    }
}
