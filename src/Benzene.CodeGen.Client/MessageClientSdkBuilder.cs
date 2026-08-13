using Benzene.CodeGen.Core;
using Benzene.CodeGen.Core.Writers;
using Benzene.Schema.OpenApi.EventService;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;

namespace Benzene.CodeGen.Client;

public class MessageClientSdkBuilder : ICodeBuilder<EventServiceDocument>
{
    private readonly string _serviceName;
    private readonly ClientSdkOptions _options;
    private readonly ICodeBuilder<IDictionary<string, OpenApiSchema>> _typeBuilder;
    private readonly IMethodName _methodName;
    private readonly ITypeName _typeName;

    public MessageClientSdkBuilder(string serviceName, string baseNamespace)
        : this(LegacyOptions(serviceName, baseNamespace))
    { }

    public MessageClientSdkBuilder(string serviceName, string baseNamespace, ICodeBuilder<IDictionary<string, OpenApiSchema>> typeBuilder, ITypeName typeName, IMethodName methodName)
        : this(LegacyOptions(serviceName, baseNamespace), typeBuilder, typeName, methodName)
    { }

    /// <summary>
    /// Initializes a client builder from <paramref name="options"/>: <see cref="ClientSdkOptions.Namespace"/>
    /// is used exactly - no magic <c>.{ServiceName}</c> suffix - as the namespace for the client
    /// class, its interface and its DTOs alike.
    /// </summary>
    public MessageClientSdkBuilder(ClientSdkOptions options)
        : this(options, new OpenApiSchemaCSharpTypeBuilder(options.Namespace), new CSharpTypeName(), new TopicReversedMethodName())
    { }

    public MessageClientSdkBuilder(ClientSdkOptions options, ICodeBuilder<IDictionary<string, OpenApiSchema>> typeBuilder, ITypeName typeName, IMethodName methodName)
    {
        if (string.IsNullOrWhiteSpace(options.ServiceName))
        {
            throw new ArgumentException("ClientSdkOptions.ServiceName is required for MessageClientSdkBuilder", nameof(options));
        }

        _options = options;
        _serviceName = options.ServiceName;
        _typeBuilder = typeBuilder;
        _typeName = typeName;
        _methodName = methodName;
    }

    // The legacy (serviceName, baseNamespace) constructors' derived namespace was always exactly
    // "{baseNamespace}.{serviceName}" - resolving that here means ClientSdkOptions.Namespace can be
    // used exactly everywhere else, with no separate "is a suffix needed?" branch downstream.
    private static ClientSdkOptions LegacyOptions(string serviceName, string baseNamespace) => new()
    {
        ServiceName = serviceName,
        Namespace = $"{baseNamespace}.{serviceName}",
    };

    public ICodeFile[] BuildCodeFiles(EventServiceDocument eventServiceDocument)
    {
        var scopedDocument = TopicScope.Apply(eventServiceDocument, _options);

        var output = new List<ICodeFile>();

        var classString = BuildClass(scopedDocument);
        var interfaceString = BuildInterface(scopedDocument);

        output.Add(new CodeFile($"{_serviceName}ServiceClient.cs", classString));
        output.Add(new CodeFile($"I{_serviceName}ServiceClient.cs", interfaceString));

        foreach (var codeFile in _typeBuilder.BuildCodeFiles(scopedDocument.Components.Schemas))
        {
            output.Add(codeFile);
        }

        return output.ToArray();
    }

