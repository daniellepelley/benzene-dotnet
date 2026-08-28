using Benzene.Abstractions.DI;
using Benzene.Abstractions.Hosting;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Abstractions.Middleware;
using Benzene.Azure.Function.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.Info;
using Benzene.Core.MessageHandlers.Serialization;
using Benzene.Core.Middleware;

namespace Benzene.Azure.Function.EventGrid;

/// <summary>
/// Provides extension methods for registering Event Grid message-handling services and adding Event
/// Grid trigger handling to an <see cref="IAzureFunctionAppBuilder"/>.
/// </summary>
public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Registers the services required to process Event Grid-triggered events.
    /// </summary>
    /// <param name="services">The service container to register services with.</param>
    /// <returns>The service container, for method chaining.</returns>
    /// <remarks>
    /// Called automatically by <see cref="UseEventGrid(IAzureFunctionAppBuilder, Action{IMiddlewarePipelineBuilder{EventGridContext}})"/>; you don't normally need to call this directly.
    /// </remarks>
    public static IBenzeneServiceContainer AddAzureEventGrid(this IBenzeneServiceContainer services)
    {
        services.TryAddScoped<JsonSerializer>();
        services.TryAddScoped<PresetTopicHolder>();

        services.AddScoped<IMessageTopicGetter<EventGridContext>>(resolver =>
            new PresetTopicMessageTopicGetter<EventGridContext>(new EventGridMessageTopicGetter(), resolver.GetService<PresetTopicHolder>()));
        services.AddHeaderMessageVersionGetter<EventGridContext>();
        services.AddScoped<IMessageHeadersGetter<EventGridContext>, EventGridMessageHeadersGetter>();
        services.AddScoped<IMessageBodyGetter<EventGridContext>, EventGridMessageBodyGetter>();
        services.AddScoped<IMessageHandlerResultSetter<EventGridContext>, EventGridMessageHandlerResultSetter>();

        services.AddSingleton<ITransportInfo>(_ => new TransportInfo(TransportNames.EventGrid));
        return services;
    }

    /// <summary>
    /// Adds an Event Grid entry point application to the Azure Function app, configuring its inner
    /// middleware pipeline. Events route by their event type (<c>eventType</c> in the Event Grid
    /// schema, <c>type</c> in CloudEvents) - declare message handlers for those topics, or override
    /// with <c>UsePresetTopic(...)</c>.
    /// </summary>
    /// <param name="app">The Azure Function app builder to add Event Grid handling to.</param>
    /// <param name="action">The action that configures the Event Grid middleware pipeline.</param>
    /// <returns>The Azure Function app builder, for method chaining.</returns>
    public static IAzureFunctionAppBuilder UseEventGrid(this IAzureFunctionAppBuilder app, Action<IMiddlewarePipelineBuilder<EventGridContext>> action, Action<EventGridOptions> configure = null, string name = null)
    {
        app.Register(x => x.AddAzureEventGrid());
        var pipeline = app.Create<EventGridContext>();
        action(pipeline);
        var options = new EventGridOptions();
        configure?.Invoke(options);

        // Registered directly (not wrapped in the public EventGridApplication class) so the SAME
        // EventGridBatchApplication instance - and so the same EventGridOptions - backs both request
        // shapes below, mirroring Benzene.Azure.Function.ServiceBus's DependencyInjectionExtensions
        // (ServiceBusReceivedMessage[] + ServiceBusTriggerBatch over one ServiceBusBatchApplication).
        var batchApplication = new EventGridBatchApplication(pipeline.Build(), options);

        // The already-parsed-events shape - HandleEventGridEvents(params EventGridTriggerEvent[]).
        app.Add(name, serviceResolverFactory => new EntryPointMiddlewareApplication<EventGridTriggerEvent[]>(batchApplication, serviceResolverFactory));

        // The raw-JSON shape - HandleEventGridEvent(string) - round 14-15 #235: a distinct request
        // type (rather than parsing here and reusing the shape above) so EventGridTriggerEvent.Parse
        // runs inside EventGridBatchApplication's own guarded per-item pipeline execution, not before
        // dispatch - see EventGridContext's raw-JSON constructor and EventGridBatchApplication's
        // string[] HandleAsync overload for the full rationale.
        app.Add(name, serviceResolverFactory => new EntryPointMiddlewareApplication<string[]>(batchApplication, serviceResolverFactory));

        return app;
    }

    /// <summary>
    /// Applies Event Grid-specific configuration to a platform-neutral <see cref="IBenzeneApplicationBuilder"/>.
    /// No-op on any platform other than Azure Functions.
    /// </summary>
    /// <param name="app">The application builder passed to <c>BenzeneStartUp.Configure</c>.</param>
    /// <param name="action">The action that configures the Event Grid middleware pipeline.</param>
    /// <returns><paramref name="app"/>, for method chaining.</returns>
    public static IBenzeneApplicationBuilder UseEventGrid(this IBenzeneApplicationBuilder app, Action<IMiddlewarePipelineBuilder<EventGridContext>> action, Action<EventGridOptions> configure = null, string name = null)
    {
        if (app is IAzureFunctionAppBuilder azureApp)
        {
            azureApp.UseEventGrid(action, configure, name);
        }
        return app;
    }
}
