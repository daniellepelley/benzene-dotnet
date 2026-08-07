using Benzene.Abstractions.DI;
using Benzene.Core.Versioning;
using Benzene.Grpc;

namespace Benzene.Grpc.Versioning;

/// <summary>
/// Transparent, request-side payload-version casting for the gRPC transport (docs/specification/versioning.md
/// §4.2.1). An older-version request arriving over gRPC is upcast into the handler's declared request type,
/// so a handler only ever sees its own (canonical/latest) schema - exactly as for the serializer-based
/// transports, but honouring gRPC's protobuf bridge.
/// </summary>
/// <remarks>
/// <para>gRPC needs its own entry point because its real request mapper (<see cref="GrpcRequestMapper"/>)
/// converts protobuf, so it cannot be replaced by the framework-default serializer mapper that the generic
/// <see cref="PayloadVersionCastingExtensions.UsePayloadVersionCasting{TContext}"/> wraps. This method
/// declares the casters through the same <see cref="PayloadVersioningBuilder"/> as
/// <see cref="PayloadVersioningExtensions.AddPayloadVersioning"/> (same eager validation, same auto-derived
/// downcasts) and then re-points the request side at <see cref="GrpcRequestMapper"/>.</para>
/// <para>Only the request side is cast: gRPC writes its response straight to protobuf via its result setter
/// and has no response payload mapper, so there is nothing to downcast on the way out. If a service needs to
/// return an older response shape over gRPC, model it as a distinct topic/method rather than a cast.</para>
/// </remarks>
public static class GrpcPayloadVersioningExtensions
{
    /// <summary>
    /// Registers request-side payload-version casting for gRPC from a single fluent definition. Declare the
    /// versioned topics and their casters exactly as with <c>AddPayloadVersioning</c>; the gRPC transport
    /// context is wired for you - do not also call <c>ForContext&lt;GrpcContext&gt;()</c>.
    /// </summary>
    /// <param name="services">The service container to register into.</param>
    /// <param name="configure">Declares the versioned topics and, per topic, the versions and casters.</param>
    /// <returns>The same container, for chaining.</returns>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown at registration time if a caster references an undeclared version, or if the declared versions
    /// cannot all be reached by the registered casters (a missing conversion path) - misconfiguration fails
    /// the build, not the first message.
    /// </exception>
    public static IBenzeneServiceContainer AddGrpcPayloadVersioning(this IBenzeneServiceContainer services,
        Action<PayloadVersioningBuilder> configure)
    {
        // Declare the casters and validate them eagerly through the shared entry point. ForContext<GrpcContext>()
        // registers the version/topic getters and a (default-mapper) request decorator; we override the request
        // side below so it wraps gRPC's protobuf-bridging mapper instead.
        services.AddPayloadVersioning(v =>
        {
            configure(v);
            v.ForContext<GrpcContext>();
        });

        // Re-point the request side at the real gRPC request mapper (last registration wins). GrpcRequestMapper
        // reads the wire body as the incoming version's CLR shape via protobuf JSON, then CastingRequestMapper
        // upcasts it into the handler's request type.
        services.UsePayloadVersionRequestCasting<GrpcContext, GrpcRequestMapper>();

        return services;
    }
}
