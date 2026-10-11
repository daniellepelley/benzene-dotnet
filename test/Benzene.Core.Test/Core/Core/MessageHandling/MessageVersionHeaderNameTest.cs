using System.Collections.Generic;
using Benzene.Abstractions;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Core.Core.MessageHandling;

/// <summary>
/// One version header, configured in one place (wire-contracts.md §2, versioning.md §2.1): the default is
/// <c>benzene-version</c>, a renamed header replaces it rather than joining it, and nothing else is read.
/// </summary>
public class MessageVersionHeaderNameTest
{
    private class TestContext
    {
    }

    private class FixedHeadersGetter : IMessageHeadersGetter<TestContext>
    {
        private readonly IDictionary<string, string> _headers;
        public FixedHeadersGetter(IDictionary<string, string> headers) => _headers = headers;
        public IDictionary<string, string> GetHeaders(TestContext context) => _headers;
    }

    private static IMessageVersionGetter<TestContext> BuildVersionGetter(
        IDictionary<string, string> headers, string? headerName)
    {
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddBenzene();
        container.AddScoped<IMessageHeadersGetter<TestContext>>(_ => new FixedHeadersGetter(headers));
        container.AddHeaderMessageVersionGetter<TestContext>();
        if (headerName != null)
        {
            container.AddMessageVersionHeaderName(headerName);
        }

        var resolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
        return resolver.GetService<IMessageVersionGetter<TestContext>>();
    }

    [Fact]
    public void NoRename_ReadsBenzeneVersion()
    {
        var getter = BuildVersionGetter(
            new Dictionary<string, string> { ["benzene-version"] = "V2", ["schema-version"] = "V9" },
            headerName: null);

        Assert.Equal("V2", getter.GetVersion(new TestContext()));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("x-version")]
    public void ABareVersionHeader_IsAnOrdinaryHeader_NotAVersion(string header)
    {
        // The fallback list is gone: a producer's own version header selects nothing.
        var getter = BuildVersionGetter(new Dictionary<string, string> { [header] = "V2" }, headerName: null);

        Assert.Null(getter.GetVersion(new TestContext()));
    }

    [Fact]
    public void ARenamedHeader_ReplacesTheDefault_RatherThanJoiningIt()
    {
        var getter = BuildVersionGetter(
            new Dictionary<string, string> { ["benzene-version"] = "V2", ["schema-version"] = "V9" },
            headerName: "schema-version");

        Assert.Equal("V9", getter.GetVersion(new TestContext()));
        Assert.Null(BuildVersionGetter(new Dictionary<string, string> { ["benzene-version"] = "V2" }, "schema-version")
            .GetVersion(new TestContext()));
    }

    [Fact]
    public void TheNameIsTheWireNamesVersion_TheOnePlaceEveryReservedNameLives()
    {
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddBenzene();
        container.AddSingleton<IBenzeneWireNames>(_ => new BenzeneWireNames(version: "schema-version"));
        container.AddScoped<IMessageHeadersGetter<TestContext>>(_ =>
            new FixedHeadersGetter(new Dictionary<string, string> { ["schema-version"] = "V3" }));
        container.AddHeaderMessageVersionGetter<TestContext>();

        var resolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());

        Assert.Equal("V3", resolver.GetService<IMessageVersionGetter<TestContext>>().GetVersion(new TestContext()));
    }
}
