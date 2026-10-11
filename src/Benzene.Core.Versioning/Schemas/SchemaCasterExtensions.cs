using Benzene.Abstractions.DI;

namespace Benzene.Core.Versioning.Schemas;

public static class SchemaCasterExtensions
{
    /// <summary>
    /// Registers an <see cref="ISchemaCasters"/> singleton that expands the individually registered
    /// <see cref="ISchemaCaster"/> instances into the full set of casters required by
    /// <paramref name="payloadSchemaVersions"/>, composing multi-step chains (e.g. V1 -> V2 -> V3)
    /// where no direct caster exists.
    /// </summary>
    public static IBenzeneServiceContainer RegisterPayloadSchemaVersions(this IBenzeneServiceContainer services, IEnumerable<PayloadSchemaVersions> payloadSchemaVersions)
    {
        services.AddSingleton<ISchemaCasters>(x =>
                new SchemaCasters(
                    new SchemaCastDefinitionsExpander().Expand(x.GetServices<ISchemaCaster>().ToArray(),
                        payloadSchemaVersions.ToArray())));
        // Version selection consults the casts: a version with no handler of its own routes to the
        // handler it casts to (core-concepts §2, step 2).
        return services.AddSingleton<Benzene.Abstractions.MessageHandlers.IPayloadVersionCasts>(x =>
            new SchemaCasterVersionCasts(x.GetService<ISchemaCasters>()));
    }

    /// <summary>
    /// Registers each schema caster built by <paramref name="action"/> as an
    /// <see cref="ISchemaCaster"/> singleton.
    /// </summary>
    public static IBenzeneServiceContainer RegisterSchemaCastDefinitions(this IBenzeneServiceContainer services, Action<SchemaCastersBuilder> action)
    {
        var builder = new SchemaCastersBuilder();

        action(builder);

        foreach (var schemaCastDefinition in builder.Build())
        {
            _ = services.AddSingleton(schemaCastDefinition);
        }

        return services;
    }
}
