using System;
using Benzene.Abstractions.DI;
using Benzene.Clients;
using Benzene.Core.Middleware;
using Benzene.Grpc.Client;
using Benzene.Grpc.Serialization;
using Benzene.Grpc.Test.Helpers;
using Benzene.Grpc.Test.Protos;
using Benzene.Grpc.TestHelpers;
using Benzene.Results;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Benzene.Grpc.Test;

public class GrpcBenzeneMessageClientTest
{
    [Fact]
    public async Task SendMessageAsync_WhenTheCallSucceeds_ReturnsTheMappedResponse()
    {
        var invoker = new TestCallInvoker { Response = new EchoReply { Message = "hello" } };
        var client = BuildClient(invoker, out var registry);
        registry.Add<EchoRequest, EchoReply>("echo-topic", "/benzene.test.TestService/Echo");

        var result = await client.SendMessageAsync<EchoRequest, EchoReply>(
            new BenzeneClientRequest<EchoRequest>("echo-topic", new EchoRequest { Name = "world" }, new Dictionary<string, string>()));

        Assert.True(result.IsSuccessful);
        Assert.Equal(BenzeneResultStatus.Ok, result.Status);
        Assert.Equal("hello", result.Payload?.Message);
    }

    [Fact]
    public async Task SendMessageAsync_WhenTheCallFailsWithRpcException_ReturnsTheMappedErrorStatus()
    {
        var invoker = new TestCallInvoker { RpcExceptionToThrow = new RpcException(new Status(StatusCode.NotFound, "no such thing")) };
        var client = BuildClient(invoker, out var registry);
        registry.Add<EchoRequest, EchoReply>("echo-topic", "/benzene.test.TestService/Echo");

        var result = await client.SendMessageAsync<EchoRequest, EchoReply>(
            new BenzeneClientRequest<EchoRequest>("echo-topic", new EchoRequest { Name = "world" }, new Dictionary<string, string>()));

        Assert.False(result.IsSuccessful);
        Assert.Equal(BenzeneResultStatus.NotFound, result.Status);
        Assert.Contains(result.Errors, e => e.Message == "no such thing");
    }

    // Round-10 #109: ambient cancellation (e.g. the inbound call's deadline/cancel firing mid-send,
    // seen here as a bare TaskCanceledException out of the call invoker rather than an RpcException)
    // is a routine outcome, not an error worth Error-level noise.
    [Fact]
    public async Task SendMessageAsync_WhenAmbientCancellationFiresMidSend_DoesNotLogAtErrorLevel()
    {
        var invoker = new TestCallInvoker { ExceptionToThrow = new TaskCanceledException("The operation was canceled.") };
        var logger = new RecordingLogger<GrpcBenzeneMessageClient>();
        var client = BuildClient(invoker, out var registry, logger: logger);
        registry.Add<EchoRequest, EchoReply>("echo-topic", "/benzene.test.TestService/Echo");

        await client.SendMessageAsync<EchoRequest, EchoReply>(
            new BenzeneClientRequest<EchoRequest>("echo-topic", new EchoRequest { Name = "world" }, new Dictionary<string, string>()));

        Assert.DoesNotContain(LogLevel.Error, logger.Levels);
        Assert.DoesNotContain(LogLevel.Critical, logger.Levels);
    }

    [Fact]
    public async Task SendMessageAsync_WhenAmbientCancellationFiresMidSend_ReturnsACancellationFlavouredFailure()
    {
        var invoker = new TestCallInvoker { ExceptionToThrow = new TaskCanceledException("The operation was canceled.") };
        var client = BuildClient(invoker, out var registry);
        registry.Add<EchoRequest, EchoReply>("echo-topic", "/benzene.test.TestService/Echo");

        var result = await client.SendMessageAsync<EchoRequest, EchoReply>(
            new BenzeneClientRequest<EchoRequest>("echo-topic", new EchoRequest { Name = "world" }, new Dictionary<string, string>()));

        Assert.False(result.IsSuccessful);
        // Same classification a mid-flight RpcException(Cancelled) already resolves to via
        // DefaultGrpcStatusReverseMapper - both cancellation surfaces agree.
        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
    }

