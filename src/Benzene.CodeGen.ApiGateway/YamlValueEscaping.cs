namespace Benzene.CodeGen.ApiGateway
{
    /// <summary>
    /// Escapes free-form, user-controlled strings (topic names, path-derived tags, configured
    /// header values) for safe embedding in the YAML <see cref="ApiGatewayBuilderV1"/> emits.
    /// </summary>
    /// <remarks>
    /// #212: raw string interpolation into the generated document let a <c>"</c> in a topic name
    /// break a double-quoted <c>summary:</c> scalar, and a <c>": "</c> in a path segment survive
    /// title-casing (<see cref="ApiGatewayBuilderV1"/>'s <c>CreateTag</c>) into an unquoted
    /// sequence item under <c>tags:</c> that no longer parses as a single scalar. Every
    /// interpolation site now goes through one of the two helpers below instead of interpolating
    /// the raw value directly.
    /// </remarks>
    internal static class YamlValueEscaping
    {
        /// <summary>
        /// Renders <paramref name="value"/> as a single-quoted YAML scalar - always quoted, so call
        /// sites never have to reason about which characters are "safe" to leave bare. A
        /// single-quoted scalar has exactly one escape rule (a literal <c>'</c> is written as
        /// <c>''</c>) and no other special characters, so doubling internal single quotes is
        /// sufficient for arbitrary content.
        /// </summary>
        public static string QuoteSingle(string? value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
        }

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
