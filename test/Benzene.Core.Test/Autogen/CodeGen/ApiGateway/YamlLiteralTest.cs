using Benzene.CodeGen.ApiGateway;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.ApiGateway;

public class YamlLiteralTest
{
    [Fact]
    public void Format_PlainValue_SingleQuoted()
    {
        Assert.Equal("'user:get'", YamlLiteral.Format("user:get"));
    }

    [Fact]
    public void Format_EmbeddedSingleQuote_Doubled()
    {
        Assert.Equal("'it''s here'", YamlLiteral.Format("it's here"));
    }

    [Fact]
    public void Format_EmbeddedDoubleQuote_NoBackslashEscapingNeeded()
    {
        // Single-quoted YAML scalars have no backslash escapes at all - a `"` needs no special
        // handling, unlike inside a double-quoted scalar.
        Assert.Equal("'say \"hi\"'", YamlLiteral.Format("say \"hi\""));
    }

    [Fact]
    public void Format_ColonAndFlowIndicators_SafeInsideQuotes()
    {
        // The #212 reproduction: a `:` (and flow indicators like `{`/`}`/`,`) would otherwise be
        // significant unquoted YAML - single-quoting neutralizes all of it uniformly.
        Assert.Equal("'weird: {value, here}'", YamlLiteral.Format("weird: {value, here}"));
    }
}
