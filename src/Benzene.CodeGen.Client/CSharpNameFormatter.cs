using Benzene.CodeGen.Core;

namespace Benzene.CodeGen.Client;

public class CSharpNameFormatter : INameFormatter
{
    public string Format(string name)
    {
        // A schema property name that isn't already a valid identifier fragment (e.g. "order-id")
        // used to fall straight through to Pascalcase, which only uppercases the first character -
        // producing uncompilable members like "Order-id". RemoveNonIdentifierCharacters (already
        // used by TopicMethodName/TopicReversedMethodName for topic->method-name formatting) strips
        // everything but letters, digits and underscore before casing is applied.
        return new FormatString(name)
            .EnsureStartsWithLetterOrUnderScore()
            .RemoveSpaces()
            .RemoveNonIdentifierCharacters()
            .Pascalcase()
            .ToString();
    }
}
