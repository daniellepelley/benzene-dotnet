using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Middleware;
using Benzene.Grpc.TestHelpers;
using Benzene.Grpc.Test.Protos;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Grpc.Test;

public class GrpcMethodHandlerTest
{
    [Fact]
    public async Task HandleAsync_WhenNoHandlerRegistered_ThrowsRpcExceptionNotFoundWithTrailer()
    {
        var pipeline = BuildPipeline(out var serviceResolverFactory);
        var handler = new GrpcMethodHandler(
            new GrpcMethodDefinition("/benzene.test.TestService/Echo", "grpc-test-topic-with-no-handler"), serviceResolverFactory, pipeline);
        var callContext = TestServerCallContext.Create();

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            handler.HandleAsync<EchoRequest, EchoReply>(new EchoRequest { Name = "x" }, callContext));

        Assert.Equal(StatusCode.NotFound, exception.StatusCode);
        Assert.Contains(callContext.ResponseTrailers, e => e.Key == "benzene-status" && e.Value == BenzeneResultStatus.NotFound);
    }

    [Fact]
    public async Task HandleAsync_WhenPipelineThrowsOperationCanceledException_ThrowsRpcExceptionCancelled()
    {
        var pipeline = BuildCancellingPipeline(out var serviceResolverFactory);
        var handler = new GrpcMethodHandler(new GrpcMethodDefinition("/x/y", "topic"), serviceResolverFactory, pipeline);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var callContext = TestServerCallContext.Create(cancellationToken: cts.Token);

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            handler.HandleAsync<EchoRequest, EchoReply>(new EchoRequest { Name = "x" }, callContext));

        Assert.Equal(StatusCode.Cancelled, exception.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_WhenPipelineThrowsOperationCanceledExceptionPastDeadline_ThrowsRpcExceptionDeadlineExceeded()
    {
        var pipeline = BuildCancellingPipeline(out var serviceResolverFactory);
        var handler = new GrpcMethodHandler(new GrpcMethodDefinition("/x/y", "topic"), serviceResolverFactory, pipeline);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var callContext = TestServerCallContext.Create(cancellationToken: cts.Token, deadline: DateTime.UtcNow.AddMilliseconds(-1));

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            handler.HandleAsync<EchoRequest, EchoReply>(new EchoRequest { Name = "x" }, callContext));

        Assert.Equal(StatusCode.DeadlineExceeded, exception.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_HandlerCanObserveCancellationTokenViaAccessor()
    {
        var pipeline = BuildPipeline(out var serviceResolverFactory);
        var handler = new GrpcMethodHandler(
            new GrpcMethodDefinition("/benzene.test.TestService/Echo", "grpc-test-accessor-topic"), serviceResolverFactory, pipeline);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var callContext = TestServerCallContext.Create(cancellationToken: cts.Token);

        var reply = await handler.HandleAsync<EchoRequest, EchoReply>(new EchoRequest { Name = "x" }, callContext);

        Assert.Equal("cancelled", reply.Message);
    }

    [Fact]
    public async Task HandleAsync_ValidationError_AttachesGoogleRpcStatusWithFieldViolations()
    {
        var pipeline = BuildResultPipeline(BenzeneResult.ValidationError("Name is required"), out var serviceResolverFactory);
        var handler = new GrpcMethodHandler(new GrpcMethodDefinition("/x/y", "topic"), serviceResolverFactory, pipeline);
        var callContext = TestServerCallContext.Create();

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            handler.HandleAsync<EchoRequest, EchoReply>(new EchoRequest { Name = "x" }, callContext));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        // The flat trailer is still there...
        Assert.Contains(callContext.ResponseTrailers, e => e.Key == "benzene-status" && e.Value == BenzeneResultStatus.ValidationError);

        // ...and the structured google.rpc.Status carries the field violations.
        var detailsBytes = callContext.ResponseTrailers.GetValueBytes("grpc-status-details-bin");
        Assert.NotNull(detailsBytes);
        var richStatus = Google.Rpc.Status.Parser.ParseFrom(detailsBytes);
        var badRequest = richStatus.Details[0].Unpack<Google.Rpc.BadRequest>();
        Assert.Equal("Name is required", badRequest.FieldViolations[0].Description);
    }

    // Phase 5 of work/archive/problem-details-plan-2026-08.md ("gRPC gets a free correctness win", ruling §5.3):
    // FieldViolation.Field is filled from BenzeneError.Field when the producer supplied one - the JSON
    // Pointer converted to gRPC's dotted path (wire-contracts §4.2).
    [Fact]
    public async Task HandleAsync_ValidationErrorWithStructuredErrors_FillsFieldViolationFieldFromBenzeneErrorField()
    {
        var errors = new[]
        {
            new Benzene.Abstractions.Results.BenzeneError("Name must not be empty", "/name", "NotEmptyValidator"),
            new Benzene.Abstractions.Results.BenzeneError("Sku must not be empty", "/items/0/sku", "NotEmptyValidator"),
        };
        var pipeline = BuildResultPipeline(BenzeneResult.ValidationError(errors), out var serviceResolverFactory);
        var handler = new GrpcMethodHandler(new GrpcMethodDefinition("/x/y", "topic"), serviceResolverFactory, pipeline);
        var callContext = TestServerCallContext.Create();

        await Assert.ThrowsAsync<RpcException>(() =>
            handler.HandleAsync<EchoRequest, EchoReply>(new EchoRequest { Name = "x" }, callContext));

        var detailsBytes = callContext.ResponseTrailers.GetValueBytes("grpc-status-details-bin");
        var richStatus = Google.Rpc.Status.Parser.ParseFrom(detailsBytes);
        var badRequest = richStatus.Details[0].Unpack<Google.Rpc.BadRequest>();

        Assert.Equal(2, badRequest.FieldViolations.Count);
        Assert.Equal("name", badRequest.FieldViolations[0].Field);
        Assert.Equal("Name must not be empty", badRequest.FieldViolations[0].Description);
        Assert.Equal("items[0].sku", badRequest.FieldViolations[1].Field);
        Assert.Equal("Sku must not be empty", badRequest.FieldViolations[1].Description);
    }

    // wire-contracts §4.2 (spec 1.1): a server SHOULD write the error's code into FieldViolation.reason.
    [Fact]
    public async Task HandleAsync_ErrorWithCode_WritesTheCodeIntoFieldViolationReason()
    {
        var errors = new[]
        {
            new Benzene.Abstractions.Results.BenzeneError("Name must not be empty", "/name", "NotEmptyValidator"),
            new Benzene.Abstractions.Results.BenzeneError("Too many items"),
        };
        var pipeline = BuildResultPipeline(BenzeneResult.ValidationError(errors), out var serviceResolverFactory);
        var handler = new GrpcMethodHandler(new GrpcMethodDefinition("/x/y", "topic"), serviceResolverFactory, pipeline);
        var callContext = TestServerCallContext.Create();

        await Assert.ThrowsAsync<RpcException>(() =>
            handler.HandleAsync<EchoRequest, EchoReply>(new EchoRequest { Name = "x" }, callContext));

        var richStatus = Google.Rpc.Status.Parser.ParseFrom(callContext.ResponseTrailers.GetValueBytes("grpc-status-details-bin"));
        var badRequest = richStatus.Details[0].Unpack<Google.Rpc.BadRequest>();
        Assert.Equal("NotEmptyValidator", GrpcFieldViolationReason.Get(badRequest.FieldViolations[0]));
        Assert.Equal("Name must not be empty", badRequest.FieldViolations[0].Description);
        // No code -> proto3's unset.
        Assert.Null(GrpcFieldViolationReason.Get(badRequest.FieldViolations[1]));
    }

    // §4.2: structured errors map onto google.rpc.BadRequest whatever the failure's status, so an error's
    // code (and field) survives a gRPC hop for, e.g., a conflict - as it does over HTTP.
    [Fact]
    public async Task HandleAsync_NonValidationFailureWithStructuredErrors_AttachesFieldViolationsToo()
    {
        var errors = new[] { new Benzene.Abstractions.Results.BenzeneError("Order already exists", "/orderId", "duplicate-order") };
        var pipeline = BuildResultPipeline(BenzeneResult.Set<EchoReply>(BenzeneResultStatus.Conflict, errors), out var serviceResolverFactory);
        var handler = new GrpcMethodHandler(new GrpcMethodDefinition("/x/y", "topic"), serviceResolverFactory, pipeline);
        var callContext = TestServerCallContext.Create();

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            handler.HandleAsync<EchoRequest, EchoReply>(new EchoRequest { Name = "x" }, callContext));

        Assert.Equal(StatusCode.AlreadyExists, exception.StatusCode);
        var richStatus = Google.Rpc.Status.Parser.ParseFrom(callContext.ResponseTrailers.GetValueBytes("grpc-status-details-bin"));
        var violation = Assert.Single(richStatus.Details[0].Unpack<Google.Rpc.BadRequest>().FieldViolations);
        Assert.Equal("orderId", violation.Field);
        Assert.Equal("duplicate-order", GrpcFieldViolationReason.Get(violation));
    }

    [Fact]
    public async Task HandleAsync_ValidationErrorWithNoField_LeavesFieldViolationFieldUnset()
    {
        var errors = new[] { new Benzene.Abstractions.Results.BenzeneError("Something went wrong") };
        var pipeline = BuildResultPipeline(BenzeneResult.ValidationError(errors), out var serviceResolverFactory);
        var handler = new GrpcMethodHandler(new GrpcMethodDefinition("/x/y", "topic"), serviceResolverFactory, pipeline);
        var callContext = TestServerCallContext.Create();

        await Assert.ThrowsAsync<RpcException>(() =>
            handler.HandleAsync<EchoRequest, EchoReply>(new EchoRequest { Name = "x" }, callContext));

        var detailsBytes = callContext.ResponseTrailers.GetValueBytes("grpc-status-details-bin");
        var richStatus = Google.Rpc.Status.Parser.ParseFrom(detailsBytes);
        var badRequest = richStatus.Details[0].Unpack<Google.Rpc.BadRequest>();

        Assert.Equal(string.Empty, badRequest.FieldViolations[0].Field);
    }

    private static IMiddlewarePipeline<GrpcContext> BuildResultPipeline(Benzene.Abstractions.Results.IBenzeneResult result, out IServiceResolverFactory serviceResolverFactory)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddBenzene().AddBenzeneMessage().AddGrpcMessageHandlers();

        var pipelineBuilder = new MiddlewarePipelineBuilder<GrpcContext>(container);
        pipelineBuilder.Use((context, _) =>
        {
            context.MessageHandlerResult = new MessageHandlerResult(result);
            return Task.CompletedTask;
        });

        serviceResolverFactory = container.CreateServiceResolverFactory();
        return pipelineBuilder.Build();
    }

    private static IMiddlewarePipeline<GrpcContext> BuildPipeline(out IServiceResolverFactory serviceResolverFactory)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddBenzene().AddBenzeneMessage().AddGrpcMessageHandlers();

        var pipelineBuilder = new MiddlewarePipelineBuilder<GrpcContext>(container);
        pipelineBuilder.UseMessageHandlers();

        serviceResolverFactory = container.CreateServiceResolverFactory();
        return pipelineBuilder.Build();
    }

    private static IMiddlewarePipeline<GrpcContext> BuildCancellingPipeline(out IServiceResolverFactory serviceResolverFactory)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddBenzene().AddBenzeneMessage().AddGrpcMessageHandlers();

        var pipelineBuilder = new MiddlewarePipelineBuilder<GrpcContext>(container);
        pipelineBuilder.Use((context, next) =>
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            return next();
        });

        serviceResolverFactory = container.CreateServiceResolverFactory();
        return pipelineBuilder.Build();
    }
}
