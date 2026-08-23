using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Benzene.Mesh.Contracts;
using Benzene.Mesh.Discovery.Aws;
using Moq;
using Xunit;

namespace Benzene.Mesh.Test.Discovery;

public class AwsLambdaDiscoveryProviderTest
{
    private static FunctionConfiguration Fn(string name)
        => FnInRegion(name, "eu-west-1");

    private static FunctionConfiguration FnInRegion(string name, string region)
        => new() { FunctionName = name, FunctionArn = $"arn:aws:lambda:{region}:1:function:{name}" };

    private static Mock<IAmazonLambda> LambdaWith(
        IReadOnlyDictionary<string, (FunctionConfiguration Fn, Dictionary<string, string> Tags)> functionsByMarkerPage,
        params (string? Marker, string? NextMarker, string[] Names)[] pages)
    {
        var mock = new Mock<IAmazonLambda>();

        foreach (var page in pages)
        {
            var captured = page;
            mock.Setup(x => x.ListFunctionsAsync(
                    It.Is<ListFunctionsRequest>(r => r.Marker == captured.Marker), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ListFunctionsResponse
                {
                    Functions = captured.Names.Select(n => functionsByMarkerPage[n].Fn).ToList(),
                    NextMarker = captured.NextMarker
                });
        }

        foreach (var (_, value) in functionsByMarkerPage)
        {
            var tags = value.Tags;
            var arn = value.Fn.FunctionArn;
            mock.Setup(x => x.ListTagsAsync(
                    It.Is<ListTagsRequest>(r => r.Resource == arn), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ListTagsResponse { Tags = tags });
        }

        return mock;
    }

    [Fact]
    public async Task Discover_EmitsOnlyTaggedFunctions_AsAwsLambdaInvokeEntries()
    {
        var functions = new Dictionary<string, (FunctionConfiguration, Dictionary<string, string>)>
        {
            ["orders"] = (Fn("orders"), new Dictionary<string, string> { ["benzene"] = "true" }),
            ["unrelated"] = (Fn("unrelated"), new Dictionary<string, string> { ["team"] = "x" }), // no benzene tag
        };
        var mock = LambdaWith(functions, (null, null, new[] { "orders", "unrelated" }));

        var provider = new AwsLambdaDiscoveryProvider(mock.Object);
        var entries = await provider.DiscoverAsync(new MeshDiscoveryFilter());

        var entry = Assert.Single(entries);
        Assert.Equal("orders", entry.Name);
        Assert.Equal(MeshServiceSource.AwsLambdaInvoke, entry.Source);
        Assert.Equal("orders", entry.SourceOptions!["functionName"]);
    }

    [Fact]
    public async Task Discover_FollowsPaginationMarker()
    {
        var functions = new Dictionary<string, (FunctionConfiguration, Dictionary<string, string>)>
        {
            ["a"] = (Fn("a"), new Dictionary<string, string> { ["benzene"] = "1" }),
            ["b"] = (Fn("b"), new Dictionary<string, string> { ["benzene"] = "1" }),
        };
        var mock = LambdaWith(functions,
            (null, "page2", new[] { "a" }),
            ("page2", null, new[] { "b" }));

        var provider = new AwsLambdaDiscoveryProvider(mock.Object);
        var entries = await provider.DiscoverAsync(new MeshDiscoveryFilter());

        Assert.Equal(new[] { "a", "b" }, entries.Select(e => e.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task Discover_CarriesMeshPathHintTag()
    {
        var functions = new Dictionary<string, (FunctionConfiguration, Dictionary<string, string>)>
        {
            ["orders"] = (Fn("orders"), new Dictionary<string, string>
            {
                ["benzene"] = "true",
                ["benzene:mesh-path"] = "/custom/mesh"
            }),
        };
        var mock = LambdaWith(functions, (null, null, new[] { "orders" }));

        var provider = new AwsLambdaDiscoveryProvider(mock.Object);
        var entry = Assert.Single(await provider.DiscoverAsync(new MeshDiscoveryFilter()));

        Assert.Equal("/custom/mesh", entry.SourceOptions!["meshPath"]);
    }

    [Fact]
    public async Task Discover_ValuedTagFilter_ExcludesNonMatching()
    {
        var functions = new Dictionary<string, (FunctionConfiguration, Dictionary<string, string>)>
        {
            ["prod-svc"] = (Fn("prod-svc"), new Dictionary<string, string> { ["benzene"] = "prod" }),
            ["dev-svc"] = (Fn("dev-svc"), new Dictionary<string, string> { ["benzene"] = "dev" }),
        };
        var mock = LambdaWith(functions, (null, null, new[] { "prod-svc", "dev-svc" }));

        var provider = new AwsLambdaDiscoveryProvider(mock.Object);
        var entries = await provider.DiscoverAsync(
            new MeshDiscoveryFilter(new Dictionary<string, string?> { ["benzene"] = "prod" }));

        var entry = Assert.Single(entries);
        Assert.Equal("prod-svc", entry.Name);
    }

    // Regression: MeshDiscoveryFilter.Regions is documented "AWS/Azure" region scoping and
    // MeshDiscoveryRunner passes one filter instance to every registered provider, but this provider
    // used to ignore filter.Regions entirely - a function in a region the operator explicitly excluded
    // was still discovered and registered. Fixed 2026-08-23 by reading the region out of the
    // function's ARN, matching AzureAppServiceDiscoveryProviderTest's own
    // Discover_RegionFilter_ExcludesOtherRegions coverage for the Azure provider.
    [Fact]
    public async Task Discover_RegionFilter_ExcludesOtherRegions()
    {
        var functions = new Dictionary<string, (FunctionConfiguration, Dictionary<string, string>)>
        {
            ["in-region"] = (FnInRegion("in-region", "eu-west-1"), new Dictionary<string, string> { ["benzene"] = "true" }),
            ["other-region"] = (FnInRegion("other-region", "us-east-1"), new Dictionary<string, string> { ["benzene"] = "true" }),
        };
        var mock = LambdaWith(functions, (null, null, new[] { "in-region", "other-region" }));

        var provider = new AwsLambdaDiscoveryProvider(mock.Object);
        var entries = await provider.DiscoverAsync(new MeshDiscoveryFilter(regions: new[] { "eu-west-1" }));

        var entry = Assert.Single(entries);
        Assert.Equal("in-region", entry.Name);
    }

    [Fact]
    public async Task Discover_RegionFilter_UnreadableArnIsNeverExcluded()
    {
        var functions = new Dictionary<string, (FunctionConfiguration, Dictionary<string, string>)>
        {
            ["no-arn"] = (new FunctionConfiguration { FunctionName = "no-arn", FunctionArn = null },
                new Dictionary<string, string> { ["benzene"] = "true" }),
        };
        var mock = LambdaWith(functions, (null, null, new[] { "no-arn" }));

        var provider = new AwsLambdaDiscoveryProvider(mock.Object);
        var entries = await provider.DiscoverAsync(new MeshDiscoveryFilter(regions: new[] { "eu-west-1" }));

        Assert.Single(entries);
    }
}
