using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Core;
using Benzene.Grpc.Serialization;
using Benzene.Grpc.Streaming;
using Google.Protobuf;
using Grpc.Core;

namespace Benzene.Grpc;

/// <summary>Default <see cref="IGrpcMethodHandler"/> implementation: runs one call through a middleware pipeline in its own DI scope.</summary>
public class GrpcMethodHandler : IGrpcMethodHandler
{
    private readonly IGrpcMethodDefinition _grpcMethodDefinition;
    private readonly IServiceResolverFactory _serviceResolverFactory;
    private readonly IMiddlewarePipeline<GrpcContext> _middlewarePipeline;

    /// <summary>Initializes a new instance of the <see cref="GrpcMethodHandler"/> class.</summary>
    /// <param name="grpcMethodDefinition">The routed method (path and Benzene topic) this handler serves.</param>
    /// <param name="serviceResolverFactory">Creates the per-call DI scope the pipeline runs in.</param>
    /// <param name="middlewarePipeline">The pipeline each call is dispatched through.</param>
    public GrpcMethodHandler(IGrpcMethodDefinition grpcMethodDefinition, IServiceResolverFactory serviceResolverFactory, IMiddlewarePipeline<GrpcContext> middlewarePipeline)
    {
        _middlewarePipeline = middlewarePipeline;
        _serviceResolverFactory = serviceResolverFactory;
        _grpcMethodDefinition = grpcMethodDefinition;
    }

    /// <inheritdoc />
    public async Task<TResponse> HandleAsync<TRequest, TResponse>(TRequest request, ServerCallContext context)
        where TRequest : class
        where TResponse : class
    {
        var grpcContext = new GrpcContext<TRequest, TResponse>(_grpcMethodDefinition.Topic, context, request);
        using var resolver = _serviceResolverFactory.CreateScope();

        await RunPipelineAsync(grpcContext, context, resolver);

        if (grpcContext.Response is TResponse typed)
        {
            return typed;
        }

        return resolver.GetService<IGrpcMessageAdapter>().ConvertResponse<TResponse>(grpcContext.ResponsePayload);
    }

