using System;
using System.Linq;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Messages.BenzeneClient;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Results;
using Benzene.Clients;
using Benzene.Core.Middleware;
using Benzene.Grpc;
using Benzene.Grpc.Serialization;
using Benzene.Results;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Grpc.Client;

/// <summary>
/// An <see cref="IBenzeneMessageClient"/> that calls another service over gRPC, so business logic
/// depends only on <c>IBenzeneMessageSender</c>/<c>IBenzeneMessageClient</c> and stays
/// transport-agnostic. A message is converted to a <see cref="GrpcSendMessageContext"/>, routed to a
/// gRPC method via <see cref="IGrpcClientRouteRegistry"/>, and run through a one-middleware call pipeline.
/// </summary>
public class GrpcBenzeneMessageClient : IBenzeneMessageClient
{
    private readonly IGrpcMessageAdapter _adapter;
    private readonly IGrpcStatusReverseMapper _statusReverseMapper;
    private readonly ILogger<GrpcBenzeneMessageClient> _logger;
    private readonly IServiceResolver _serviceResolver;
    private readonly IMiddlewarePipeline<GrpcSendMessageContext> _middlewarePipeline;

    /// <summary>Initializes a new instance calling over the given channel.</summary>
    /// <param name="channel">The gRPC channel to call on.</param>
    /// <param name="routeRegistry">Resolves a Benzene topic to its gRPC method.</param>
    /// <param name="adapter">Converts between the wire protobuf request/response and the caller's declared types.</param>
    /// <param name="statusReverseMapper">Maps a gRPC status back to a Benzene result status.</param>
    /// <param name="logger">Logs a send failure.</param>
    /// <param name="serviceResolver">The resolver the call pipeline runs in.</param>
    public GrpcBenzeneMessageClient(GrpcChannel channel, IGrpcClientRouteRegistry routeRegistry, IGrpcMessageAdapter adapter,
        IGrpcStatusReverseMapper statusReverseMapper, ILogger<GrpcBenzeneMessageClient> logger, IServiceResolver serviceResolver)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _adapter = adapter;
        _statusReverseMapper = statusReverseMapper;
        _logger = logger;
        _serviceResolver = serviceResolver;

