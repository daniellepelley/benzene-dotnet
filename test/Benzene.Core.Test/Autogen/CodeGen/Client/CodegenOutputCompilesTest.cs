using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Benzene.CodeGen.Client;
using Benzene.CodeGen.Core;
using Benzene.Schema.OpenApi.EventService;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.OpenApi.Models;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Client;

/// <summary>
/// #66/#67: a top-level (non-$ref) oneOf request/response schema and a non-identifier schema
/// property name both used to produce uncompilable generated C#
/// (<c>Task&lt;IBenzeneResult&lt;&gt;&gt;</c> with an empty generic argument; <c>public string
/// Order-id { get; set; }</c>). Checking the generator doesn't throw is not enough - both bugs
/// generated cleanly, they just emitted invalid C#. This drives the real
/// MessageClientSdkBuilder/OpenApiSchemaCSharpTypeBuilder pipeline against a hand-built spec
/// carrying both shapes and actually compiles the result with Roslyn.
/// </summary>
public class CodegenOutputCompilesTest
{
    private const string Namespace = "Benzene.Service.Clients.Payments";

    private static OpenApiSchema Ref(string id) => new()
    {
        Reference = new OpenApiReference { Id = id, Type = ReferenceType.Schema }
    };

    private static EventServiceDocument BuildDocument()
    {
        var schemas = new Dictionary<string, OpenApiSchema>
        {
            ["PaymentMethod"] = new()
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema> { ["currency"] = new() { Type = "string" } }
            },
            ["CardPayment"] = new()
            {
                Type = "object",
                AllOf = new List<OpenApiSchema> { Ref("PaymentMethod") },
                Properties = new Dictionary<string, OpenApiSchema> { ["cardNumber"] = new() { Type = "string" } }
            },
            ["BankPayment"] = new()
            {
                Type = "object",
                AllOf = new List<OpenApiSchema> { Ref("PaymentMethod") },
                Properties = new Dictionary<string, OpenApiSchema> { ["iban"] = new() { Type = "string" } }
            },
            // #67: a non-identifier property name ("order-id") on the request DTO.
            ["CreateCheckoutRequest"] = new()
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema> { ["order-id"] = new() { Type = "string" } }
            }
        };

        var requestResponse = new RequestResponse
        {
            Topic = "payment:checkout",
            Request = Ref("CreateCheckoutRequest"),
            // #66: a bare top-level oneOf, not wrapped in a named/$ref schema - exactly the shape
            // CSharpTypeName.GetName had no branch for. Each union member is only a $ref
            // placeholder (no inline body), mirroring how the real reflection-based
            // SchemaBuilder/SchemaGenerator populates a oneOf member site.
            Response = new OpenApiSchema
            {
                OneOf = new List<OpenApiSchema> { Ref("CardPayment"), Ref("BankPayment") }
            }
        };

        return new EventServiceDocument(
            new OpenApiInfo { Title = "Payments", Version = "1.0" },
            Array.Empty<OpenApiTag>(),
            new[] { requestResponse },
            Array.Empty<Event>(),
            new OpenApiComponents { Schemas = schemas });
    }

    private static ICodeFile[] BuildFiles()
    {
        var document = BuildDocument();
        var typeBuilder = new OpenApiSchemaCSharpTypeBuilder(Namespace);
        // The options-based constructor uses Namespace exactly (no magic .{ServiceName} suffix), so
        // the client class and the DTOs built above by typeBuilder land in the SAME namespace -
        // matching how the CLI's own `build` command wires these two together.
        var options = new ClientSdkOptions { ServiceName = "Payments", Namespace = Namespace };
        var builder = new MessageClientSdkBuilder(options, typeBuilder, new CSharpTypeName(),
            new TopicReversedMethodName());

        return builder.BuildCodeFiles(document);
    }

    private static string Text(ICodeFile file) => string.Join(Environment.NewLine, file.Lines);

    [Fact]
    public void ResponseTypeName_IsNotBlank_ForATopLevelOneOfSchema()
    {
        var files = BuildFiles();
        var clientClass = Text(files.Single(x => x.Name == "PaymentsServiceClient.cs"));

        // Before the fix: `Task<IBenzeneResult<>>` - an empty, uncompilable generic argument.
        Assert.DoesNotContain("IBenzeneResult<>", clientClass);
        Assert.DoesNotContain("IBenzeneResult< >", clientClass);
    }

    [Fact]
    public void NonIdentifierPropertyName_IsFormattedToAValidIdentifier()
    {
        var files = BuildFiles();
        var dto = Text(files.Single(x => x.Name == "CreateCheckoutRequest.cs"));

        Assert.DoesNotContain("Order-id", dto);
        Assert.DoesNotContain(" Order-id ", dto);
    }

    [Fact]
    public void GeneratedClient_WithTopLevelOneOfResponseAndNonIdentifierPropertyName_Compiles()
    {
        var files = BuildFiles();

        var trees = files
            .Select(f => CSharpSyntaxTree.ParseText(Text(f), path: f.Name))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "WpH_CodegenCompileCheck_" + Guid.NewGuid().ToString("N"),
            trees,
            ReferenceAssemblies(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();

        Assert.True(errors.Length == 0,
            "Generated code failed to compile:" + Environment.NewLine +
            string.Join(Environment.NewLine, errors.Select(x => x.ToString())) + Environment.NewLine +
            string.Join(Environment.NewLine + "-----" + Environment.NewLine, files.Select(Text)));
    }

    // #240: SuppliedSchemaCatalog schema ids are arbitrary caller strings, not pre-sanitized C#
    // identifiers. "orderItem" (valid but not yet Pascal-cased) and "order-item" (hyphenated, unusable
    // in C# at all) both used to flow into a generated property's *type* unformatted, while the class
    // declaration for that same id was correctly formatted via CSharpNameFormatter - a guaranteed
    // mismatch (CS0246 for "orderItem", a syntax error for "order-item").
    private static EventServiceDocument BuildArbitraryCatalogueIdDocument(string itemSchemaId)
    {
        var schemas = new Dictionary<string, OpenApiSchema>
        {
            [itemSchemaId] = new()
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema> { ["sku"] = new() { Type = "string" } }
            },
            ["Order"] = new()
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema> { ["item"] = Ref(itemSchemaId) }
            }
        };

        var requestResponse = new RequestResponse
        {
            Topic = "order:create",
            Request = Ref("Order"),
            Response = Ref("Order")
        };

        return new EventServiceDocument(
            new OpenApiInfo { Title = "Orders", Version = "1.0" },
            Array.Empty<OpenApiTag>(),
            new[] { requestResponse },
            Array.Empty<Event>(),
            new OpenApiComponents { Schemas = schemas });
    }

    private static ICodeFile[] BuildFilesFor(EventServiceDocument document)
    {
        var typeBuilder = new OpenApiSchemaCSharpTypeBuilder(Namespace);
        var options = new ClientSdkOptions { ServiceName = "Orders", Namespace = Namespace };
        var builder = new MessageClientSdkBuilder(options, typeBuilder, new CSharpTypeName(),
            new TopicReversedMethodName());

        return builder.BuildCodeFiles(document);
    }

    [Theory]
    [InlineData("orderItem")]
    [InlineData("order-item")]
    public void GeneratedClient_WithArbitraryCatalogueSchemaId_Compiles(string itemSchemaId)
    {
        var files = BuildFilesFor(BuildArbitraryCatalogueIdDocument(itemSchemaId));

        var trees = files
            .Select(f => CSharpSyntaxTree.ParseText(Text(f), path: f.Name))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "WpF_CodegenCompileCheck_" + Guid.NewGuid().ToString("N"),
            trees,
            ReferenceAssemblies(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();

        Assert.True(errors.Length == 0,
            "Generated code failed to compile:" + Environment.NewLine +
            string.Join(Environment.NewLine, errors.Select(x => x.ToString())) + Environment.NewLine +
            string.Join(Environment.NewLine + "-----" + Environment.NewLine, files.Select(Text)));
    }

    // #263: Discriminator.PropertyName and every mapping.Key are arbitrary caller-supplied strings
    // (the discriminator *value*, not an identifier) - reachable via SuppliedSchemaCatalog or any
    // hand-built EventServiceDocument, not only reflection-derived schemas - interpolated unescaped
    // into a generated [JsonPolymorphic]/[JsonDerivedType] C# string literal. A value containing a
    // `"` used to produce uncompilable output (7 cascading Roslyn errors from one bad character).
    [Fact]
    public void GeneratedClient_WithAdversarialDiscriminatorMappingKeyContainingAQuote_Compiles()
    {
        var schemas = new Dictionary<string, OpenApiSchema>
        {
            ["PaymentMethod"] = new()
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema> { ["currency"] = new() { Type = "string" } },
                Discriminator = new OpenApiDiscriminator
                {
                    PropertyName = "type",
                    Mapping = new Dictionary<string, string>
                    {
                        ["12\" wheel"] = "#/components/schemas/CardPayment"
                    }
                }
            },
            ["CardPayment"] = new()
            {
                Type = "object",
                AllOf = new List<OpenApiSchema> { Ref("PaymentMethod") },
                Properties = new Dictionary<string, OpenApiSchema> { ["cardNumber"] = new() { Type = "string" } }
            }
        };

        var typeBuilder = new OpenApiSchemaCSharpTypeBuilder(Namespace);
        var files = typeBuilder.BuildCodeFiles(schemas);

        var trees = files
            .Select(f => CSharpSyntaxTree.ParseText(Text(f), path: f.Name))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "WpH_DiscriminatorEscapeCheck_" + Guid.NewGuid().ToString("N"),
            trees,
            ReferenceAssemblies(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();

        Assert.True(errors.Length == 0,
            "Generated code failed to compile:" + Environment.NewLine +
            string.Join(Environment.NewLine, errors.Select(x => x.ToString())) + Environment.NewLine +
            string.Join(Environment.NewLine + "-----" + Environment.NewLine, files.Select(Text)));
    }

    // Same approach as Benzene.Test.Docs.DocSnippetCompiler: TRUSTED_PLATFORM_ASSEMBLIES rather than
    // AppDomain.CurrentDomain.GetAssemblies(), since a project reference the test process hasn't
    // touched yet (e.g. Benzene.Abstractions.Results, Benzene.Clients) may not be loaded.
    private static IReadOnlyList<MetadataReference> ReferenceAssemblies() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Where(File.Exists)
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray();
}
