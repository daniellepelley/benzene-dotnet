using Benzene.CodeGen.ApiGateway;
using Benzene.CodeGen.Client;
using Benzene.CodeGen.Core;
using Benzene.CodeGen.Markdown;
using Benzene.Schema.OpenApi.EventService;

namespace Benzene.CodeGen.Cli.Core.Commands.Build;

public class CodeBuilderFactory
{
    /// <summary>
    /// The documented, supported <c>--output</c> values. Deliberately excludes <c>api-gateway</c>:
    /// its switch case still works (the 2026-08-12 plan amendment froze deprecation/removal for a
    /// later decision - see work/spec-mesh-tooling-implementation-plan.md Amendment B), but it stays
    /// out of this advertised list and out of the unknown-value error below.
    /// </summary>
    private static readonly string[] ValidOutputs = { "client", "topic-client", "message-handlers", "readme" };

    public ICodeBuilder<EventServiceDocument> Create(ICommandPayload codePayload)
    {
        // A resolved ServiceName (explicit --service-name, or a --file/--url/--mesh source's
        // default - see ServiceNameResolver) takes over naming entirely; a bare --lambda-name
        // source leaves it null/empty and keeps the original LambdaNameParser derivation unchanged,
        // so every existing --lambda-name call site and its tests keep behaving identically.
        var hasResolvedServiceName = !string.IsNullOrWhiteSpace(codePayload.ServiceName);
        var serviceName = hasResolvedServiceName
            ? ServiceNameFormatter.ToPascalCase(codePayload.ServiceName)
            : LambdaNameParser.GetServiceName(codePayload.LambdaName);

        string DerivedNamespace(string suffix) => hasResolvedServiceName
            ? $"{serviceName}.{suffix}"
            : LambdaNameParser.GetNamespace(codePayload.LambdaName, suffix);

        // An explicit --namespace always wins and is used exactly (no magic suffix); absent, every
        // mode's namespace derivation is exactly what it was before this flag existed.
        var explicitNamespace = string.IsNullOrWhiteSpace(codePayload.Namespace) ? null : codePayload.Namespace;
        var topics = ParseTopics(codePayload.Topics);

        switch (codePayload.Output)
        {
            case "readme":
                return new LambdaServiceMarkdownBuilder(codePayload.LambdaName, serviceName, "");
            case "api-gateway":
                return new ApiGatewayBuilderV1(codePayload.LambdaName);
            case "message-handlers":
                return new MessageHandlerBuilder(explicitNamespace ?? DerivedNamespace("Service"));
            case "topic-client":
                return new AtomicClientSdkBuilder(new ClientSdkOptions
                {
                    Namespace = explicitNamespace ?? DerivedNamespace("Client"),
                    Topics = topics,
                });
            case "client":
                return new MessageClientSdkBuilder(new ClientSdkOptions
                {
                    ServiceName = serviceName,
                    // Unchanged derivation when no --namespace: the legacy (serviceName, baseNamespace)
                    // constructor always resolved to exactly "{baseNamespace}.{serviceName}".
                    Namespace = explicitNamespace ?? $"{DerivedNamespace("Client")}.{serviceName}",
                    Topics = topics,
                });
            default:
                throw new ArgumentException(
                    $"Unknown --output '{codePayload.Output}'. Valid values: {string.Join(", ", ValidOutputs)}.");
        }
    }

    private static IReadOnlyCollection<string>? ParseTopics(string topics) =>
        string.IsNullOrWhiteSpace(topics)
            ? null
            : topics.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
