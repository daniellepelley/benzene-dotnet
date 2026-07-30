using System;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.ApplicationLoadBalancerEvents;
using Benzene.Abstractions.Middleware;
using Benzene.Aws.Lambda.Core;
using Benzene.Aws.Lambda.Core.AwsEventStream;
using Benzene.Aws.Lambda.HttpBridge;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Benzene.Aws.Lambda.AspNet;

/// <summary>
/// Wires a Benzene AWS event pipeline into an ASP.NET Core application so one Lambda function serves
/// HTTP through ASP.NET and consumes SQS, SNS, EventBridge and the rest through Benzene — off one
/// container, driven by <c>app.Run()</c>.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Adds Benzene AWS Lambda hosting to an ASP.NET Core application: the built-in HTTP bridge, the
    /// Benzene-driven <c>IServer</c>, and the event pipeline you configure.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="configureEvents">
    /// Configures the AWS event-stream pipeline — typically <c>UseHttpBridgeV2()</c> for the HTTP front
    /// door plus <c>UseSqs</c>/<c>UseSns</c>/… for the message transports.
    /// </param>
    /// <returns>The service collection, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Pair it with <c>UsingBenzene(...)</c> for handler and transport registration:
    /// </para>
    /// <code>
    /// var builder = WebApplication.CreateBuilder(args);
    ///
    /// builder.Services.UsingBenzene(x =&gt; x
    ///     .AddMessageHandlers(typeof(OrderCreatedHandler).Assembly)
    ///     .AddSqs()
    ///     .AddSns());
    ///
    /// builder.Services.AddBenzeneAwsLambdaHosting(events =&gt; events
    ///     .UseHttpBridgeV2()                          // HTTP -&gt; ASP.NET
    ///     .UseSqs(sqs =&gt; sqs.UseMessageHandlers())    // SQS  -&gt; Benzene
    ///     .UseSns(sns =&gt; sns.UseMessageHandlers()));  // SNS  -&gt; Benzene
    ///
    /// var app = builder.Build();
    /// app.MapGet("/orders/{id}", (string id) =&gt; new { orderId = id });
    /// app.Run();
    /// </code>
    /// <para>
    /// The event pipeline is configured now, against the application's service collection, so
    /// <c>UseSqs</c>/<c>UseSns</c> register their services before the container is built; it is built
    /// into a running pipeline later, inside <c>IServer.StartAsync</c>, once the container exists.
    /// </para>
    /// <para>
    /// The Benzene-driven <c>IServer</c> is registered only when running inside Lambda (detected via
    /// <c>AWS_LAMBDA_RUNTIME_API</c>); locally, Kestrel keeps serving so <c>dotnet run</c> still hosts the
    /// HTTP endpoints. <c>AddBenzene()</c> is called for you here — the composition footgun the manual
    /// recipe is prone to.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddBenzeneAwsLambdaHosting(
        this IServiceCollection services,
        Action<IMiddlewarePipelineBuilder<AwsEventStreamContext>> configureEvents)
    {
        // Configure the event pipeline against the app's service collection: UseSqs/UseSns register
        // their services now (before the container is built), while the pipeline itself is built later.
        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddBenzene();
        var eventPipeline = new MiddlewarePipelineBuilder<AwsEventStreamContext>(container);
        configureEvents(eventPipeline);

        // The built-in bridges, one per front-door payload shape — so a mixed function needs no adapter
        // class of its own whichever it is fronted by. Each is resolved per invocation only if the event
        // pipeline opts into it: UseHttpBridgeV2() -> v2, UseHttpBridge() -> REST v1, UseHttpBridgeAlb() ->
        // ALB. Registering all three is free until resolved, so the choice stays in the events pipeline.
        services.TryAddSingleton<IAwsHttpBridge<APIGatewayHttpApiV2ProxyRequest, APIGatewayHttpApiV2ProxyResponse>>(
            sp => new BenzeneAspNetBridge(sp));
        services.TryAddSingleton<IAwsHttpBridge<APIGatewayProxyRequest, APIGatewayProxyResponse>>(
            sp => new BenzeneAspNetRestBridge(sp));
        services.TryAddSingleton<IAwsHttpBridge<ApplicationLoadBalancerRequest, ApplicationLoadBalancerResponse>>(
            sp => new BenzeneAspNetAlbBridge(sp));

        // Take over the IServer only inside Lambda; locally leave Kestrel in place.
        if (IsRunningInLambda())
        {
            services.AddSingleton<IServer>(sp => new BenzeneLambdaServer(
                sp,
                provider => new AwsLambdaEntryPoint(
                    eventPipeline.Build(),
                    new MicrosoftServiceResolverFactory(provider))));
        }

        return services;
    }

    // AWS sets AWS_LAMBDA_RUNTIME_API in the execution environment; its absence means a local run.
    private static bool IsRunningInLambda()
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_LAMBDA_RUNTIME_API"));
}
