using Benzene.Abstractions.Messages.BenzeneClient;
using Benzene.Abstractions.Middleware;
using Benzene.Core.Middleware;
using Benzene.Grpc.Serialization;
using Grpc.Core;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Grpc.Client;

/// <summary>Pipeline-builder extensions for the outbound gRPC call path.</summary>
public static class Extensions
{
    /// <summary>Adds a <see cref="GrpcClientMiddleware"/> built from the given call invoker to the pipeline.</summary>
    /// <param name="app">The pipeline builder to add the middleware to.</param>
    /// <param name="callInvoker">The gRPC call invoker to call with.</param>
    /// <param name="routeRegistry">Resolves a Benzene topic to its gRPC method.</param>
    /// <param name="adapter">Converts between the wire protobuf request/response and the caller's declared types.</param>
    /// <returns>The pipeline builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<GrpcSendMessageContext> UseGrpcClient(
        this IMiddlewarePipelineBuilder<GrpcSendMessageContext> app, CallInvoker callInvoker, IGrpcClientRouteRegistry routeRegistry, IGrpcMessageAdapter adapter)
    {
        return app.Use(_ => new GrpcClientMiddleware(callInvoker, routeRegistry, adapter));
    }

    /// <summary>Adds a <see cref="GrpcClientMiddleware"/> resolved from the service container to the pipeline.</summary>
    /// <param name="app">The pipeline builder to add the middleware to.</param>
    /// <returns>The pipeline builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<GrpcSendMessageContext> UseGrpcClient(
        this IMiddlewarePipelineBuilder<GrpcSendMessageContext> app)
    {
        app.Register(x => x.AddScoped<GrpcClientMiddleware>());
        return app.Use<GrpcSendMessageContext, GrpcClientMiddleware>();
    }

    /// <summary>Converts <typeparamref name="TContext"/> to <typeparamref name="TContextOut"/> and builds the inner pipeline from <paramref name="action"/>.</summary>
    /// <param name="app">The pipeline builder to convert.</param>
    /// <param name="converter">Converts the context and maps the response back.</param>
    /// <param name="action">Configures the inner pipeline the converted context runs through.</param>
    /// <returns>The pipeline builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<TContext> Convert<TContext, TContextOut>(this IMiddlewarePipelineBuilder<TContext> app,
        IContextConverter<TContext, TContextOut> converter, Action<IMiddlewarePipelineBuilder<TContextOut>> action)
    {
        var middlewarePipeline = app.CreateMiddlewarePipeline(action);
        return app.Use(serviceResolver => new ContextConverterMiddleware<TContext, TContextOut>(converter, middlewarePipeline, serviceResolver));
    }

    /// <summary>Converts a Benzene outbound client context to a gRPC call and runs it through the given inner pipeline.</summary>
    /// <param name="app">The outbound client pipeline builder.</param>
    /// <param name="action">Configures the inner gRPC call pipeline.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<IBenzeneClientContext<T, Void>> UseGrpc<T>(this IMiddlewarePipelineBuilder<IBenzeneClientContext<T, Void>> app,
        Action<IMiddlewarePipelineBuilder<GrpcSendMessageContext>> action)
    {
        return Convert(app, new GrpcContextConverter<T>(), action);
    }

    /// <summary>Converts a Benzene outbound client context to a gRPC call, using the default <see cref="GrpcClientMiddleware"/> configuration.</summary>
    /// <param name="app">The outbound client pipeline builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<IBenzeneClientContext<T, Void>> UseGrpc<T>(this IMiddlewarePipelineBuilder<IBenzeneClientContext<T, Void>> app)
    {
        return app.Convert(new GrpcContextConverter<T>(), builder => builder.UseGrpcClient());
    }
}
