using Benzene.CodeGen.Client;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Client;

// CSharpNameFormatter.Format never called the existing CodeGenHelpers.RemoveNonIdentifierCharacters
// helper (already used by TopicMethodName/TopicReversedMethodName for topic->method-name
// formatting), so a schema property name containing characters that aren't valid inside a C#
// identifier (e.g. "order-id") fell straight through to Pascalcase - which only uppercases the
// first character - producing uncompilable members like `public string Order-id { get; set; }`.
public class CSharpNameFormatterTest
{
    private readonly CSharpNameFormatter _formatter = new();

    [Fact]
    public void Format_HyphenatedName_ProducesAValidIdentifier()
    {
        var formatted = _formatter.Format("order-id");

        Assert.DoesNotContain("-", formatted);
        Assert.True(SyntaxFacts.IsValidIdentifier(formatted), $"'{formatted}' is not a valid identifier");
    }

    [Fact]
    public void Format_NameWithMultipleNonIdentifierCharacters_ProducesAValidIdentifier()
    {
        var formatted = _formatter.Format("customer.email/address");

        Assert.DoesNotContain(".", formatted);
        Assert.DoesNotContain("/", formatted);
        Assert.True(SyntaxFacts.IsValidIdentifier(formatted), $"'{formatted}' is not a valid identifier");
    }

    [Fact]
    public void Format_OrdinaryName_IsUnaffected()
    {
        Assert.Equal("OrderId", _formatter.Format("OrderId"));
        Assert.Equal("Id", _formatter.Format("id"));
    }
}
