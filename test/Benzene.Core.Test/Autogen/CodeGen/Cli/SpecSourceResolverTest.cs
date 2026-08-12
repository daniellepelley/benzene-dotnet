using System;
using Benzene.CodeGen.Cli.Core.Commands.Spec;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Cli;

// SpecSourceResolver: exactly one of --file/--url/--mesh/--lambda-name must be given, shared by
// both `build` and `spec`.
public class SpecSourceResolverTest
{
    [Fact]
    public void Resolve_NoSourceGiven_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => SpecSourceResolver.Resolve(null, null, null, null, null, null));
        Assert.Contains("No spec source given", exception.Message);
    }

    [Theory]
    [InlineData("Orders.spec.json", "https://orders.example.com", null, null)]
    [InlineData("Orders.spec.json", null, "https://mesh.example.com/manifest.json", "orders")]
    [InlineData(null, "https://orders.example.com", "https://mesh.example.com/manifest.json", "orders")]
    public void Resolve_MultipleSourcesGiven_Throws(string file, string url, string mesh, string service)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => SpecSourceResolver.Resolve(file, url, mesh, service, null, null));
        Assert.Contains("Multiple spec sources", exception.Message);
    }

    [Fact]
    public void Resolve_FileAndLambdaNameBothGiven_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => SpecSourceResolver.Resolve("Orders.spec.json", null, null, null, "orders-func", null));
    }

    [Fact]
    public void Resolve_File_ReturnsFileSpecSource()
    {
        var source = SpecSourceResolver.Resolve("Orders.spec.json", null, null, null, null, null);
        Assert.IsType<FileSpecSource>(source);
    }

    [Fact]
    public void Resolve_Url_ReturnsHttpSpecSource()
    {
        var source = SpecSourceResolver.Resolve(null, "https://orders.example.com", null, null, null, null);
        Assert.IsType<HttpSpecSource>(source);
    }

    [Fact]
    public void Resolve_Mesh_ReturnsMeshSpecSource()
    {
        var source = SpecSourceResolver.Resolve(null, null, "https://mesh.example.com/manifest.json", "orders", null, null);
        Assert.IsType<MeshSpecSource>(source);
    }

    [Fact]
    public void Resolve_MeshWithoutService_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => SpecSourceResolver.Resolve(null, null, "https://mesh.example.com/manifest.json", null, null, null));
        Assert.Contains("--service", exception.Message);
    }

    [Fact]
    public void Resolve_LambdaName_ReturnsAwsLambdaSpecSource()
    {
        var source = SpecSourceResolver.Resolve(null, null, null, null, "orders-func", "my-profile");
        Assert.IsType<AwsLambdaSpecSource>(source);
    }
}
