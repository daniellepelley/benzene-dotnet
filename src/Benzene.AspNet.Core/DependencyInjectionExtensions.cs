using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.MessageHandlers.Request;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Serialization;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.Info;
using Benzene.Core.MessageHandlers.MediaFormats;
using Benzene.Core.MessageHandlers.Request;
using Benzene.Core.MessageHandlers.Response;
using Benzene.Core.MessageHandlers.Serialization;
using Benzene.Core.Middleware;
using Benzene.Http;
using Benzene.Http.RequestBody;
using Benzene.Http.Routing;

namespace Benzene.AspNet.Core;

/// <summary>
/// Provides extension methods for registering the services required to process HTTP requests through
/// Benzene's message handler pipeline in ASP.NET Core.
/// </summary>
public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Registers the services required to route HTTP requests to message handlers.
    /// </summary>
    /// <param name="services">The service container to register services with.</param>
    /// <returns>The service container, for method chaining.</returns>
    /// <remarks>
    /// Called automatically by <see cref="BenzeneExtensions.UseHttp(IAspApplicationBuilder, Action{IMiddlewarePipelineBuilder{AspNetContext}})"/>; you don't normally need to
    /// call this directly.
    /// </remarks>
    public static IBenzeneServiceContainer AddAspNetMessageHandlers(this IBenzeneServiceContainer services)
    {
        // Single-resolve seams use TryAdd so a user registration made earlier (ConfigureServices runs
        // before Configure, where UseHttp calls this) wins over these defaults. Collection-resolved
        // services (IResponseHandler, IResponseRenderer, IRequestEnricher) stay plain Add: every
        // registration is part of the pipeline, not a default to be overridden.
        // Note: with no user override, AddBenzene's earlier TryAddSingleton<ISerializer, JsonSerializer>
        // now wins over this one - same stateless type, singleton instead of scoped, intentionally.
        services.TryAddScoped<ISerializer, JsonSerializer>();
        services.AddScoped<IMiddlewarePipelineBuilder<AspNetContext>, MiddlewarePipelineBuilder<AspNetContext>>();
        services.TryAddScoped<IMessageTopicGetter<AspNetContext>, AspNetMessageTopicGetter>();
        services.TryAddScoped<IMessageVersionGetter<AspNetContext>>(resolver =>
            new AspNetMessageVersionGetter(resolver.GetService<IRouteFinder>(),
                resolver.GetService<IMessageHeadersGetter<AspNetContext>>(),
                resolver.TryGetService<MessageVersionHeaderNames>()?.HeaderNames));
        services.TryAddScoped<IMessageHeadersGetter<AspNetContext>, AspNetMessageHeadersGetter>();
        services.TryAddScoped<IMessageBodyGetter<AspNetContext>, AspNetMessageBodyGetter>();
        services.TryAddScoped<IHttpRequestBodyReader<AspNetContext>, AspNetMessageBodyGetter>();
        services.AddScoped<HttpRequestBodyBuffer>();
        services.TryAddScoped<IMessageHandlerResultSetter<AspNetContext>, AspMessageHandlerResultSetter>();
        services.AddScoped<IResponseHandler<AspNetContext>, HttpStatusCodeResponseHandler<AspNetContext>>();
        services.AddScoped<IResponseRenderer<AspNetContext>, SerializerResponseRenderer<AspNetContext>>();
        services.AddScoped<IResponseHandler<AspNetContext>, RendererResponseHandler<AspNetContext>>();
        services.AddScoped<MessageRouter<AspNetContext>>();
        services.AddMediaFormatNegotiation<AspNetContext>();
        services
            .TryAddScoped<IRequestMapper<AspNetContext>,
                MultiSerializerOptionsRequestMapper<AspNetContext>>();
        services.AddScoped<IRequestEnricher<AspNetContext>, AspNetRequestEnricher>();
        services.TryAddScoped<IHttpRequestAdapter<AspNetContext>, AspNetHttpRequestAdapter>();
        services.TryAddScoped<IBenzeneResponseAdapter<AspNetContext>, AspNetResponseAdapter>();
        services.TryAddScoped<IHttpHeaderMappings, DefaultHttpHeaderMappings>();

        services.AddSingleton<ITransportInfo>(_ => new TransportInfo(TransportNames.Asp));
        services.AddHttpMessageHandlers();

        return services;
    }
}
