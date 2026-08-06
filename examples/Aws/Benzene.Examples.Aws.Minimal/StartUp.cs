using Benzene.Abstractions.DI;
using Benzene.Abstractions.Hosting;
using Benzene.Aws.Lambda.ApiGateway;
using Benzene.Aws.Lambda.Core;
using Benzene.Aws.Lambda.Core.BenzeneMessage;
using Benzene.Aws.Lambda.EventBridge;
using Benzene.Aws.Lambda.Sns;
using Benzene.Aws.Lambda.Sqs;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Http;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Examples.Aws.Minimal;

/// <summary>
/// The smallest AWS Lambda that shows Benzene's central promise: one <see cref="PlaceOrderMessageHandler"/>,
/// reached over FOUR event sources from a single function - API Gateway (HTTP request/response), SNS, SQS
/// and EventBridge (fire-and-forget events). The handler is transport-agnostic; everything AWS-specific
/// lives here, in <see cref="Configure"/>, inside <c>app.UseAwsLambda(...)</c>.
///
/// This is the platform-neutral <see cref="BenzeneStartUp"/> - the exact same shape the ASP.NET and Kafka
/// examples use; only the transports wired below differ. The runnable companion to
/// <c>docs/getting-started-aws.md</c>.
/// </summary>
public class StartUp : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // The stand-in "data store" the handler records to, so a fire-and-forget SNS/SQS/EventBridge
        // test can still observe that the handler ran.
        services.AddSingleton<IProcessedLog, InMemoryProcessedLog>();

        // The only Benzene registration this host needs up front: the BenzeneMessage envelope's mappers.
        // Everything else is registered implicitly in Configure - UseMessageHandlers() discovers the
        // handlers and pulls in the core services (AddBenzene) + context mappers, and UseApiGateway wires
        // HTTP routing. (UsingBenzene also registers logging.)
        services.UsingBenzene(x => x.AddBenzeneMessage());
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
        app.UseAwsLambda(aws =>
        {
            // The transport-neutral BenzeneMessage envelope pipeline, reused directly and mounted on
            // API Gateway as the /benzene-message endpoint.
            var pipeline = aws.Create<BenzeneMessageContext>()
                .UseMessageHandlers(_ => { });

            aws.UseBenzeneMessage(pipeline);

            // API Gateway: routes the [HttpEndpoint("POST","/orders")] handler - request/response.
            aws.UseApiGateway(api => api.UseMessageHandlers(_ => { }));

            // SNS / SQS: the topic rides in the "topic" message attribute; fire-and-forget.
            aws.UseSns(sns => sns.UseMessageHandlers(_ => { }));
            aws.UseSqs(sqs => sqs.UseMessageHandlers(_ => { }));

            // EventBridge: the topic is the event's detail-type; fire-and-forget.
            aws.UseEventBridge(eventBridge => eventBridge.UseMessageHandlers(_ => { }));
        });
    }
}

/// <summary>
/// AWS Lambda entry point hosting <see cref="StartUp"/>. Point the function-handler setting at
/// <c>Benzene.Examples.Aws.Minimal::Benzene.Examples.Aws.Minimal.Function::FunctionHandlerAsync</c>.
/// </summary>
public class Function : AwsLambdaHost<StartUp>;
