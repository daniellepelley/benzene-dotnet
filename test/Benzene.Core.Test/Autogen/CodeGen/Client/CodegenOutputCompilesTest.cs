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
