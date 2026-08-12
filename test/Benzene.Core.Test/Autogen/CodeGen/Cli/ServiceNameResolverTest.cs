using Benzene.CodeGen.Cli.Core.Commands.Build;
using Benzene.Schema.OpenApi.EventService;
using Microsoft.OpenApi.Models;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Cli;

public class ServiceNameResolverTest
{
    private static EventServiceDocument EmptyDocument(string title = null) =>
        new(new OpenApiInfo { Title = title }, System.Array.Empty<OpenApiTag>(),
            System.Array.Empty<RequestResponse>(), System.Array.Empty<Event>(), new OpenApiComponents());

    [Fact]
    public void Resolve_ExplicitServiceNameOverride_AlwaysWins()
    {
        var payload = new BuildPayload { ServiceName = "Explicit", Mesh = "https://mesh.example.com/manifest.json", Service = "mesh-name" };

        Assert.Equal("Explicit", ServiceNameResolver.Resolve(payload, EmptyDocument()));
    }

    [Fact]
    public void Resolve_MeshSource_UsesTheMeshServiceName()
    {
        var payload = new BuildPayload { Mesh = "https://mesh.example.com/manifest.json", Service = "orders-api" };

        Assert.Equal("orders-api", ServiceNameResolver.Resolve(payload, EmptyDocument()));
    }

    [Fact]
    public void Resolve_FileSource_UsesTheDocumentTitle_WhenPresent()
    {
        var payload = new BuildPayload { File = "Orders.spec.json" };

        Assert.Equal("Orders Service", ServiceNameResolver.Resolve(payload, EmptyDocument("Orders Service")));
    }

    [Fact]
    public void Resolve_FileSource_FallsBackToTheFileStem_WhenTitleIsAbsent()
    {
        var payload = new BuildPayload { File = "Orders.spec.json" };

        Assert.Equal("Orders", ServiceNameResolver.Resolve(payload, EmptyDocument()));
    }

    [Fact]
    public void Resolve_FileSource_FallsBackToTheFileStem_WhenTitleIsWhitespace()
    {
        var payload = new BuildPayload { File = "/tmp/artifacts/Payments.spec.json" };

        Assert.Equal("Payments", ServiceNameResolver.Resolve(payload, EmptyDocument("   ")));
    }

    [Fact]
    public void Resolve_UrlSource_UsesTheHostsFirstLabel()
    {
        var payload = new BuildPayload { Url = "https://orders.example.com/whatever" };

        Assert.Equal("orders", ServiceNameResolver.Resolve(payload, EmptyDocument()));
    }

    [Fact]
    public void Resolve_LambdaNameSource_ReturnsNull_SoCodeBuilderFactoryKeepsItsOwnDerivation()
    {
        var payload = new BuildPayload { LambdaName = "benzene-orders-func" };

        Assert.Null(ServiceNameResolver.Resolve(payload, EmptyDocument()));
    }
}
