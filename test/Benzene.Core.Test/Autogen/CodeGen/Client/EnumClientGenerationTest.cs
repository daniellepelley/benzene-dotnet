using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Benzene.CodeGen.Client;
using Benzene.CodeGen.Core;
using Benzene.Schema.OpenApi;
using Benzene.Test.Autogen.CodeGen.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Client;

/// <summary>
/// #166: a schema with <c>enum</c> entries (Swashbuckle's shape for a real C# enum property) used to
/// fall through <see cref="OpenApiSchemaCSharpTypeBuilder"/>'s class-emission path and come out as an
/// empty C# class with no members - which then serializes as <c>"status":{}</c> on the wire, which a
/// real server rejects with HTTP 400. This drives the real
/// SchemaBuilder -&gt; OpenApiSchemaCSharpTypeBuilder pipeline against a request DTO with both a
/// string enum (<c>[JsonConverter(typeof(JsonStringEnumConverter))]</c>) and a plain int enum,
/// compiles the generated code with Roslyn (same approach as
/// <see cref="CodegenOutputCompilesTest"/>), and - since compiling alone would not have caught an
/// empty-but-compilable class - loads the compiled assembly and actually serializes an instance to
/// confirm the wire shape is a real value, not <c>{}</c>.
/// </summary>
public class EnumClientGenerationTest
{
    private const string Namespace = "Benzene.Service.Clients.Orders";

    private static ICodeFile[] BuildFiles()
    {
        var schemaBuilder = new SchemaBuilder();
        schemaBuilder.AddSchema(typeof(EnumRequest));

        var typeBuilder = new OpenApiSchemaCSharpTypeBuilder(Namespace);
        return typeBuilder.BuildCodeFiles(schemaBuilder.Build());
    }

    private static string Text(ICodeFile file) => string.Join(Environment.NewLine, file.Lines);

    [Fact]
    public void StringEnum_EmitsARealEnum_WithJsonStringEnumConverter_NotAnEmptyClass()
    {
        var files = BuildFiles();
        var generated = Text(files.Single(x => x.Name == "OrderStatus.cs"));

        Assert.Contains("public enum OrderStatus", generated);
        Assert.DoesNotContain("public class OrderStatus", generated);
        Assert.Contains("[JsonConverter(typeof(JsonStringEnumConverter))]", generated);
        Assert.Contains("Pending", generated);
        Assert.Contains("Shipped", generated);
        Assert.Contains("Delivered", generated);
    }

    [Fact]
    public void IntEnum_EmitsARealEnum_WithTheSchemasNumericValues_NotAnEmptyClass()
    {
        var files = BuildFiles();
        var generated = Text(files.Single(x => x.Name == "Priority.cs"));

        Assert.Contains("public enum Priority", generated);
        Assert.DoesNotContain("public class Priority", generated);
        // No JsonStringEnumConverter for an int enum - System.Text.Json already serializes it as its
        // numeric value by default, which is exactly what the schema's `enum` values are (0, 1, 5).
        Assert.DoesNotContain("JsonStringEnumConverter", generated);
        Assert.Contains("= 0", generated);
        Assert.Contains("= 1", generated);
        Assert.Contains("= 5", generated);
    }

    [Fact]
    public void RequestDto_PropertiesAreTypedAsTheRealEnums_NotAsTheEmptyGeneratedClass()
    {
        var files = BuildFiles();
        var dto = Text(files.Single(x => x.Name == "EnumRequest.cs"));

        Assert.Contains("public OrderStatus Status { get; set; }", dto);
        Assert.Contains("public Priority Level { get; set; }", dto);
    }

    [Fact]
    public void GeneratedEnumTypes_Compile_AndSerializeToRealWireValues_NotEmptyObjects()
    {
        var files = BuildFiles();

        var trees = files
            .Select(f => CSharpSyntaxTree.ParseText(Text(f), path: f.Name))
            .ToArray();

        var assemblyName = "WpH_EnumCodegenCompileCheck_" + Guid.NewGuid().ToString("N");
        var compilation = CSharpCompilation.Create(
            assemblyName,
            trees,
            ReferenceAssemblies(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var emitResult = compilation.Emit(stream);

        var errors = emitResult.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(emitResult.Success,
            "Generated code failed to compile:" + Environment.NewLine +
            string.Join(Environment.NewLine, errors.Select(x => x.ToString())) + Environment.NewLine +
            string.Join(Environment.NewLine + "-----" + Environment.NewLine, files.Select(Text)));

        stream.Seek(0, SeekOrigin.Begin);
        var assembly = Assembly.Load(stream.ToArray());

        var requestType = assembly.GetType($"{Namespace}.EnumRequest")!;
        var statusType = assembly.GetType($"{Namespace}.OrderStatus")!;
        var priorityType = assembly.GetType($"{Namespace}.Priority")!;

        Assert.NotNull(requestType);
        Assert.True(statusType.IsEnum, "OrderStatus must be a real C# enum, not a class.");
        Assert.True(priorityType.IsEnum, "Priority must be a real C# enum, not a class.");

        var request = Activator.CreateInstance(requestType)!;
        requestType.GetProperty("Status")!.SetValue(request, Enum.Parse(statusType, "Shipped"));
        requestType.GetProperty("Level")!.SetValue(request, Enum.ToObject(priorityType, 5));

        var json = JsonSerializer.Serialize(request, requestType);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // Before the fix: both properties serialized as "{}" (an empty object) rather than the
        // enum's actual value - exactly the shape a real server's model binder rejects with HTTP 400.
        Assert.NotEqual(JsonValueKind.Object, root.GetProperty("Status").ValueKind);
        Assert.Equal("Shipped", root.GetProperty("Status").GetString());
        Assert.NotEqual(JsonValueKind.Object, root.GetProperty("Level").ValueKind);
        Assert.Equal(5, root.GetProperty("Level").GetInt32());
    }

    // Same approach as CodegenOutputCompilesTest/Benzene.Test.Docs.DocSnippetCompiler:
    // TRUSTED_PLATFORM_ASSEMBLIES rather than AppDomain.CurrentDomain.GetAssemblies(), since a project
    // reference the test process hasn't touched yet may not be loaded.
    private static IReadOnlyList<MetadataReference> ReferenceAssemblies() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Where(File.Exists)
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray();
}
