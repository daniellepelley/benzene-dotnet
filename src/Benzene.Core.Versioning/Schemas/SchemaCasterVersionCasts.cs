using Benzene.Abstractions.MessageHandlers;

namespace Benzene.Core.Versioning.Schemas;

/// <summary>
/// <see cref="IPayloadVersionCasts"/> over the registered <see cref="ISchemaCasters"/>: what lets the
/// default version selector route a message whose version has no handler of its own to the handler its
/// payload can be cast to (docs/specification/core-concepts.md §2, step 2).
/// </summary>
/// <remarks>
/// <para>A version <c>v</c> of a topic reaches a handler's request type when:</para>
/// <list type="number">
/// <item>a cast from <c>v</c> to that type is registered (chained casts are already expanded into the
/// registry);</item>
/// <item><c>v</c> <i>names</i> that type - it is the from- or to-schema of a registered cast carrying
/// exactly that CLR type - so a message in the handler's own version needs no cast; or</item>
/// <item>the request type takes part in none of the topic's casts (it does not vary by version - the
/// topic versions only its responses) and <c>v</c> is a version the topic's casts name: the request
/// arrives in the same shape whatever its version, so the identity is the cast.</item>
/// </list>
/// <para>A version none of the topic's casts name reaches nothing - it is <c>not-found</c>. Registered by
/// <see cref="PayloadVersioningExtensions.AddPayloadVersioning"/> and
/// <see cref="SchemaCasterExtensions.RegisterPayloadSchemaVersions"/>.</para>
/// </remarks>
public class SchemaCasterVersionCasts : IPayloadVersionCasts
{
    private readonly ISchemaCasters _casters;

    /// <summary>Initializes a new instance over the service's caster registry.</summary>
    /// <param name="casters">The registered (expanded) casters.</param>
    public SchemaCasterVersionCasts(ISchemaCasters casters)
    {
        _casters = casters;
    }

    /// <inheritdoc />
    public bool CanCast(string topicId, string fromVersion, Type requestType)
    {
        if (_casters.TryGetSchemaCaster(topicId, fromVersion, requestType, out _))
        {
            return true;
        }

        var versionIsKnown = false;
        var requestTypeIsVersioned = false;
        foreach (var caster in _casters.GetAll())
        {
            if (!string.Equals(caster.Definition.Topic, topicId, StringComparison.Ordinal))
            {
                continue;
            }

            var isFrom = string.Equals(caster.Definition.FromSchema, fromVersion, StringComparison.Ordinal);
            var isTo = string.Equals(caster.Definition.ToSchema, fromVersion, StringComparison.Ordinal);
            if ((isFrom && caster.FromType == requestType) || (isTo && caster.ToType == requestType))
            {
                return true; // the version names the handler's own request type
            }

            versionIsKnown |= isFrom || isTo;
            requestTypeIsVersioned |= caster.FromType == requestType || caster.ToType == requestType;
        }

        // A request type no cast mentions does not vary by version: any version the topic knows arrives
        // in that same shape.
        return versionIsKnown && !requestTypeIsVersioned;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> GetVersions(string topicId)
    {
        return _casters.GetAll()
            .Where(x => string.Equals(x.Definition.Topic, topicId, StringComparison.Ordinal))
            .SelectMany(x => new[] { x.Definition.FromSchema, x.Definition.ToSchema })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