    /// <inheritdoc />
    public async Task ServerStreamingAsync<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context)
        where TRequest : class
        where TResponse : class
    {
        var grpcContext = new GrpcContext<TRequest, IAsyncEnumerable<TResponse>>(_grpcMethodDefinition.Topic, context, request);
        using var resolver = _serviceResolverFactory.CreateScope();

        await RunPipelineAsync(grpcContext, context, resolver, deferSuccessTrailer: true);

        var items = ResolveResponseStream<TRequest, TResponse>(grpcContext, resolver, context.CancellationToken);
        await WriteStreamAsync(items, responseStream, grpcContext, context, resolver);
    }

    /// <inheritdoc />
    public async Task<TResponse> ClientStreamingAsync<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, ServerCallContext context)
        where TRequest : class
        where TResponse : class
    {
        var requestItems = GrpcStreamAdapter.ReadAll(requestStream, context.CancellationToken);
        var grpcContext = new GrpcContext<IAsyncEnumerable<TRequest>, TResponse>(_grpcMethodDefinition.Topic, context, requestItems);
        using var resolver = _serviceResolverFactory.CreateScope();

        await RunPipelineAsync(grpcContext, context, resolver);

        if (grpcContext.Response is TResponse typed)
        {
            return typed;
        }

        return resolver.GetService<IGrpcMessageAdapter>().ConvertResponse<TResponse>(grpcContext.ResponsePayload);
    }

    /// <inheritdoc />
    public async Task DuplexStreamingAsync<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context)
        where TRequest : class
        where TResponse : class
    {
        var requestItems = GrpcStreamAdapter.ReadAll(requestStream, context.CancellationToken);
        var grpcContext = new GrpcContext<IAsyncEnumerable<TRequest>, IAsyncEnumerable<TResponse>>(_grpcMethodDefinition.Topic, context, requestItems);
        using var resolver = _serviceResolverFactory.CreateScope();

        await RunPipelineAsync(grpcContext, context, resolver, deferSuccessTrailer: true);

        var items = ResolveResponseStream<IAsyncEnumerable<TRequest>, TResponse>(grpcContext, resolver, context.CancellationToken);
        await WriteStreamAsync(items, responseStream, grpcContext, context, resolver);
    }

    /// <summary>
    /// Runs the middleware pipeline for one gRPC call, regardless of shape: populates the call accessor,
    /// translates a cancelled pipeline into the right <see cref="RpcException"/>, maps the handler's result
    /// status onto a trailer and (for non-OK results) an <see cref="RpcException"/>, and flushes any buffered
    /// response headers. Callers are responsible for extracting/converting the response (or response stream)
    /// from <paramref name="grpcContext"/> afterwards.
    /// </summary>
    /// <param name="grpcContext">The pipeline context for this call, carrying the request and (once the pipeline runs) the handler's result.</param>
    /// <param name="context">The underlying gRPC server call context.</param>
    /// <param name="resolver">The per-call DI scope the pipeline runs in.</param>
    /// <param name="deferSuccessTrailer">
    /// <c>true</c> for the two streaming shapes: on a successful pipeline result, skip writing the
    /// <c>benzene-status</c> trailer here and leave it to <see cref="WriteStreamAsync{TResponseItem}"/> to
    /// write it once the response stream has actually finished draining. A pipeline FAILURE (a non-OK
    /// status decided before any stream item is produced - e.g. request validation) still writes its
    /// trailer and throws immediately either way, since no stream has started yet. Unary/client-streaming
    /// callers always pass <c>false</c> (the default): the whole response is already known once the
    /// pipeline returns, so there is nothing to defer.
    /// </param>
    private async Task RunPipelineAsync<TRequest, TResponse>(GrpcContext<TRequest, TResponse> grpcContext, ServerCallContext context, IServiceResolver resolver, bool deferSuccessTrailer = false)
    {
        var callAccessor = resolver.TryGetService<GrpcServerCallAccessor>();
        if (callAccessor != null)
        {
            callAccessor.CallContext = context;
        }

        // Seed the scope's ambient cancellation token from the gRPC call's token (client cancel /
        // deadline), so a handler resolving ICancellationTokenAccessor observes it.
        resolver.SeedCancellationToken(context.CancellationToken);

        try
        {
            await _middlewarePipeline.HandleAsync(grpcContext, resolver);
        }
        catch (OperationCanceledException)
        {
            var cancelCode = DateTime.UtcNow >= context.Deadline ? StatusCode.DeadlineExceeded : StatusCode.Cancelled;
            throw new RpcException(new Status(cancelCode, "The call was cancelled."));
        }

        var status = grpcContext.MessageHandlerResult?.BenzeneResult.Status;
        var isSuccessful = grpcContext.MessageHandlerResult?.BenzeneResult.IsSuccessful ?? false;
        var statusCode = resolver.GetService<IGrpcStatusCodeMapper>().Map(status, isSuccessful);

        if (!(deferSuccessTrailer && statusCode == StatusCode.OK))
        {
            grpcContext.ResponseTrailers.Add("benzene-status", status ?? "Unknown");
        }

        if (statusCode != StatusCode.OK)
        {
            var errors = grpcContext.MessageHandlerResult?.BenzeneResult.Errors;
            var detail = errors is { Count: > 0 } ? string.Join("; ", errors) : status ?? "Error";
            AddRichErrorDetails(grpcContext.ResponseTrailers, statusCode, detail, status, errors);
            throw new RpcException(new Status(statusCode, detail));
        }

        if (grpcContext.ResponseHeaders.Count > 0)
        {
            await context.WriteResponseHeadersAsync(grpcContext.ResponseHeaders);
        }
    }

    /// <summary>
    /// Drains a streaming response (<see cref="GrpcStreamAdapter.WriteAll{T}"/>) and only then writes the
    /// <c>benzene-status</c> trailer - gRPC trailers are sent once, at call end, so this is safe, and it's
    /// what lets a mid-stream handler exception (#280) still land a truthful trailer instead of the
    /// success one <see cref="RunPipelineAsync{TRequest,TResponse}"/> would otherwise have written before
    /// the handler's iterator ever ran. A mid-drain exception is classified the same way
    /// <c>Benzene.Core.MessageHandlers.MessageHandler{TRequest,TResponse}</c> classifies a unary handler's
    /// exception (<see cref="ArgumentException"/> -&gt; ValidationError, <see cref="TimeoutException"/> -&gt;
    /// Timeout, <see cref="OperationCanceledException"/> -&gt; the same Cancelled/DeadlineExceeded
    /// translation <see cref="RunPipelineAsync{TRequest,TResponse}"/> uses, anything else -&gt;
    /// ServiceUnavailable), then run through the same <see cref="IGrpcStatusCodeMapper"/>/
    /// <see cref="AddRichErrorDetails"/> path as a pipeline-level failure.
    /// </summary>
    private static async Task WriteStreamAsync<TResponseItem>(IAsyncEnumerable<TResponseItem> items, IServerStreamWriter<TResponseItem> responseStream, GrpcContext grpcContext, ServerCallContext context, IServiceResolver resolver)
    {
        try
        {
            await GrpcStreamAdapter.WriteAll(items, responseStream, context.CancellationToken);

            var status = grpcContext.MessageHandlerResult?.BenzeneResult.Status;
            grpcContext.ResponseTrailers.Add("benzene-status", status ?? "Unknown");
        }
        catch (OperationCanceledException)
        {
            var cancelCode = DateTime.UtcNow >= context.Deadline ? StatusCode.DeadlineExceeded : StatusCode.Cancelled;
            throw new RpcException(new Status(cancelCode, "The call was cancelled."));
        }
        catch (Exception ex)
        {
            var (benzeneStatus, detail) = ClassifyStreamException(ex);
            var statusCode = resolver.GetService<IGrpcStatusCodeMapper>().Map(benzeneStatus, isSuccessful: false);
            grpcContext.ResponseTrailers.Add("benzene-status", benzeneStatus);
            AddRichErrorDetails(grpcContext.ResponseTrailers, statusCode, detail, benzeneStatus, errors: null);
            throw new RpcException(new Status(statusCode, detail));
        }
    }

    /// <summary>
    /// Classifies a mid-stream handler exception the same way
    /// <c>Benzene.Core.MessageHandlers.MessageHandler{TRequest,TResponse}.HandleAsync</c> classifies a
    /// unary handler's exception (see its remarks) - kept in sync deliberately, not shared, since the two
    /// call sites work with different exception-handling shapes (a result-returning try/catch there, a
    /// throw-through-<see cref="RpcException"/> one here).
    /// </summary>
    private static (string BenzeneStatus, string Detail) ClassifyStreamException(Exception ex)
    {
        return ex switch
        {
            ArgumentException => (Benzene.Results.BenzeneResultStatus.ValidationError, ex.Message),
            TimeoutException => (Benzene.Results.BenzeneResultStatus.Timeout, ex.Message),
            _ => (Benzene.Results.BenzeneResultStatus.ServiceUnavailable, ex.Message),
        };
    }

    /// <summary>
    /// Attaches a <c>google.rpc.Status</c> to the <c>grpc-status-details-bin</c> trailer alongside the
    /// flat <c>benzene-status</c> trailer, so a gRPC client can read structured error details. A
    /// <see cref="Benzene.Results.BenzeneResultStatus.ValidationError"/> maps its error messages to a
    /// <c>google.rpc.BadRequest</c> with one field violation per message - <see cref="Benzene.Abstractions.Results.BenzeneError.Field"/>
    /// fills <see cref="Google.Rpc.BadRequest.Types.FieldViolation.Field"/> when present (left unset,
    /// not empty-string, when the error isn't scoped to a field - Phase 5 of
    /// work/archive/problem-details-plan-2026-08.md, "gRPC gets a free correctness win", ruling §5.3). Carrying
    /// <see cref="Benzene.Abstractions.Results.BenzeneError.Code"/> in <c>google.rpc.ErrorInfo</c> is
    /// out of scope (parked, plan §8) - not added here.
    /// </summary>
    private static void AddRichErrorDetails(Metadata trailers, StatusCode statusCode, string detail, string? status, IReadOnlyList<Benzene.Abstractions.Results.BenzeneError>? errors)
    {
        var richStatus = new Google.Rpc.Status
        {
            Code = (int)statusCode,
            Message = detail,
        };

        if (status == Benzene.Results.BenzeneResultStatus.ValidationError && errors is { Count: > 0 })
        {
            var badRequest = new Google.Rpc.BadRequest();
            foreach (var error in errors)
            {
                var fieldViolation = new Google.Rpc.BadRequest.Types.FieldViolation { Description = error.Message };
                if (!string.IsNullOrEmpty(error.Field))
                {
                    fieldViolation.Field = error.Field;
                }

                badRequest.FieldViolations.Add(fieldViolation);
            }

            richStatus.Details.Add(Google.Protobuf.WellKnownTypes.Any.Pack(badRequest));
        }

        // The "-bin" suffix marks a binary metadata value; the client reads it via GetRpcStatus().
        trailers.Add("grpc-status-details-bin", richStatus.ToByteArray());
    }

    private static IAsyncEnumerable<TResponseItem> ResolveResponseStream<TRequest, TResponseItem>(GrpcContext<TRequest, IAsyncEnumerable<TResponseItem>> grpcContext, IServiceResolver resolver, CancellationToken cancellationToken)
        where TResponseItem : class
    {
        if (grpcContext.Response != null)
        {
            return grpcContext.Response;
        }

        var adapter = resolver.GetService<IGrpcMessageAdapter>();
        if (GrpcStreamAdapter.TryConvertStream(grpcContext.ResponsePayload, typeof(IAsyncEnumerable<TResponseItem>), adapter, isResponseDirection: true, cancellationToken) is IAsyncEnumerable<TResponseItem> converted)
        {
            return converted;
        }

        throw new RpcException(new Status(StatusCode.Internal, "The message handler did not produce a response stream."));
    }
}
