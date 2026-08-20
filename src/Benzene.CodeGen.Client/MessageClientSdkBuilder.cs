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
        output.Add(new CodeFile($"{_serviceName}ServiceClientRegistration.cs", BuildRegistration()));

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

        lineWriter.WriteLine("}", 1);

        lineWriter.WriteLines(AddRoutingClass(eventServiceDocument));

        lineWriter.WriteLine("}");

        return lineWriter.GetLines();
    }

    private string[] AddRoutingClass(EventServiceDocument eventServiceDocument)
    {
        // Reflected over by Benzene.Clients' ValidateOutboundRouting() at startup - see
        // work/archive/benzene-clients-redesign-plan-2026-07.md §2.5. Exactly the topics this client has methods for:
        // Benzene's own reserved endpoints (benzene:*) are framework plumbing, not a client's domain
        // surface, and are excluded by TopicScope like any other reserved topic - naming one here
        // would demand an outbound route the consumer never asked for and fail its start-up checks.
        var requiredTopics = string.Join(", ", eventServiceDocument.Requests.Select(x => $@"""{x.Topic}"""));

        var lineWriter = new LineWriter();
        lineWriter.WriteLine();
        lineWriter.WriteLine("[OutboundRoutingContract]", 1);
        lineWriter.WriteLine($"public static class {_serviceName}ServiceClientRouting", 1);
        lineWriter.WriteLine("{", 1);
        lineWriter.WriteLine($"public static readonly string[] RequiredTopics = {{ {requiredTopics} }};", 2);
        lineWriter.WriteLine("}", 1);
        return lineWriter.GetLines();
    }

    /// <summary>
    /// Builds the client's DI registration extension - one <c>Add{Service}ServiceClient()</c> over
    /// <see cref="Benzene.Abstractions.DI.IBenzeneServiceContainer"/>, so a consumer stops hand-writing
    /// the registration (and stops having to know the right lifetime).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why <c>IBenzeneServiceContainer</c> and not <c>IServiceCollection</c>:</b> Benzene's own
    /// container abstraction is the one thing every host has, whatever container is underneath -
    /// Autofac, Microsoft.Extensions.DependencyInjection, or anything else. Generating against
    /// Microsoft's <c>IServiceCollection</c> would be useless to a consumer on Autofac while Benzene is
    /// the thing doing the DI. <c>Benzene.Abstractions</c> is already referenced by the generated
    /// client (<c>IBenzeneResult</c>), so this adds no new dependency to the generated output.
    /// </para>
    /// <para>
    /// It goes in its own file rather than beside the client class so the generated client stays free
    /// of DI usings, exactly as the routing contract stays free of them.
    /// </para>
    /// </remarks>
    public string[] BuildRegistration()
    {
        var lineWriter = new LineWriter();

        lineWriter.WriteLine("using System.Diagnostics.CodeAnalysis;");
        lineWriter.WriteLine("using Benzene.Abstractions.DI;");
        lineWriter.WriteLine("");

        lineWriter.WriteLine($"namespace {_options.Namespace}");
        lineWriter.WriteLine("{");
        lineWriter.WriteLine("[ExcludeFromCodeCoverage]", 1);
        lineWriter.WriteLine($"public static class {_serviceName}ServiceClientRegistration", 1);
        lineWriter.WriteLine("{", 1);
        lineWriter.WriteLine($"/// <summary>Registers {_serviceName}ServiceClient as I{_serviceName}ServiceClient.</summary>", 2);
        lineWriter.WriteLine($"public static IBenzeneServiceContainer Add{_serviceName}ServiceClient(this IBenzeneServiceContainer container)", 2);
        lineWriter.WriteLine("{", 2);
        // The lifetime is the whole reason this is generated rather than left to the consumer:
        // AddOutboundRouting registers IBenzeneMessageSender SCOPED, so a singleton client would
        // capture a scoped dependency. The comment ships in the generated file so the reasoning is
        // visible where the registration is read.
        lineWriter.WriteLine("// Scoped, not singleton: AddOutboundRouting registers IBenzeneMessageSender", 3);
        lineWriter.WriteLine("// scoped, so a singleton client would be a captive dependency.", 3);
        lineWriter.WriteLine($"return container.AddScoped<I{_serviceName}ServiceClient, {_serviceName}ServiceClient>();", 3);
        lineWriter.WriteLine("}", 2);
        lineWriter.WriteLine("}", 1);
        lineWriter.WriteLine("}");

        return lineWriter.GetLines();
    }

    private void AddHashCode(EventServiceDocument eventServiceDocument, LineWriter lineWriter)
    {
        // The spec-pinned contractHash (contract-document.md §6) - see ContractHash for why this is
        // a distinct algorithm from CodeGenHelpers.GenerateHash. _options.IsTopicScopedForHash is set
        // only by AtomicClientSdkBuilder's inner options, for its single-topic (§5.3) document.
        var hashCode = ContractHash.Compute(eventServiceDocument, _options.IsTopicScopedForHash);
        lineWriter.WriteLine($@"public string HashCode => ""{hashCode}"";", 2);
        lineWriter.WriteLine();
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
        lineWriter.WriteLine("using Benzene.Results;");

        lineWriter.WriteLine("");

        lineWriter.WriteLine($"namespace {_options.Namespace}");
        lineWriter.WriteLine("{");
        lineWriter.WriteLine($"public interface I{_serviceName}ServiceClient", 1);
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
