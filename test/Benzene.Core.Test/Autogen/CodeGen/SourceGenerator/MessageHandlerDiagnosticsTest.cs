using System;
using System.IO;
using System.Linq;
using Benzene.CodeGen.SourceGenerators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.SourceGenerator;

/// <summary>
/// Drives the generator over real source and asserts what the compiler would tell a developer.
/// </summary>
/// <remarks>
/// Written against <see cref="CSharpGeneratorDriver"/> rather than the source-generator testing
/// framework, whose verifier does not work in this environment (see the skipped test in
/// <see cref="MessageHandlerSourceGeneratorTest"/>). These diagnostics are the whole reason the
/// analyzer is now referenced by Benzene.Core.MessageHandlers, so they need a check that runs.
/// </remarks>
public class MessageHandlerDiagnosticsTest
{
    private const string Preamble = """
        using System.Threading.Tasks;
        using Benzene.Abstractions.MessageHandlers;
        using Benzene.Abstractions.Results;
        using Benzene.Core.MessageHandlers;
        using Benzene.Http;

        public class Request { }
        public class Response { }
        """;

    private static Diagnostic[] Run(string source) => Generate(source).diagnostics;

    private static (Diagnostic[] diagnostics, Compilation output) Generate(string source)
    {
        var compilation = CSharpCompilation.Create(
            "BenzeneAnalyzerTest",
            new[] { CSharpSyntaxTree.ParseText(Preamble + Environment.NewLine + source) },
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(File.Exists)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        CSharpGeneratorDriver
            .Create(new MessageHandlerSourceGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        return (diagnostics.ToArray(), output);
    }

    [Fact]
    public void TheGeneratedRegistrationsCompile()
    {
        // They did not. AddGeneratedMessageHandlers opened with services.GetService<MessageHandlersList>(),
        // which IBenzeneServiceContainer has never had — it registers services, it does not resolve
        // them. Nothing caught it because nothing referenced the analyzer, and the moment
        // Benzene.Core.MessageHandlers did, every example failed to build.
        var (_, output) = Generate("""
            [Message("order:create")]
            public class CreateOrderHandler : IMessageHandler<Request, Response>
            {
                public Task<IBenzeneResult<Response>> HandleAsync(Request message) => null;
            }
            """);

        var errors = output.GetDiagnostics()
            .Where(x => x.Severity == DiagnosticSeverity.Error)
            .Select(x => $"{x.Location.GetLineSpan()}: {x.Id} {x.GetMessage()}")
            .ToArray();

        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void Benz002_AnHttpEndpointWithNoTopicIsACompileError()
    {
        // Handler discovery keys on [Message]. Without it the handler is skipped entirely and the
        // route never exists — a 404 with nothing in the logs. UnroutedHttpEndpointCheck says the same
        // thing, but not until the first request builds the route table.
        var diagnostics = Run("""
            [HttpEndpoint("GET", "/orders/{id}")]
            public class GetOrderHandler : IMessageHandler<Request, Response>
            {
                public Task<IBenzeneResult<Response>> HandleAsync(Request message) => null;
            }
            """);

        var reported = Assert.Single(diagnostics.Where(x => x.Id == "BENZ002"));
        Assert.Equal(DiagnosticSeverity.Error, reported.Severity);
        Assert.Contains("GetOrderHandler", reported.GetMessage());
        Assert.Contains("[Message(\"topic\")]", reported.GetMessage());
    }

    [Fact]
    public void Benz002_IsSilentOnceTheHandlerHasATopic()
    {
        var diagnostics = Run("""
            [Message("order:get")]
            [HttpEndpoint("GET", "/orders/{id}")]
            public class GetOrderHandler : IMessageHandler<Request, Response>
            {
                public Task<IBenzeneResult<Response>> HandleAsync(Request message) => null;
            }
            """);

        Assert.Empty(diagnostics.Where(x => x.Id == "BENZ002"));
    }

    [Fact]
    public void Benz002_IgnoresAnHttpEndpointOnSomethingThatIsNotAHandler()
    {
        // Benzene's discovery was never going to route this, so complaining about it would be a false
        // positive — and a hint stated with unearned confidence costs more than no hint.
        var diagnostics = Run("""
            [HttpEndpoint("GET", "/orders/{id}")]
            public class NotAHandler
            {
            }
            """);

        Assert.Empty(diagnostics.Where(x => x.Id == "BENZ002"));
    }

    [Fact]
    public void Benz001_TwoHandlersOnOneTopicIsACompileError()
    {
        // The runtime is inconsistent about this: ReflectionMessageHandlersFinder throws, but
        // MessageHandlerDefinitionIndex silently keeps the first and drops the rest. The compiler can
        // settle it before either gets a chance.
        var diagnostics = Run("""
            [Message("order:create")]
            public class CreateOrderHandler : IMessageHandler<Request, Response>
            {
                public Task<IBenzeneResult<Response>> HandleAsync(Request message) => null;
            }

            [Message("order:create")]
            public class ShadowCreateOrderHandler : IMessageHandler<Request, Response>
            {
                public Task<IBenzeneResult<Response>> HandleAsync(Request message) => null;
            }
            """);

        var reported = diagnostics.Where(x => x.Id == "BENZ001").ToArray();

        Assert.Equal(2, reported.Length);
        Assert.All(reported, x => Assert.Equal(DiagnosticSeverity.Error, x.Severity));
        Assert.Contains("order:create", reported[0].GetMessage());
    }

    [Fact]
    public void Benz001_LetsTheSameTopicThroughAtDifferentVersions()
    {
        var diagnostics = Run("""
            [Message("order:create", "1")]
            public class CreateOrderHandlerV1 : IMessageHandler<Request, Response>
            {
                public Task<IBenzeneResult<Response>> HandleAsync(Request message) => null;
            }

            [Message("order:create", "2")]
            public class CreateOrderHandlerV2 : IMessageHandler<Request, Response>
            {
                public Task<IBenzeneResult<Response>> HandleAsync(Request message) => null;
            }
            """);

        Assert.Empty(diagnostics.Where(x => x.Id == "BENZ001"));
    }
}