        var benzeneServiceContainer = new NullBenzeneServiceContainer();
        var middlewarePipelineBuilder = new MiddlewarePipelineBuilder<GrpcSendMessageContext>(benzeneServiceContainer);
        _middlewarePipeline = middlewarePipelineBuilder
            .UseGrpcClient(channel.CreateCallInvoker(), routeRegistry, adapter)
            .Build();
    }

    /// <summary>Initializes a new instance from an already-built call pipeline (for testing).</summary>
    /// <param name="middlewarePipeline">The call pipeline to run each message through.</param>
    /// <param name="adapter">Converts between the wire protobuf request/response and the caller's declared types.</param>
    /// <param name="statusReverseMapper">Maps a gRPC status back to a Benzene result status.</param>
    /// <param name="logger">Logs a send failure.</param>
    /// <param name="serviceResolver">The resolver the call pipeline runs in.</param>
    public GrpcBenzeneMessageClient(IMiddlewarePipeline<GrpcSendMessageContext> middlewarePipeline, IGrpcMessageAdapter adapter,
        IGrpcStatusReverseMapper statusReverseMapper, ILogger<GrpcBenzeneMessageClient> logger, IServiceResolver serviceResolver)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _middlewarePipeline = middlewarePipeline;
        _adapter = adapter;
        _statusReverseMapper = statusReverseMapper;
        _logger = logger;
        _serviceResolver = serviceResolver;
    }

    /// <inheritdoc />
    public async Task<IBenzeneResult<TResponse>> SendMessageAsync<TRequest, TResponse>(IBenzeneClientRequest<TRequest> request)
    {
        try
        {
            // Propagate the ambient cancellation token (seeded by the gRPC server from the inbound
            // call's deadline/cancellation) onto the downstream RPC, so an upstream cancel aborts it
            // instead of leaving orphaned work running.
            var cancellationToken = _serviceResolver.TryGetService<ICancellationTokenAccessor>()?.CancellationToken ?? CancellationToken.None;
            var converter = new GrpcContextConverter<TRequest>(cancellationToken, ResolveDeadline());
            var context = await converter.CreateRequestAsync(new BenzeneClientContext<TRequest, Void>(request));

            await _middlewarePipeline.HandleAsync(context, _serviceResolver);

            var status = _statusReverseMapper.Map(context.Status.StatusCode, context.ResponseTrailers);

            if (context.Status.StatusCode != StatusCode.OK)
            {
                return BenzeneResult.Set<TResponse>(status, ExtractErrors(context.Status, context.ResponseTrailers));
            }

            var payload = _adapter.ConvertRequest<TResponse>(context.Response);
            return BenzeneResult.Set(status, payload!, true);
        }
        catch (OperationCanceledException ex)
        {
            // Ambient cancellation (the inbound call's deadline/cancel firing mid-send, propagated
            // via ICancellationTokenAccessor above) is a routine outcome, not a send failure worth
            // Error-level noise - mirror the server side's own OperationCanceledException handling
            // (GrpcMethodHandler.RunPipelineAsync, no log) rather than falling into the catch-all
            // below. Classified the same way an actual mid-flight RpcException(Cancelled) already
            // resolves via DefaultGrpcStatusReverseMapper, so both cancellation surfaces agree.
            return BenzeneResult.ServiceUnavailable<TResponse>(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sending message {topic} failed", request.Topic);
            return BenzeneResult.ServiceUnavailable<TResponse>(ex.Message);
        }
    }

    /// <summary>
    /// Recovers structured errors for a non-<see cref="StatusCode.OK"/> call: when the server attached
    /// a <c>google.rpc.BadRequest</c> to the <c>grpc-status-details-bin</c> trailer (see
    /// <c>Benzene.Grpc.GrpcMethodHandler.AddRichErrorDetails</c>), one <see cref="BenzeneError"/> per
    /// <c>FieldViolation</c> - <see cref="Google.Rpc.BadRequest.Types.FieldViolation.Description"/> as
    /// the message, <see cref="Google.Rpc.BadRequest.Types.FieldViolation.Field"/> as the field when
    /// non-empty (mirroring the HTTP client's <c>ProblemDetails.Errors</c> read). Otherwise falls back
    /// to a single message-only error from <see cref="Status.Detail"/> - unchanged behavior for a
    /// non-Benzene gRPC server, or a Benzene failure that isn't <c>ValidationError</c> (the only status
    /// the server attaches field violations for today).
    /// </summary>
    private static IReadOnlyList<BenzeneError> ExtractErrors(Status status, Metadata? trailers)
    {
        var fieldViolations = trailers?.GetRpcStatus(ignoreParseError: true)?.Details
            .Select(TryUnpackBadRequest)
            .FirstOrDefault(badRequest => badRequest != null)
            ?.FieldViolations;

        if (fieldViolations is { Count: > 0 })
        {
            return fieldViolations
                .Select(fieldViolation => new BenzeneError(
                    fieldViolation.Description,
                    string.IsNullOrEmpty(fieldViolation.Field) ? null : fieldViolation.Field))
                .ToArray();
        }

        return string.IsNullOrEmpty(status.Detail)
            ? Array.Empty<BenzeneError>()
            : new[] { new BenzeneError(status.Detail) };
    }

    private static Google.Rpc.BadRequest? TryUnpackBadRequest(Any detail)
    {
        return detail.Is(Google.Rpc.BadRequest.Descriptor) ? detail.Unpack<Google.Rpc.BadRequest>() : null;
    }

    private DateTime? ResolveDeadline()
    {
        // Deadline propagation: when this send happens inside an inbound gRPC call, inherit that call's
        // absolute deadline so the downstream call must finish by the same wall-clock time. The gRPC
        // ServerCallContext reports DateTime.MaxValue when the inbound call has no deadline - treat that
        // as "no deadline" rather than forwarding a max-value deadline.
        var callContext = _serviceResolver.TryGetService<IGrpcServerCallAccessor>()?.CallContext;
        var deadline = callContext?.Deadline;
        return deadline.HasValue && deadline.Value != DateTime.MaxValue ? deadline : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Method intentionally left empty.
    }
}
