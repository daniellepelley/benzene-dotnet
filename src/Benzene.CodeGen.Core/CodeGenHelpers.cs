using System.Security.Cryptography;
using System.Text;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Schema.OpenApi;
using Benzene.Schema.OpenApi.EventService;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;

namespace Benzene.CodeGen.Core
{
    public static class CodeGenHelpers
    {
        public static FormatString Camelcase(this string source)
        {
            return new FormatString(source).Camelcase();
        }

        public static FormatString Camelcase(this FormatString source)
        {
            if (string.IsNullOrEmpty(source.Value))
                return source;

            // Match System.Text.Json's JsonNamingPolicy.CamelCase (the wire policy the runtime
            // serializer uses), so property keys shown in generated markdown docs are the exact
            // wire shape: a capital that precedes a lowercase letter is kept ("IPAddress" ->
            // "ipAddress", not "ipaddress"). See Benzene.Schema.OpenApi ExamplePayloadBuilder.
            return new FormatString(System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(source.Value));
        }

        public static FormatString Pascalcase(this FormatString source)
        {
            if (string.IsNullOrEmpty(source.Value))
                return source;
            return new FormatString(char.ToUpperInvariant(source.Value[0]) + source.Value.Substring(1));
        }

        public static FormatString EnsureStartsWithLetterOrUnderScore(this FormatString source)
        {
            if (string.IsNullOrEmpty(source.Value))
                return source;

            if (!char.IsLetter(source.Value[0]) && source.Value[0] != '_')
            {
                return new FormatString("_" + source);
            }

            return source;
        }

        public static FormatString RemoveSpaces(this FormatString source)
        {
            if (string.IsNullOrEmpty(source.Value))
                return source;

            return new FormatString(source.Value.Replace(" ", ""));
        }

        public static FormatString RemoveNonIdentifierCharacters(this FormatString source)
        {
            if (string.IsNullOrEmpty(source.Value))
                return source;

            return new FormatString(string.Concat(source.Value.Where(ch => char.IsLetterOrDigit(ch) || ch == '_')));
        }

        public static string TitleCase(string value)
        {
            return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value);
        }

        private static string GeneratorBase64(string json)
        {
            var plainTextBytes = Encoding.UTF8.GetBytes(json);
            return System.Convert.ToBase64String(plainTextBytes);
        }

        /// <summary>
        /// HMAC-SHA256 (empty key) of <paramref name="json"/> as lowercase hex - the mesh descriptor's
        /// <c>descriptorHash</c> primitive (<c>Benzene.Mesh.Contracts.MeshHashing</c> mirrors it). Not a
        /// contract hash: the contract document's two hashes are <see cref="ContractHash"/>'s.
        /// </summary>
        public static string GenerateHash(string json)
        {
            var hash = new HMACSHA256(Array.Empty<byte>()).ComputeHash(Encoding.UTF8.GetBytes(json));
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }

        /// <summary>
        /// Produces a correctly-escaped, double-quoted C# string literal for a user-authored value
        /// (a message topic, a discriminator property name, a discriminator mapping key, ...) that is
        /// about to be embedded directly into generated C# source. The topic/version case of this
        /// exact hazard is already fixed, in the one codegen path that runs inside the compiler, via
        /// <c>Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true)</c> - see
        /// <c>Benzene.CodeGen.SourceGenerators/MessageHandlerSourceGenerator.cs</c>. Every other
        /// generator that interpolates a user-authored string into emitted C# (this package's own
        /// <c>MessageClientSdkBuilder</c>/<c>OpenApiSchemaCSharpTypeBuilder</c>) needs the identical
        /// fix - an unescaped <c>"</c> breaks the generated string literal, and an unescaped <c>\</c>
        /// or a crafted <c>", ...</c> sequence can inject arbitrary tokens into the generated source -
        /// but those packages are ordinary shipped NuGet libraries (unlike the analyzer project, which
        /// pulls in Microsoft.CodeAnalysis.CSharp only as a build-time, non-shipping analyzer
        /// dependency), so taking a real runtime dependency on the Roslyn compiler there would be a
        /// disproportionate addition. This reimplements the same escaping semantics (backslash, quote,
        /// and control characters via <c>\uXXXX</c>) without that dependency.
        /// </summary>
        public static string ToCSharpStringLiteral(string value)
        {
            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');

            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\':
                        builder.Append(@"\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\0':
                        builder.Append(@"\0");
                        break;
                    case '\a':
                        builder.Append(@"\a");
                        break;
                    case '\b':
                        builder.Append(@"\b");
                        break;
                    case '\f':
                        builder.Append(@"\f");
                        break;
                    case '\n':
                        builder.Append(@"\n");
                        break;
                    case '\r':
                        builder.Append(@"\r");
                        break;
                    case '\t':
                        builder.Append(@"\t");
                        break;
                    case '\v':
                        builder.Append(@"\v");
                        break;
                    default:
                        if (char.IsControl(c))
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(c);
                        }
                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }
    }
}
