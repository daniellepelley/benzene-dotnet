using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Benzene.Abstractions;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.MessageHandlers.Request;
using Benzene.Abstractions.Messages;
using Benzene.Abstractions.Messages.BenzeneClient;
using Benzene.Abstractions.Results;
using Benzene.Clients;
using Benzene.Clients.CorrelationId;
using Benzene.Core.MessageHandlers;
using Benzene.Core.Messages;
using Benzene.DataAnnotations;
using Benzene.RabbitMq.RabbitMqSendMessage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Test.Customization;

// Regression tests for the customization-robustness fixes (see
// work/customization-robustness-review.md): each of these was a silent failure or a hard-coded
// customization point found by probing the framework the way an adopter would.
public class RobustnessFixesTest
{
    [Fact]
    public async Task RabbitMqConverter_NullHeaderValue_CoalescesInsteadOfThrowing()
    {
        // Before the fix: Encoding.UTF8.GetBytes(null) threw ArgumentNullException, which the
        // client's catch-all masked as a bare service-unavailable. Kafka already coalesced.
        var converter = new RabbitMqContextConverter<string>();
        var request = new BenzeneClientRequest<string>("some-topic", "body",
            new Dictionary<string, string> { ["x-tenant-id"] = null! });
        var context = new BenzeneClientContext<string, Void>(request);

        var outContext = await converter.CreateRequestAsync(context);

        Assert.Equal(Array.Empty<byte>(), (byte[])outContext.Headers["x-tenant-id"]!);
    }

    [Fact]
    public async Task CorrelationIdMiddleware_ExplicitPerCallHeader_IsNotClobbered()
    {
        // Before the fix: the middleware unconditionally overwrote the key, so a correlation id the
        // caller deliberately forwarded per-call was replaced by a self-generated GUID whenever
        // nothing had seeded ICorrelationId in the scope.
        var mockCorrelationId = new Mock<ICorrelationId>();
        mockCorrelationId.Setup(x => x.Get()).Returns(Guid.NewGuid().ToString());

        var context = new OutboundContext("my-topic", "hello",
            new Dictionary<string, string> { ["correlationId"] = "inbound-abc-123" });
        var middleware = new CorrelationIdMiddleware(mockCorrelationId.Object);

        await middleware.HandleAsync(context, () => Task.CompletedTask);

        Assert.Equal("inbound-abc-123", context.Headers["correlationId"]);
    }

    [Fact]
    public async Task CorrelationIdMiddleware_NoExplicitHeader_StillStamps()
    {
        var mockCorrelationId = new Mock<ICorrelationId>();
        mockCorrelationId.Setup(x => x.Get()).Returns("ambient-xyz");

        var context = new OutboundContext("my-topic", "hello");
        var middleware = new CorrelationIdMiddleware(mockCorrelationId.Object);

        await middleware.HandleAsync(context, () => Task.CompletedTask);

        Assert.Equal("ambient-xyz", context.Headers["correlationId"]);
    }

    public class AnnotatedRequest
    {
        [System.ComponentModel.DataAnnotations.Required]
        public string? Name { get; set; }
    }

    private sealed class QuarantineStatuses : IDefaultStatuses
    {
        public string ValidationError => "quarantined";
        public string NotFound => "not-found";
        public string BadRequest => "bad-request";
    }

    [Fact]
    public async Task DataAnnotationsValidation_UsesReplacedDefaultStatuses()
    {
        // Before the fix: the DataAnnotations middleware hard-coded validation-error, so replacing
        // IDefaultStatuses (the documented customization point) silently did nothing here.
        var middleware = new ValidationMiddleware<AnnotatedRequest, Void>(new QuarantineStatuses());
        var mockContext = new Mock<IMessageHandlerContext<AnnotatedRequest, Void>>();
        mockContext.SetupGet(x => x.Request).Returns(new AnnotatedRequest());
        IBenzeneResult<Void>? captured = null;
        mockContext.SetupSet(x => x.Response = It.IsAny<IBenzeneResult<Void>>())
            .Callback<IBenzeneResult<Void>>(r => captured = r);

        await middleware.HandleAsync(mockContext.Object, () => Task.CompletedTask);

        Assert.NotNull(captured);
        Assert.Equal("quarantined", captured!.Status);
    }

    [Fact]
    public async Task DataAnnotationsValidation_DefaultConstructor_KeepsValidationError()
    {
        var middleware = new ValidationMiddleware<AnnotatedRequest, Void>();
        var mockContext = new Mock<IMessageHandlerContext<AnnotatedRequest, Void>>();
        mockContext.SetupGet(x => x.Request).Returns(new AnnotatedRequest());
        IBenzeneResult<Void>? captured = null;
        mockContext.SetupSet(x => x.Response = It.IsAny<IBenzeneResult<Void>>())
            .Callback<IBenzeneResult<Void>>(r => captured = r);

        await middleware.HandleAsync(mockContext.Object, () => Task.CompletedTask);

        Assert.Equal(Benzene.Results.BenzeneResultStatus.ValidationError, captured!.Status);
    }

    public class RouterTestContext
    {
    }

    [Fact]
    public async Task MessageRouter_MissingTopicSentinel_EmitsActionableRemediation()
    {
        // The built-in topic getters convert an unresolvable topic to the "<missing>" sentinel, so
        // the router's null-topic "Topic is missing" branch never fired for them and users saw only
        // "No handler found for topic '<missing>'". The not-found branch now names the remedy.
        var messageGetter = new Mock<IMessageGetter<RouterTestContext>>();
        messageGetter.Setup(x => x.GetTopic(It.IsAny<RouterTestContext>()))
            .Returns(new Topic(null));

        var versionGetter = new Mock<IMessageVersionGetter<RouterTestContext>>();
        versionGetter.Setup(x => x.GetVersion(It.IsAny<RouterTestContext>())).Returns((string?)null);

        var lookUp = new Mock<IMessageHandlerDefinitionLookUp>();
        lookUp.Setup(x => x.FindHandler(It.IsAny<ITopic>()))
            .Returns((IMessageHandlerDefinition?)null);

        var defaultStatuses = new Mock<IDefaultStatuses>();
        defaultStatuses.SetupGet(x => x.NotFound).Returns("not-found");
        defaultStatuses.SetupGet(x => x.ValidationError).Returns("validation-error");

        IMessageHandlerResult? captured = null;
        var resultSetter = new Mock<IMessageHandlerResultSetter<RouterTestContext>>();
        resultSetter.Setup(x => x.SetResultAsync(It.IsAny<RouterTestContext>(), It.IsAny<IMessageHandlerResult>()))
            .Callback<RouterTestContext, IMessageHandlerResult>((_, r) => captured = r)
            .Returns(Task.CompletedTask);

        var router = new MessageRouter<RouterTestContext>(
            Mock.Of<IMessageHandlerFactory>(),
            messageGetter.Object,
            versionGetter.Object,
            lookUp.Object,
            Mock.Of<IRequestMapper<RouterTestContext>>(),
            resultSetter.Object,
            defaultStatuses.Object,
            NullLogger<MessageRouter<RouterTestContext>>.Instance);

        await router.HandleAsync(new RouterTestContext(), () => Task.CompletedTask);

        Assert.NotNull(captured);
        Assert.Equal("not-found", captured!.BenzeneResult.Status);
        Assert.Contains("No topic could be resolved", string.Join(" ", captured.BenzeneResult.Errors ?? Array.Empty<string>()));
    }
}
