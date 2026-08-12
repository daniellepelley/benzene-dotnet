using Benzene.CodeGen.Core;

namespace Benzene.CodeGen.Cli.Core.Commands.Build;

/// <summary>
/// Pascal-cases a resolved service name (an explicit <c>--service-name</c>, a mesh service name, a
/// spec document title, a file stem, or a URL host segment) into a valid C# identifier segment for
/// use as a generated namespace/type name root - the same treatment <see cref="LambdaNameParser"/>
/// gives a <c>--lambda-name</c>, generalized to names that were never Lambda function names.
/// </summary>
public static class ServiceNameFormatter
{
    private static readonly char[] WordSeparators = { '-', '_', ' ', '.' };

    public static string ToPascalCase(string name)
    {
        return string.Join("", name
            .Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => new FormatString(x).Pascalcase()));
    }
}
