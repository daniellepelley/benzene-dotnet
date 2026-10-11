using Benzene.HealthChecks.Core;

namespace Benzene.HealthChecks.Schema;

/// <summary>
/// Provides extension methods for adding the provider-side schema/contract health check.
/// </summary>
public static class SchemaHealthCheckExtensions
{
    /// <summary>
    /// Adds a <see cref="SchemaHealthCheck"/> to the health-check pipeline: it publishes the served
    /// contract document's <c>contractHash</c>.
    /// </summary>
    /// <param name="source">The health-check builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHealthCheckBuilder AddSchemaHealthCheck(this IHealthCheckBuilder source)
    {
        return source.AddHealthCheck(resolver => new SchemaHealthCheck(resolver));
    }
}