    public string[] BuildClass(EventServiceDocument eventServiceDocument)
    {
        var lineWriter = new LineWriter();

        lineWriter.WriteLine("using System;");
        lineWriter.WriteLine("using System.Collections.Generic;");
        lineWriter.WriteLine("using System.Threading.Tasks;");
        lineWriter.WriteLine("using Benzene.Abstractions.Results;");
        lineWriter.WriteLine("using Benzene.Clients;");
        lineWriter.WriteLine("using Benzene.Clients.HealthChecks;");
        lineWriter.WriteLine("using Benzene.HealthChecks.Core;");
        lineWriter.WriteLine("using Benzene.Results;");
        lineWriter.WriteLine("using System.Diagnostics.CodeAnalysis;");
        lineWriter.WriteLine("");

        lineWriter.WriteLine($"namespace {_options.Namespace}");
        lineWriter.WriteLine("{");
        lineWriter.WriteLine("[ExcludeFromCodeCoverage]", 1);
        lineWriter.WriteLine($"public class {_serviceName}ServiceClient : I{_serviceName}ServiceClient", 1);
        lineWriter.WriteLine("{", 1);

        lineWriter.WriteLine("private readonly IBenzeneMessageSender _sender;", 2);
        lineWriter.WriteLine();
        lineWriter.WriteLine($"public {_serviceName}ServiceClient(IBenzeneMessageSender sender)", 2);
        lineWriter.WriteLine("{", 2);
        lineWriter.WriteLine("_sender = sender;", 3);
        lineWriter.WriteLine("}", 2);
        lineWriter.WriteLine();

        AddHashCode(eventServiceDocument, lineWriter);

        foreach (var definition in eventServiceDocument.Requests)
        {
            lineWriter.WriteLines(AddMethod(definition.Topic, definition.Request, definition.Response));
        }

        lineWriter.WriteLines(AddHealthCheckMethod());

        lineWriter.WriteLine("}", 1);

        lineWriter.WriteLines(AddRoutingClass(eventServiceDocument));

        lineWriter.WriteLine("}");

        return lineWriter.GetLines();
    }

    private string[] AddRoutingClass(EventServiceDocument eventServiceDocument)
    {
        // Reflected over by Benzene.Clients' ValidateOutboundRouting() at startup - see
        // work/benzene-clients-redesign-plan.md §2.5.
        var topics = eventServiceDocument.Requests.Select(x => x.Topic).Append(Benzene.Abstractions.BenzeneTopic.HealthCheck);
        var requiredTopics = string.Join(", ", topics.Select(topic => $@"""{topic}"""));

        var lineWriter = new LineWriter();
        lineWriter.WriteLine();
        lineWriter.WriteLine("[OutboundRoutingContract]", 1);
        lineWriter.WriteLine($"public static class {_serviceName}ServiceClientRouting", 1);
        lineWriter.WriteLine("{", 1);
        lineWriter.WriteLine($"public static readonly string[] RequiredTopics = {{ {requiredTopics} }};", 2);
        lineWriter.WriteLine("}", 1);
        return lineWriter.GetLines();
    }

    private void AddHashCode(EventServiceDocument eventServiceDocument, LineWriter lineWriter)
    {
        var hashCode = CodeGenHelpers.GenerateHash(eventServiceDocument);
        lineWriter.WriteLine($@"public string HashCode => ""{hashCode}"";", 2);
        lineWriter.WriteLine();
    }

    private string[] AddHealthCheckMethod()
    {
        var lineWriter = new LineWriter();
        lineWriter.WriteLine(
            $"public async Task<IBenzeneResult<HealthCheckResponse>> HealthCheckAsync()", 2);
        lineWriter.WriteLine("{", 2);
        // Benzene.Abstractions.Results.Void, not a nonexistent "NullPayload" - the request payload for
        // the no-body benzene:healthcheck topic. Every server-side handler for it (Benzene.HealthChecks'
        // middleware) reads the topic, not the body, so any serializable empty type would work on the
        // wire; Void is the one this codebase already established for exactly this "no meaningful
        // request" case (see e.g. AtomicClientSdkBuilder's own generated Void.cs DTO for a Void-request
        // topic). Fully qualified rather than relying on the "using Benzene.Abstractions.Results;" above:
        // a topic-scoped client (AtomicClientSdkBuilder) whose own request/response schema happens to
        // include a component literally named "Void" emits its OWN same-namespace Void DTO (see
        // OpenApiSchemaCSharpTypeBuilder), and a bare "Void" here would then bind to THAT type instead -
        // fine when the shapes happen to match, but the two are unrelated types; when no such local type
        // exists, a bare "Void" is instead ambiguous with System.Void (CS0104, displayed as "void") once
        // this using and any other Void-bearing using are both in scope. Full qualification is correct in
        // every case: same-namespace, ambiguous, and unambiguous alike.
        lineWriter.WriteLine(
            $@"var benzeneResult = await _sender.SendAsync<Benzene.Abstractions.Results.Void, HealthCheckResponse>(""benzene:healthcheck"", new Benzene.Abstractions.Results.Void());",
            3);
        lineWriter.WriteLine("if (benzeneResult.Payload == null)", 3);
        lineWriter.WriteLine("{", 3);
        lineWriter.WriteLine("return benzeneResult;", 4);
        lineWriter.WriteLine("}", 3);
        lineWriter.WriteLine(
            "var annotated = ClientHealthCheckProcessor.Process(benzeneResult.Payload, HashCode) as HealthCheckResponse;",
            3);
        lineWriter.WriteLine("return BenzeneResult.Set(benzeneResult.Status, annotated, benzeneResult.IsSuccessful);", 3);
        lineWriter.WriteLine("}", 2);
        return lineWriter.GetLines();
    }

