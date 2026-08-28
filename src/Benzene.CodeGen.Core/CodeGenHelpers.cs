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

        public static string GenerateHash(IMessageHandlerDefinition[] handlers)
        {
            var document = new EventServiceDocumentBuilder(new SchemaBuilder())
                .AddMessageHandlerDefinitions(handlers)
                .Build();

            return GenerateHash(document);
        }

        /// <summary>
        /// Computes the contract hash of a spec document: the hash of its serialized form with the
        /// non-contract decoration stripped — generated <c>example</c> payloads and the
        /// <c>messageEndpoint</c> advertisement. Examples are derived from the schemas and the
        /// endpoint is transport plumbing, so neither changes what the service's message contract
        /// *is* — and excluding them keeps this hash identical to the hashes baked into client SDKs
        /// generated before examples existed, so upgrading a service doesn't trip the
        /// client-vs-service contract-drift check (<c>Benzene.Clients.HealthChecks</c>) falsely.
        /// </summary>
        public static string GenerateHash(EventServiceDocument document)
        {
            var normalized = new EventServiceDocument(
                document.Info,
                document.Tags,
                document.Requests.Select(x => new RequestResponse
                {
                    Topic = x.Topic,
                    Version = x.Version,
                    HttpMappings = x.HttpMappings,
                    Request = x.Request,
                    Response = x.Response
                }).ToArray(),
                document.Events.Select(x => new Event(x.Topic, x.Message)).ToArray(),
                document.Components);

            return GenerateHash(normalized.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0));
        }

        private static string GeneratorBase64(string json)
        {
            var plainTextBytes = Encoding.UTF8.GetBytes(json);
            return System.Convert.ToBase64String(plainTextBytes);
        }

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
