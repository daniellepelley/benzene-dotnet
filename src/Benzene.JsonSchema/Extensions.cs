using Benzene.Abstractions.Middleware;
using Benzene.Core.Middleware;

namespace Benzene.JsonSchema;

/// <summary>
/// Middleware-pipeline registration extensions for JSON Schema request validation.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Registers JSON Schema request validation for <typeparamref name="TContext"/> onto a
    /// middleware pipeline builder.
    /// </summary>
    /// <param name="app">The pipeline builder to register JSON Schema validation onto.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<TContext> UseJsonSchema<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app)
        where TContext : class
    {
        app.Register(x => x.AddJsonSchema());
        return app.Use<TContext, JsonSchemaMiddleware<TContext>>();
    }
}