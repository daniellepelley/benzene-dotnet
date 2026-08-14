using System.Collections.Generic;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.MediaFormats;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.MediaFormats;
using Benzene.Core.MessageHandlers.Response;
using Benzene.Core.MessageHandlers.Serialization;
using Benzene.Core.Messages;
using Benzene.Results;
using Benzene.Test.Examples;
using Benzene.Xml;
using Moq;
using Xunit;

namespace Benzene.Test.Core.Core.Response;

/// <summary>
/// Coverage for <see cref="SerializerResponseRenderer{TContext}"/>'s failure-branch content type
/// (Phase 3 of work/problem-details-plan.md, §2.3): the negotiated format is rewritten to its RFC
/// 9457 "problem" counterpart on a failed result, and left alone on success.
/// </summary>
public class SerializerResponseRendererContentTypeTest
{
    private class TestContext
    {
        public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>();
    }

    private class FakeHeadersGetter : IMessageHeadersGetter<TestContext>
    {
        public IDictionary<string, string> GetHeaders(TestContext context) => context.Headers;
    }

    private class FakeResponseAdapter : IBenzeneResponseAdapter<TestContext>
    {
        public string Body { get; private set; }
        public string ContentType { get; private set; }

        public void SetResponseHeader(TestContext context, string headerKey, string headerValue) { }
        public void SetContentType(TestContext context, string contentType) => ContentType = contentType;
        public void SetStatusCode(TestContext context, string statusCode) { }
        public void SetBody(TestContext context, string body) => Body = body;
        public string GetBody(TestContext context) => Body;
        public Task FinalizeAsync(TestContext context) => Task.CompletedTask;
    }

    private static IServiceResolver CreateServiceResolver()
    {
        var serviceResolver = new Mock<IServiceResolver>();
        serviceResolver.Setup(x => x.GetService<IMessageHeadersGetter<TestContext>>()).Returns(new FakeHeadersGetter());
        return serviceResolver.Object;
    }

    private static SerializerResponseRenderer<TestContext> CreateRenderer(out IServiceResolver resolver)
    {
        resolver = CreateServiceResolver();
        var negotiator = new MediaFormatNegotiator<TestContext>(
            new IMediaFormat<TestContext>[] { new XmlMediaFormat<TestContext>(new XmlSerializer()) },
            new JsonMediaFormat<TestContext>(new JsonSerializer()),
            resolver);
        return new SerializerResponseRenderer<TestContext>(new DefaultResponsePayloadMapper<TestContext>(), negotiator, resolver);
    }

    private static IMessageHandlerResult FailedResult()
    {
        var handlerDefinition = Mother.CreateMessageHandlerDefinitionV2();
        return new MessageHandlerResult(new Topic(Defaults.Topic), handlerDefinition, BenzeneResult.NotFound("not found"));
    }

    private static IMessageHandlerResult SuccessfulResult()
    {
        var handlerDefinition = Mother.CreateMessageHandlerDefinitionV2();
        return new MessageHandlerResult(new Topic(Defaults.Topic), handlerDefinition,
            BenzeneResult.Ok(Mother.CreateResponse("resp-name")));
    }

    [Fact]
    public async Task FailedResult_JsonNegotiated_ContentTypeIsApplicationProblemJson()
    {
        var renderer = CreateRenderer(out _);
        var adapter = new FakeResponseAdapter();

        await renderer.RenderAsync(new TestContext(), FailedResult(), adapter);

        Assert.Equal("application/problem+json", adapter.ContentType);
    }

    [Fact]
    public async Task FailedResult_XmlNegotiated_ContentTypeIsApplicationProblemXml()
    {
        var renderer = CreateRenderer(out _);
        var adapter = new FakeResponseAdapter();
        var context = new TestContext();
        context.Headers["accept"] = "application/xml";

        await renderer.RenderAsync(context, FailedResult(), adapter);

        Assert.Equal("application/problem+xml", adapter.ContentType);
    }

    [Fact]
    public async Task SuccessfulResult_JsonNegotiated_ContentTypeIsPlainApplicationJson()
    {
        var renderer = CreateRenderer(out _);
        var adapter = new FakeResponseAdapter();

        await renderer.RenderAsync(new TestContext(), SuccessfulResult(), adapter);

        Assert.Equal("application/json", adapter.ContentType);
    }

    [Fact]
    public async Task SuccessfulResult_XmlNegotiated_ContentTypeIsPlainApplicationXml()
    {
        var renderer = CreateRenderer(out _);
        var adapter = new FakeResponseAdapter();
        var context = new TestContext();
        context.Headers["accept"] = "application/xml";

        await renderer.RenderAsync(context, SuccessfulResult(), adapter);

        Assert.Equal("application/xml", adapter.ContentType);
    }
}
