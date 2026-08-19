using System;
using Benzene.Abstractions;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Logging;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Abstractions.Middleware;
using Benzene.Core.Middleware;

namespace Benzene.Diagnostics.Correlation;

public static class Extensions
{
    public static IBenzeneServiceContainer AddCorrelationId(this IBenzeneServiceContainer services)
    {
        return services.AddScoped<ICorrelationId, CorrelationId>();
    }

    public static ILogContextBuilder<TContext> WithCorrelationId<TContext>(this ILogContextBuilder<TContext> source)
    {
        source.Register(x => x.AddCorrelationId());
        return source.OnRequest("correlationId", resolver =>
        {
            var correlationId = resolver.GetService<ICorrelationId>();
            return correlationId.Get();
        });
    }

    /// <summary>
    /// Looks up a single header, matching the key case-insensitively.
    /// </summary>
    public static string GetHeader<TContext>(this IMessageHeadersGetter<TContext> source, TContext context, string key)
        => GetHeader(source, context, new[] { key });

    /// <summary>
    /// Looks up the first of several candidate header keys that is present (matched case-insensitively),
    /// in the order given.
    /// </summary>
    public static string GetHeader<TContext>(this IMessageHeadersGetter<TContext> source, TContext context, IReadOnlyList<string> keys)
    {
        var headers = source.GetHeaders(context);
        foreach (var key in keys)
        {
            foreach (var pair in headers)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(pair.Value))
                {
                    return pair.Value;
                }
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Adds <see cref="InboundCorrelationIdMiddleware{TContext}"/> to an inbound pipeline, so the
    /// caller's correlation id is read off the incoming message's headers and put back into
    /// <see cref="ICorrelationId"/> - the inbound counterpart of <c>Benzene.Clients</c>'s outbound
    /// <c>UseCorrelationId()</c>, which stamps the same header on the way out. Without it a consumer's
    /// correlation id is a fresh GUID and the chain breaks at every hop.
    /// </summary>
    /// <typeparam name="TContext">The transport context type this pipeline handles.</typeparam>
    /// <param name="app">The pipeline builder to add the middleware to.</param>
    /// <param name="correlationKey">
    /// The header key to read. Pass explicitly to override for this pipeline only; leave <c>null</c>
    /// (the default) to use a DI-registered <see cref="CorrelationHeaderOptions"/> if one is registered,
    /// or <see cref="CorrelationHeaderDefaults.HeaderKey"/> otherwise - the same resolution order the
    /// outbound <c>UseCorrelationId()</c> uses, so both directions move together.
    /// </param>
    /// <returns>The pipeline builder, for method chaining.</returns>
    /// <remarks>
    /// <para>
    /// Registers <see cref="ICorrelationId"/> (via <see cref="AddCorrelationId"/>) if nothing else has;
    /// an earlier registration of your own wins.
    /// </para>
    /// <para>
    /// This is a shorthand over the explicit form, which you can write yourself from public API:
    /// <c>app.Use(resolver =&gt; new InboundCorrelationIdMiddleware&lt;TContext&gt;(resolver.GetService&lt;ICorrelationId&gt;(),
    /// resolver.GetService&lt;IMessageHeadersGetter&lt;TContext&gt;&gt;(), key))</c>. Drop to that to supply your own
    /// <see cref="ICorrelationId"/> or headers getter.
    /// </para>
    /// <para>
    /// Transport-agnostic, exactly like <see cref="W3CTraceContextExtensions.UseW3CTraceContext{TContext}"/>:
    /// it works on any pipeline whose context has an <see cref="IMessageHeadersGetter{TContext}"/>
    /// registered. Because the middleware resolves both of its dependencies when the pipeline is built,
    /// a pipeline missing either one is named by the start-up checks rather than failing on the message
    /// path. Add it near the top of the pipeline, before anything that logs or sends.
    /// </para>
    /// </remarks>
    public static IMiddlewarePipelineBuilder<TContext> UseCorrelationId<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app, string? correlationKey = null)
    {
        app.Register(x => x.TryAddScoped<ICorrelationId, CorrelationId>());

        return app.Use(resolver => new InboundCorrelationIdMiddleware<TContext>(
            resolver.GetService<ICorrelationId>(),
            resolver.GetService<IMessageHeadersGetter<TContext>>(),
            correlationKey ?? resolver.TryGetService<CorrelationHeaderOptions>()?.HeaderKey ?? CorrelationHeaderDefaults.HeaderKey));
    }
}
