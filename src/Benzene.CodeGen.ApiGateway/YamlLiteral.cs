namespace Benzene.CodeGen.ApiGateway
{
    /// <summary>
    /// Escapes a user-authored value (a message topic, or a tag derived from an HTTP-mapped path)
    /// for safe embedding as a YAML scalar in the generated <c>openApi.yaml</c>. Mirrors the fix
    /// pattern <c>Benzene.CodeGen.SourceGenerators/MessageHandlerSourceGenerator.cs</c> already
    /// applies to the same class of hazard for generated C# (<c>SymbolDisplay.FormatLiteral</c>):
    /// an unescaped <c>"</c> in a topic used to break the double-quoted <c>summary:</c> scalar, and a
    /// <c>:</c> in a path segment survived title-casing into an invalid unquoted sequence item under
    /// <c>tags:</c> (#212/#263).
    /// </summary>
    public static class YamlLiteral
    {
        /// <summary>
        /// Wraps <paramref name="value"/> in a single-quoted YAML scalar, doubling any embedded single
        /// quote - the standard YAML single-quoted-scalar escaping rule. Unlike a double-quoted YAML
        /// scalar, a single-quoted one has no backslash-escape sequences to worry about, so this is the
        /// only rule needed to make the value safe regardless of what it contains (quotes, colons,
        /// leading/trailing whitespace, flow indicators, ...).
        /// </summary>
        public static string Format(string value)
        {
            return "'" + value.Replace("'", "''") + "'";
        }
    }
}
