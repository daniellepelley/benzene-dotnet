using System.Collections.Generic;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.NewtonsoftJson;
using Moq;
using Xunit;

namespace Benzene.Test.Plugins.Newtonsoft;

public class NewtonsoftJsonMediaFormatTest
{
    private class TestContext
    {
        public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>();
    }

    private class FakeHeadersGetter : IMessageHeadersGetter<TestContext>
    {
        public IDictionary<string, string> GetHeaders(TestContext context) => context.Headers;
    }

    private static IServiceResolver CreateServiceResolver()
    {
        var serviceResolver = new Mock<IServiceResolver>();
        serviceResolver.Setup(x => x.GetService<IMessageHeadersGetter<TestContext>>()).Returns(new FakeHeadersGetter());
        return serviceResolver.Object;
    }

    [Fact]
    public void ContentType_IsApplicationJson()
    {
        var format = new NewtonsoftJsonMediaFormat<TestContext>(new JsonSerializer());

        Assert.Equal("application/json", format.ContentType);
    }

    [Fact]
    public void GetSerializer_ReturnsTheInjectedSerializer()
    {
        var injectedSerializer = new JsonSerializer();
        var format = new NewtonsoftJsonMediaFormat<TestContext>(injectedSerializer);

        Assert.Same(injectedSerializer, format.GetSerializer(Mock.Of<IServiceResolver>()));
    }

    [Fact]
    public void CanRead_MatchingContentType_ReturnsTrue()
    {
        var context = new TestContext();
        context.Headers["content-type"] = "application/json; charset=utf-8";

        var format = new NewtonsoftJsonMediaFormat<TestContext>(new JsonSerializer());

        Assert.True(format.CanRead(context, CreateServiceResolver()));
    }

    [Fact]
    public void CanRead_NonMatchingContentType_ReturnsFalse()
    {
        var context = new TestContext();
        context.Headers["content-type"] = "application/xml";

        var format = new NewtonsoftJsonMediaFormat<TestContext>(new JsonSerializer());

        Assert.False(format.CanRead(context, CreateServiceResolver()));
    }

    [Fact]
    public void CanRead_NoHeaders_ReturnsFalse()
    {
        var format = new NewtonsoftJsonMediaFormat<TestContext>(new JsonSerializer());

        Assert.False(format.CanRead(new TestContext(), CreateServiceResolver()));
    }

    [Fact]
    public void CanWrite_AcceptHeaderContainsJson_ReturnsTrue()
    {
        var context = new TestContext();
        context.Headers["accept"] = "application/xml, application/json;q=0.9";

        var format = new NewtonsoftJsonMediaFormat<TestContext>(new JsonSerializer());

        Assert.True(format.CanWrite(context, CreateServiceResolver()));
    }

    [Fact]
    public void CanWrite_AcceptHeaderMissingJson_ReturnsFalse()
    {
        var context = new TestContext();
        context.Headers["accept"] = "application/xml";

        var format = new NewtonsoftJsonMediaFormat<TestContext>(new JsonSerializer());

        Assert.False(format.CanWrite(context, CreateServiceResolver()));
    }

    [Fact]
    public void CanWrite_NoAcceptHeader_ReturnsFalse()
    {
        var format = new NewtonsoftJsonMediaFormat<TestContext>(new JsonSerializer());

        Assert.False(format.CanWrite(new TestContext(), CreateServiceResolver()));
    }
}
