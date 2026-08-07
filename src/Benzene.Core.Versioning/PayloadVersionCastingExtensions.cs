using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.MessageHandlers.Request;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Core.MessageHandlers.Request;
using Benzene.Core.MessageHandlers.Response;
using Benzene.Core.Versioning.Request;
using Benzene.Core.Versioning.Response;
using Benzene.Core.Versioning.Schemas;

namespace Benzene.Core.Versioning;

/// <summary>
/// DI wiring for transparent payload-version casting (docs/specification/versioning.md §4, mechanism B).
/// </summary>
public static class PayloadVersionCastingExtensions
{
    /// <summary>
    /// Wraps the request and response payload mappers for <typeparamref name="TContext"/> with the
    /// version-casting decorators, so an incoming older-version payload is upcast into the handler's
    /// declared request type and the response is downcast back to the requested version - the handler
    /// only ever sees its own (canonical/latest) schema. A topic with no registered casters, or a
    /// message that signals no version, is unaffected: the decorators delegate straight to the mappers
    /// they wrap.
    /// </summary>
    /// <remarks>
    /// Call this after the transport's own registration (e.g. <c>UseHttp</c>/<c>AddSqs</c> +
    /// <c>AddMessageHandlers</c>), so the closed <see cref="IRequestMapper{TContext}"/>/
    /// <see cref="IResponsePayloadMapper{TContext}"/> registrations here take precedence, and pair it
    /// with <c>RegisterSchemaCastDefinitions</c>/<c>RegisterPayloadSchemaVersions</c> to supply the
    /// casters. It wraps the framework-default mappers (<see cref="MultiSerializerOptionsRequestMapper{TContext}"/>
    /// / <see cref="DefaultResponsePayloadMapper{TContext}"/>); a transport that registers a bespoke
    /// request mapper (e.g. gRPC's protobuf-bridging one) wraps that instead via
    /// <see cref="UsePayloadVersionRequestCasting{TContext,TInnerRequestMapper}"/> (see
    /// <c>Benzene.Grpc.Versioning</c>).
    /// </remarks>
    /// <typeparam name="TContext">The transport-specific context type to enable casting for.</typeparam>
    /// <param name="services">The service container to register into.</param>
    /// <returns>The same container, for chaining.</returns>
    public static IBenzeneServiceContainer UsePayloadVersionCasting<TContext>(this IBenzeneServiceContainer services)
        where TContext : class
    {
        // Request side wraps the framework-default request mapper. A transport with a bespoke request
        // mapper (gRPC) re-wires the request side over its own mapper via the overload below.
        services.UsePayloadVersionRequestCasting<TContext, MultiSerializerOptionsRequestMapper<TContext>>();

        // Self-register the framework-default response mapper as its own concrete type so the decorator
        // below can resolve and wrap it (it's otherwise only registered under its interface).
        services.TryAddScoped<DefaultResponsePayloadMapper<TContext>>();

        services.AddScoped<IResponsePayloadMapper<TContext>>(resolver =>
            new CastingResponsePayloadMapper<TContext>(
                resolver.GetService<DefaultResponsePayloadMapper<TContext>>(),
                resolver.GetService<IMessageVersionGetter<TContext>>(),
                resolver.GetService<IMessageTopicGetter<TContext>>(),
                resolver.TryGetService<ISchemaCasters>()));

        return services;
    }

    /// <summary>
    /// Wraps the request payload mapper for <typeparamref name="TContext"/> with the version-casting
    /// decorator over a <b>named concrete inner mapper</b>, rather than the framework default. Use this
    /// for a transport whose real <see cref="IRequestMapper{TContext}"/> is bespoke - most notably gRPC,
    /// whose <c>GrpcRequestMapper</c> bridges protobuf and so cannot be replaced by the default
    /// serializer-based mapper (docs/specification/versioning.md §4.2.1). The decorator reads the wire
    /// body as the incoming version's CLR shape through <typeparamref name="TInnerRequestMapper"/> - so
    /// the transport's own deserialization still runs - then upcasts into the handler's request type.
    /// A topic with no registered casters, or a message signalling no version, delegates straight through.
    /// </summary>
    /// <remarks>
    /// Register this <b>after</b> the transport's own request-mapper registration so this closed
    /// <see cref="IRequestMapper{TContext}"/> wins, and after any <see cref="UsePayloadVersionCasting{TContext}"/>
    /// so it re-points the request side at <typeparamref name="TInnerRequestMapper"/>. The response side is
    /// unaffected - gRPC writes its response straight to protobuf via its result setter and has no
    /// response payload mapper to cast.
    /// </remarks>
    /// <typeparam name="TContext">The transport-specific context type to enable request casting for.</typeparam>
    /// <typeparam name="TInnerRequestMapper">The transport's real request mapper, wrapped as the inner.</typeparam>
    /// <param name="services">The service container to register into.</param>
    /// <returns>The same container, for chaining.</returns>
    public static IBenzeneServiceContainer UsePayloadVersionRequestCasting<TContext, TInnerRequestMapper>(this IBenzeneServiceContainer services)
        where TContext : class
        where TInnerRequestMapper : class, IRequestMapper<TContext>
    {
        // Self-register the inner mapper as its own concrete type so the decorator can resolve and wrap
        // it (it's otherwise only registered under the IRequestMapper<TContext> interface).
        services.TryAddScoped<TInnerRequestMapper>();

        services.AddScoped<IRequestMapper<TContext>>(resolver =>
            new CastingRequestMapper<TContext>(
                resolver.GetService<TInnerRequestMapper>(),
                resolver.GetService<IMessageVersionGetter<TContext>>(),
                resolver.GetService<IMessageTopicGetter<TContext>>(),
                resolver.TryGetService<ISchemaCasters>()));

        return services;
    }
}
