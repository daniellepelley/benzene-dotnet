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

    [Fact]
    public void Benz003_HandlerOnAReservedTopicIsACompileError()
    {
        // benzene:healthcheck is always intercepted by dedicated middleware before dispatch - a
        // handler registered on it can never run.
        var diagnostics = Run("""
            [Message("benzene:healthcheck")]
            public class SneakyHealthCheckHandler : IMessageHandler<Request, Response>
            {
                public Task<IBenzeneResult<Response>> HandleAsync(Request message) => null;
            }
            """);

        var reported = Assert.Single(diagnostics.Where(x => x.Id == "BENZ003"));
        Assert.Equal(DiagnosticSeverity.Error, reported.Severity);
        Assert.Contains("SneakyHealthCheckHandler", reported.GetMessage());
        Assert.Contains("benzene:healthcheck", reported.GetMessage());
    }

    [Fact]
    public void Benz003_IsSilentOnAnOrdinaryApplicationTopic()
    {
        var diagnostics = Run("""
            [Message("order:create")]
            public class CreateOrderHandler : IMessageHandler<Request, Response>
            {
                public Task<IBenzeneResult<Response>> HandleAsync(Request message) => null;
            }
            """);

        Assert.Empty(diagnostics.Where(x => x.Id == "BENZ003"));
    }

    [Fact]
    public void Benz003_IsSilentOnAMeshCollectorExtendingTheReservedNamespace()
    {
        // benzene:mesh:aggregate is a real, shipped handler (examples/AwsMesh/Mesh/MeshAggregateHandler.cs):
        // a mesh collector is an ordinary Benzene service serving mesh.md §4's ingest topics as
        // handlers, so the wider benzene:mesh:* namespace is legitimately extended this way. Only
        // Benzene's own specific known ids (BenzeneTopic.All) are always wrong to register on.
        var diagnostics = Run("""
            [Message("benzene:mesh:aggregate")]
            public class MeshAggregateHandler : IMessageHandler<Request, Response>
            {
                public Task<IBenzeneResult<Response>> HandleAsync(Request message) => null;
            }
            """);

        Assert.Empty(diagnostics.Where(x => x.Id == "BENZ003"));
    }

    [Fact]
    public void Benz004_ObjectRequestTypeIsFlaggedAsUnconstrained()
    {
        var diagnostics = Run("""
            [Message("order:create")]
            public class CreateOrderHandler : IMessageHandler<object, Response>
            {
                public Task<IBenzeneResult<Response>> HandleAsync(object message) => null;
            }
            """);

        var reported = Assert.Single(diagnostics.Where(x => x.Id == "BENZ004"));
        Assert.Equal(DiagnosticSeverity.Info, reported.Severity);
        Assert.Contains("request", reported.GetMessage());
        Assert.Contains("CreateOrderHandler", reported.GetMessage());
    }

    [Fact]
    public void Benz004_JsonElementResponseTypeIsFlaggedAsUnconstrained()
    {
        var diagnostics = Run("""
            [Message("order:create")]
            public class CreateOrderHandler : IMessageHandler<Request, System.Text.Json.JsonElement>
            {
                public Task<IBenzeneResult<System.Text.Json.JsonElement>> HandleAsync(Request message) => null;
            }
            """);

        var reported = Assert.Single(diagnostics.Where(x => x.Id == "BENZ004"));
        Assert.Equal(DiagnosticSeverity.Info, reported.Severity);
        Assert.Contains("response", reported.GetMessage());
    }

    [Fact]
    public void Benz004_EnumRequestTypeIsFlaggedAsUnconstrained()
    {
        var diagnostics = Run("""
            public enum OrderKind { Standard, Express }

            [Message("order:create")]
            public class CreateOrderHandler : IMessageHandler<OrderKind, Response>
            {
                public Task<IBenzeneResult<Response>> HandleAsync(OrderKind message) => null;
            }
            """);

        Assert.Single(diagnostics.Where(x => x.Id == "BENZ004"));
    }

    [Fact]
    public void Benz004_IsSilentOnOrdinaryConcreteTypes()
    {
        var diagnostics = Run("""
            [Message("order:create")]
            public class CreateOrderHandler : IMessageHandler<Request, Response>
            {
                public Task<IBenzeneResult<Response>> HandleAsync(Request message) => null;
            }
            """);

        Assert.Empty(diagnostics.Where(x => x.Id == "BENZ004"));
    }

    [Fact]
    public void Benz004_IsSilentOnAResponselessHandlersImpliedVoid()
    {
        // IMessageHandler<TRequest> has no declared response type argument - Void isn't a payload
        // and must not be flagged.
        var diagnostics = Run("""
            [Message("order:archive")]
            public class ArchiveOrderHandler : IMessageHandler<Request>
            {
                public Task<IBenzeneResult<Void>> HandleAsync(Request message) => null;
            }
            """);

        Assert.Empty(diagnostics.Where(x => x.Id == "BENZ004"));
    }
}
