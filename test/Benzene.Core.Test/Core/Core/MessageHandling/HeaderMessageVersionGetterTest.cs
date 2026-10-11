using System.Collections.Generic;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Core.MessageHandlers;
using Xunit;

namespace Benzene.Test.Core.Core.MessageHandling;

public class HeaderMessageVersionGetterTest
{
    private class TestContext
    {
    }

    private class FixedHeadersGetter : IMessageHeadersGetter<TestContext>
    {
        private readonly IDictionary<string, string>? _headers;

        public FixedHeadersGetter(IDictionary<string, string>? headers)
        {
            _headers = headers;
        }

        public IDictionary<string, string> GetHeaders(TestContext context) => _headers;
    }

    [Fact]
    public void GetVersion_ReadsBenzeneVersion_IgnoringEveryOtherVersionShapedHeader()
    {
        var headers = new Dictionary<string, string>
        {
            ["benzene-version"] = "V2",
            ["version"] = "V1",
            ["x-version"] = "V3"
        };
        var getter = new HeaderMessageVersionGetter<TestContext>(new FixedHeadersGetter(headers));

        Assert.Equal("V2", getter.GetVersion(new TestContext()));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("x-version")]
    public void GetVersion_OnlyAProducersOwnVersionHeader_IsNoVersion(string header)
    {
        // One header, no fallback list (wire-contracts.md §2): a plain version/x-version header is an
        // ordinary header - it used to be read as the payload version and could misroute a message.
        var headers = new Dictionary<string, string> { [header] = "V1" };
        var getter = new HeaderMessageVersionGetter<TestContext>(new FixedHeadersGetter(headers));

        Assert.Null(getter.GetVersion(new TestContext()));
    }

    [Fact]
    public void GetVersion_NoVersionHeaderPresent_ReturnsNull()
    {
        var headers = new Dictionary<string, string> { ["content-type"] = "application/json" };
        var getter = new HeaderMessageVersionGetter<TestContext>(new FixedHeadersGetter(headers));

        Assert.Null(getter.GetVersion(new TestContext()));
    }

    [Fact]
    public void GetVersion_HeaderNameCasingDiffers_StillMatchesCaseInsensitively()
    {
        var headers = new Dictionary<string, string> { ["Benzene-Version"] = "V2" };
        var getter = new HeaderMessageVersionGetter<TestContext>(new FixedHeadersGetter(headers));

        Assert.Equal("V2", getter.GetVersion(new TestContext()));
    }

    [Fact]
    public void GetVersion_HeadersDictionaryIsNull_ReturnsNull()
    {
        var getter = new HeaderMessageVersionGetter<TestContext>(new FixedHeadersGetter(null));

        Assert.Null(getter.GetVersion(new TestContext()));
    }

    [Fact]
    public void GetVersion_RenamedHeader_ReplacesBenzeneVersion()
    {
        var headers = new Dictionary<string, string> { ["benzene-version"] = "V2", ["schema-version"] = "V9" };
        var getter = new HeaderMessageVersionGetter<TestContext>(new FixedHeadersGetter(headers), headerName: "schema-version");

        // A configured name replaces the default; it does not join a list (versioning.md §2.1).
        Assert.Equal("V9", getter.GetVersion(new TestContext()));
        Assert.Equal("schema-version", getter.HeaderName);
    }
}
