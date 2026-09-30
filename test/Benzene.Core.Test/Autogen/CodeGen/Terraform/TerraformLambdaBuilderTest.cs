using System.Text.RegularExpressions;
using System.IO;
using Benzene.CodeGen.Terraform;
using Benzene.Test.Autogen.CodeGen.Helpers;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Terraform;

public class TerraformLambdaBuilderTest
{
    private string LoadExpected(string fileName) => File.ReadAllText($"{Directory.GetCurrentDirectory()}/Autogen/CodeGen/Terraform/Examples/{fileName}");

    [Fact]
    public void MainCoreService_Test()
    {
        var expectedLambda = LoadExpected("MainCore/lambda.txt");
        var expectedRole = LoadExpected("MainCore/iam_roles.txt");

        var terraformBuilder = new TerraformLambdaBuilder();

        var result = terraformBuilder.Build(new TerraformLambdaSettings
        {
            Name = "benzene_main_core_func",
            EntryPoint = "Benzene.Main.Core.Func::benzene.main.Core.LambdaEntryPoint::FunctionHandlerAsync",
            Timeout = 30,
            MemorySize = 2048,
            Domain = "benzene",
            SubDomain = "main"
        });

        Assert.Equal(expectedLambda, result["lambda.tf"], ignoreLineEndingDifferences: true);
        Assert.Equal(expectedRole, result["iam_roles.tf"], ignoreLineEndingDifferences: true);
    }

    // #212/#263: Name/Domain/SubDomain are caller-supplied and interpolated as HCL string literals
    // (function_name, tags, the role's own name/tags) - a `"`/`\` used to break the literal outright,
    // and a `${` was live Terraform template-interpolation syntax, not just mangled text.
    [Fact]
    public void BuildLambda_AdversarialName_QuoteBackslashAndInterpolation_EscapedNotEvaluated()
    {
        const string adversarialName = "svc\"; \\${aws_iam_role.admin.arn}";

        var lines = new TerraformLambdaBuilder().BuildLambda(new TerraformLambdaSettings
        {
            Name = adversarialName,
            EntryPoint = "Handler::Handle",
            Domain = "benzene",
            SubDomain = "main"
        });

        Assert.Contains($"  function_name = {HclLiteral.Format(adversarialName)}", lines);
        Assert.Contains($"    name = {HclLiteral.Format(adversarialName)}", lines);
        // The sharpest edge of the finding: the name's "${" must never survive live into the generated
        // .tf - it would be evaluated by Terraform as an expression, not read back as this literal text.
        // (The generator's own "${path.module}/file.zip" is a deliberate expression, so the check is for
        // the payload's, unescaped - HclLiteral writes it as "$${".)
        Assert.DoesNotContain(lines, line => Regex.IsMatch(line, @"(?<!\$)\$\{aws_iam_role\.admin"));
        // And the name can't break out through the resource label or the reference built from it: both
        // have to be Terraform identifiers, which a quote cannot close.
        var label = Regex.Match(lines[0], "^resource \"aws_lambda_function\" \"([^\"]*)\" \\{$");
        Assert.True(label.Success, lines[0]);
        Assert.Matches("^[A-Za-z_][A-Za-z0-9_]*$", label.Groups[1].Value);
        Assert.Contains($"  role = aws_iam_role.{label.Groups[1].Value}_role.arn", lines);
    }

    [Fact]
    public void BuildRole_AdversarialName_Interpolation_NeutralizedInNameAndTags()
    {
        const string adversarialName = "svc${data.aws_caller_identity.current.account_id}";

        var lines = new TerraformLambdaBuilder().BuildRole(new TerraformLambdaSettings
        {
            Name = adversarialName,
            Domain = "benzene",
            SubDomain = "main"
        });

        Assert.Contains($"  name = {HclLiteral.Format(adversarialName + "-role")}", lines);
        Assert.DoesNotContain(lines, line => line.Contains("\"${") || line.Contains("-role${"));
    }
}
