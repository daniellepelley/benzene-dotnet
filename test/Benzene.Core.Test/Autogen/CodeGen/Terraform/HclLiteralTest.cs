using Benzene.CodeGen.Terraform;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Terraform;

public class HclLiteralTest
{
    [Fact]
    public void Format_PlainValue_DoubleQuoted()
    {
        Assert.Equal("\"benzene-orders-func\"", HclLiteral.Format("benzene-orders-func"));
    }

    [Fact]
    public void Format_EmbeddedQuote_Escaped()
    {
        Assert.Equal("\"say \\\"hi\\\"\"", HclLiteral.Format("say \"hi\""));
    }

    [Fact]
    public void Format_EmbeddedBackslash_Escaped()
    {
        Assert.Equal("\"a\\\\b\"", HclLiteral.Format(@"a\b"));
    }

    // #212/#263's sharpest edge: unescaped `${`/`%{` isn't just mangled output, it's *live* HCL
    // template-interpolation/directive syntax that Terraform would actually evaluate.
    [Fact]
    public void Format_DollarBrace_NeutralizedAsLiteral_NotLeftAsInterpolationSyntax()
    {
        var result = HclLiteral.Format("${aws_iam_role.admin.arn}");

        Assert.Equal("\"$${aws_iam_role.admin.arn}\"", result);
        Assert.DoesNotContain("\"${", result);
    }

    [Fact]
    public void Format_PercentBrace_NeutralizedAsLiteral_NotLeftAsDirectiveSyntax()
    {
        var result = HclLiteral.Format("%{if true}danger%{endif}");

        Assert.Equal("\"%%{if true}danger%%{endif}\"", result);
        Assert.DoesNotContain("\"%{", result);
    }

    [Fact]
    public void Format_QuoteBackslashAndInterpolation_AllHandledTogether()
    {
        var result = HclLiteral.Format("topic\"${evil}\\end");

        Assert.Equal("\"topic\\\"$${evil}\\\\end\"", result);
    }
}