    private string[] AddMethod(string topic, OpenApiSchema requestType, OpenApiSchema responseType)
    {
        var requestTypeName = _typeName.GetName(requestType);
        var responseTypeName = _typeName.GetName(responseType);
        var methodName = _methodName.Create(topic, requestType);

        var lineWriter = new LineWriter();
        lineWriter.WriteLine(
            $"public Task<IBenzeneResult<{responseTypeName}>> {methodName}Async({requestTypeName} message)", 2);
        lineWriter.WriteLine("{", 2);
        lineWriter.WriteLine($@"return {methodName}Async(message, null);", 3);
        lineWriter.WriteLine("}", 2);
        lineWriter.WriteLine();

        lineWriter.WriteLine(
            $"public Task<IBenzeneResult<{responseTypeName}>> {methodName}Async({requestTypeName} message, IDictionary<string, string> headers)", 2);
        lineWriter.WriteLine("{", 2);
        lineWriter.WriteLine(
            $@"return _sender.SendAsync<{requestTypeName}, {responseTypeName}>(""{topic}"", message, headers);",
            3);
        lineWriter.WriteLine("}", 2);
        lineWriter.WriteLine();
        return lineWriter.GetLines();
    }

    public string[] BuildInterface(EventServiceDocument eventServiceDocument)
    {
        var lineWriter = new LineWriter();

        lineWriter.WriteLine("using System;");
        lineWriter.WriteLine("using System.Collections.Generic;");
        lineWriter.WriteLine("using System.Threading.Tasks;");
        lineWriter.WriteLine("using Benzene.Abstractions.Results;");
        lineWriter.WriteLine("using Benzene.Clients;");
        lineWriter.WriteLine("using Benzene.Clients.HealthChecks;");
        lineWriter.WriteLine("using Benzene.Results;");

        lineWriter.WriteLine("");

        lineWriter.WriteLine($"namespace {_options.Namespace}");
        lineWriter.WriteLine("{");
        lineWriter.WriteLine($"public interface I{_serviceName}ServiceClient : IHasHealthCheck", 1);
        lineWriter.WriteLine("{", 1);

        foreach (var definition in eventServiceDocument.Requests)
        {
            var topicFunction = GetTopicFunction(definition.Topic);
            var requestTypeName = _typeName.GetName(definition.Request);
            var responseTypeName = _typeName.GetName(definition.Response);
            var methodName = _methodName.Create(definition.Topic, definition.Request);
    
            lineWriter.WriteLine(
                $"Task<IBenzeneResult<{responseTypeName}>> {methodName}Async({requestTypeName} message);", 2);
            lineWriter.WriteLine(
                $"Task<IBenzeneResult<{responseTypeName}>> {methodName}Async({requestTypeName} message, IDictionary<string, string> headers);", 2);
        }

        lineWriter.WriteLine("}", 1);
        lineWriter.WriteLine("}");

        return lineWriter.GetLines();

    }


    private static string GetTopicFunction(string topic)
    {
        return topic.Split(':').LastOrDefault();
    }
}
