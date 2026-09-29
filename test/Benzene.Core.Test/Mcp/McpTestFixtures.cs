using System;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Benzene.Test.Mcp;

public class EchoRequest
{
    public string Name { get; set; } = string.Empty;
}

public class EchoResponse
{
    public string Greeting { get; set; } = string.Empty;
}

/// <summary>
/// Answers the greeting, or refuses a name it does not like - so a failure result has a body to map.
/// Registered explicitly by each test (no [Message] attribute) so the assembly scan other tests rely
/// on sees exactly the handlers it always has.
/// </summary>
public class EchoHandler : IMessageHandler<EchoRequest, EchoResponse>
{
    public Task<IBenzeneResult<EchoResponse>> HandleAsync(EchoRequest request)
    {
        if (request.Name == "nobody")
        {
            return Task.FromResult(BenzeneResult.NotFound<EchoResponse>("There is nobody by that name."));
        }

        return Task.FromResult(BenzeneResult.Ok(new EchoResponse { Greeting = $"hello {request.Name}" }));
    }
}

public static class McpTestFixtures
{
    /// <summary>A container, resolver and built BenzeneMessage pipeline with <see cref="EchoHandler"/> registered on <c>mcp:echo</c>.</summary>
    public static (IServiceResolver Resolver, IMiddlewarePipeline<BenzeneMessageContext> Pipeline) EchoPipeline(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        configure?.Invoke(services);
        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddBenzeneMessage();

        var pipeline = container.CreateMiddlewarePipeline<BenzeneMessageContext>(p =>
            p.UseMessageHandlers(Array.Empty<Type>(),
                router => router.AddMessageHandler<EchoHandler, EchoRequest, EchoResponse>("mcp:echo")));

        var resolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
        return (resolver, pipeline);
    }
}
