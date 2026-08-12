using Benzene.CodeGen.ApiGateway;
using Benzene.CodeGen.Client;
using Benzene.CodeGen.Core;
using Benzene.CodeGen.Markdown;
using Benzene.Schema.OpenApi.EventService;

namespace Benzene.CodeGen.Cli.Core.Commands.Build;

public class CodeBuilderFactory
{
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

        string Namespace(string suffix) => hasResolvedServiceName
            ? $"{serviceName}.{suffix}"
            : LambdaNameParser.GetNamespace(codePayload.LambdaName, suffix);

        switch (codePayload.Output)
        {
            case "readme":
                return new LambdaServiceMarkdownBuilder(codePayload.LambdaName, serviceName, "");
            case "api-gateway":
                return new ApiGatewayBuilderV1(codePayload.LambdaName);
            case "message-handlers":
                return new MessageHandlerBuilder(Namespace("Service"));
            case "topic-client":
                return new AtomicClientSdkBuilder(Namespace("Client"));
            default:
                return new MessageClientSdkBuilder(serviceName, Namespace("Client"));
        }
    }
}
