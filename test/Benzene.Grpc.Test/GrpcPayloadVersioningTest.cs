using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.MessageHandlers.Request;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Versioning.Request;
using Benzene.Grpc.TestHelpers;
using Benzene.Grpc.Test.Protos;
using Benzene.Grpc.Versioning;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Grpc.Test;

/// <summary>
/// Coverage for <c>Benzene.Grpc.Versioning</c>: request-side payload-version casting over gRPC
/// (docs/specification/versioning.md §4.2.1). A handler written against the V2 schema transparently
/// serves a V1 gRPC caller - the request is read as V1 through gRPC's own protobuf bridge and upcast to
/// V2 before the handler - which the generic (default-mapper) casting could not do because gRPC's request
/// mapper is bespoke.
/// </summary>
public class GrpcPayloadVersioningTest
{
    // Distinct namespaces, so an incoming EchoRequest ({"name": ...} in proto3 JSON) reads as V1 and the
    // V1 -> V2 upcast injects Currency (a value that exists only because the caster ran).
    public static class V1
    {
        public class OrderPayload
        {
            public string Name { get; set; } = string.Empty;
            public int Quantity { get; set; }
        }
    }

    public static class V2
    {
        public class OrderPayload
        {
            public string Name { get; set; } = string.Empty;
            public int Quantity { get; set; }
            public string? Currency { get; set; }
        }
    }

    private sealed class StubVersionGetter : IMessageVersionGetter<GrpcContext>
    {
        private readonly string? _version;
        public StubVersionGetter(string? version) => _version = version;
        public string? GetVersion(GrpcContext context) => _version;
    }

    private static IRequestMapper<GrpcContext> BuildMapper(string? signalledVersion)
    {
        var services = new ServiceCollection();
        services.UsingBenzene(x => x
            .AddBenzene()
            .AddGrpcMessageHandlers()
            .AddGrpcPayloadVersioning(v => v
                .Topic("order", topic => topic
                    .Version<V1.OrderPayload>("V1")
                    .Version<V2.OrderPayload>("V2")
                    .Upcast<V1.OrderPayload, V2.OrderPayload>(f => f.RegisterInitValue(o => o.Currency, "FROM-UPCAST"))))
            // Signal the incoming version deterministically (the real getter reads a gRPC request header).
            .AddScoped<IMessageVersionGetter<GrpcContext>>(_ => new StubVersionGetter(signalledVersion)));

        var factory = new MicrosoftServiceResolverFactory(services);
        return factory.CreateScope().GetService<IRequestMapper<GrpcContext>>();
    }

    private static GrpcContext OrderContext(string name)
        => new GrpcContext<EchoRequest, EchoReply>("order", TestServerCallContext.Create(), new EchoRequest { Name = name });

    [Fact]
    public void AddGrpcPayloadVersioning_WrapsTheRequestMapperWithTheCastingDecorator()
    {
        var mapper = BuildMapper("V1");

        // Not GrpcRequestMapper and not the framework-default mapper: the gRPC request side is now cast.
        Assert.IsType<CastingRequestMapper<GrpcContext>>(mapper);
    }

    [Fact]
    public void V1GrpcRequest_IsReadThroughTheProtobufBridge_AndUpcastToTheHandlerType()
    {
        var mapper = BuildMapper("V1");

        var v2 = mapper.GetBody<V2.OrderPayload>(OrderContext("acct-1"));

        Assert.NotNull(v2);
        // Name survived the protobuf -> V1 -> V2 path; Currency proves the V1 -> V2 upcast caster ran
        // (it is nowhere in the incoming EchoRequest).
        Assert.Equal("acct-1", v2!.Name);
        Assert.Equal("FROM-UPCAST", v2.Currency);
    }

    [Fact]
    public void RequestWithoutAVersion_BypassesCasting_AndReadsStraightThroughTheGrpcMapper()
    {
        var mapper = BuildMapper(null);

        // No version signalled: the decorator delegates to GrpcRequestMapper, which converts the
        // EchoRequest straight to V2 - so the upcast never runs and Currency stays null.
        var v2 = mapper.GetBody<V2.OrderPayload>(OrderContext("acct-2"));

        Assert.NotNull(v2);
        Assert.Equal("acct-2", v2!.Name);
        Assert.Null(v2.Currency);
    }

    [Fact]
    public void RequestForAnUnversionedTopic_BypassesCasting()
    {
        var mapper = BuildMapper("V1");

        // Version signalled, but this topic has no registered casters, so the decorator passes through.
        var context = new GrpcContext<EchoRequest, EchoReply>("other", TestServerCallContext.Create(), new EchoRequest { Name = "acct-3" });
        var v1 = mapper.GetBody<V1.OrderPayload>(context);

        Assert.NotNull(v1);
        Assert.Equal("acct-3", v1!.Name);
    }
}