    // Phase 5 of work/archive/problem-details-plan-2026-08.md: the reverse read of GrpcMethodHandler.AddRichErrorDetails'
    // grpc-status-details-bin google.rpc.BadRequest, mirroring the HTTP client's ProblemDetails.Errors fix.
    [Fact]
    public async Task SendMessageAsync_FailureWithBadRequestDetails_PopulatesStructuredErrorsFromFieldViolations()
    {
        var richStatus = new Google.Rpc.Status { Code = (int)StatusCode.InvalidArgument, Message = "bad request" };
        var badRequest = new Google.Rpc.BadRequest();
        badRequest.FieldViolations.Add(new Google.Rpc.BadRequest.Types.FieldViolation { Field = "Name", Description = "Name must not be empty" });
        badRequest.FieldViolations.Add(new Google.Rpc.BadRequest.Types.FieldViolation { Description = "Age must be greater than 0" });
        richStatus.Details.Add(Google.Protobuf.WellKnownTypes.Any.Pack(badRequest));

        var trailers = new Metadata { { "grpc-status-details-bin", richStatus.ToByteArray() } };
        var invoker = new TestCallInvoker
        {
            RpcExceptionToThrow = new RpcException(new Status(StatusCode.InvalidArgument, "bad request"), trailers),
        };
        var client = BuildClient(invoker, out var registry);
        registry.Add<EchoRequest, EchoReply>("echo-topic", "/benzene.test.TestService/Echo");

        var result = await client.SendMessageAsync<EchoRequest, EchoReply>(
            new BenzeneClientRequest<EchoRequest>("echo-topic", new EchoRequest { Name = "world" }, new Dictionary<string, string>()));

        Assert.False(result.IsSuccessful);
        Assert.Equal(BenzeneResultStatus.BadRequest, result.Status);
        Assert.Equal(2, result.Errors.Count);
        Assert.Equal("Name must not be empty", result.Errors[0].Message);
        Assert.Equal("Name", result.Errors[0].Field);
        Assert.Equal("Age must be greater than 0", result.Errors[1].Message);
        Assert.Null(result.Errors[1].Field);
    }

    [Fact]
    public async Task SendMessageAsync_FailureWithNoBadRequestDetails_FallsBackToAMessageOnlyErrorFromStatusDetail()
    {
        var invoker = new TestCallInvoker { RpcExceptionToThrow = new RpcException(new Status(StatusCode.NotFound, "no such thing")) };
        var client = BuildClient(invoker, out var registry);
        registry.Add<EchoRequest, EchoReply>("echo-topic", "/benzene.test.TestService/Echo");

        var result = await client.SendMessageAsync<EchoRequest, EchoReply>(
            new BenzeneClientRequest<EchoRequest>("echo-topic", new EchoRequest { Name = "world" }, new Dictionary<string, string>()));

        var error = Assert.Single(result.Errors);
        Assert.Equal("no such thing", error.Message);
        Assert.Null(error.Field);
    }

    [Fact]
    public async Task SendMessageAsync_WhenNoRouteIsRegistered_ReturnsNotImplemented()
    {
        var invoker = new TestCallInvoker();
        var client = BuildClient(invoker, out _);

        var result = await client.SendMessageAsync<EchoRequest, EchoReply>(
            new BenzeneClientRequest<EchoRequest>("unregistered-topic", new EchoRequest { Name = "world" }, new Dictionary<string, string>()));

        Assert.False(result.IsSuccessful);
        Assert.Equal(BenzeneResultStatus.NotImplemented, result.Status);
    }

    [Fact]
    public async Task SendMessageAsync_PropagatesInboundGrpcDeadlineToTheDownstreamCall()
    {
        var deadline = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var accessor = new GrpcServerCallAccessor { CallContext = TestServerCallContext.Create(deadline: deadline) };
        var resolver = new Mock<IServiceResolver>();
        resolver.Setup(x => x.TryGetService<IGrpcServerCallAccessor>()).Returns(accessor);

        var invoker = new TestCallInvoker { Response = new EchoReply { Message = "hello" } };
        var client = BuildClient(invoker, out var registry, resolver.Object);
        registry.Add<EchoRequest, EchoReply>("echo-topic", "/benzene.test.TestService/Echo");

        await client.SendMessageAsync<EchoRequest, EchoReply>(
            new BenzeneClientRequest<EchoRequest>("echo-topic", new EchoRequest { Name = "world" }, new Dictionary<string, string>()));

        // The downstream call inherits the same absolute wall-clock deadline (deadline propagation).
        Assert.Equal(deadline, invoker.CapturedOptions.Deadline);
    }

    [Fact]
    public async Task SendMessageAsync_NoInboundCall_ForwardsNoDeadline()
    {
        var invoker = new TestCallInvoker { Response = new EchoReply { Message = "hello" } };
        var client = BuildClient(invoker, out var registry);
        registry.Add<EchoRequest, EchoReply>("echo-topic", "/benzene.test.TestService/Echo");

        await client.SendMessageAsync<EchoRequest, EchoReply>(
            new BenzeneClientRequest<EchoRequest>("echo-topic", new EchoRequest { Name = "world" }, new Dictionary<string, string>()));

        Assert.Null(invoker.CapturedOptions.Deadline);
    }

    private static GrpcBenzeneMessageClient BuildClient(TestCallInvoker invoker, out GrpcClientRouteRegistry registry, IServiceResolver? resolver = null, ILogger<GrpcBenzeneMessageClient>? logger = null)
    {
        registry = new GrpcClientRouteRegistry();
        var adapter = new ProtobufJsonGrpcMessageAdapter();

        var pipeline = new MiddlewarePipelineBuilder<GrpcSendMessageContext>(new NullBenzeneServiceContainer())
            .UseGrpcClient(invoker, registry, adapter)
            .Build();

        return new GrpcBenzeneMessageClient(pipeline, adapter, new DefaultGrpcStatusReverseMapper(), logger ?? NullLogger<GrpcBenzeneMessageClient>.Instance, resolver ?? new NullServiceResolver());
    }
}
