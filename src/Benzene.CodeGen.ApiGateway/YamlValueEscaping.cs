namespace Benzene.CodeGen.ApiGateway
{
    /// <summary>
    /// Escapes free-form, user-controlled strings for safe embedding in the YAML
    /// <see cref="ApiGatewayBuilderV1"/> emits, for the one call shape <see cref="YamlLiteral"/>
    /// doesn't cover - a value embedded inside a YAML double-quoted scalar the call site already
    /// wraps in literal <c>"..."</c> (an AWS-required <c>"'value'"</c> shape), rather than a plain
    /// standalone scalar <see cref="YamlLiteral.Format"/> renders on its own.
    /// </summary>
    internal static class YamlValueEscaping
    {
        /// <summary>
        /// Escapes <paramref name="value"/> for embedding inside a YAML double-quoted scalar that
        /// the call site already wraps in literal <c>"..."</c> (preserving that surrounding
        /// convention, e.g. an AWS-required <c>"'value'"</c> shape) - escapes the two characters a
        /// double-quoted scalar treats specially, backslash and double-quote.
        /// </summary>
        public static string EscapeForDoubleQuoted(string? value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
