using System;
using System.Collections.Generic;
using Benzene.Abstractions.MessageHandlers.MediaFormats;
using Benzene.Abstractions.MessageHandlers.Request;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.BenzeneMessage;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.MessageHandlers.MediaFormats;
using Benzene.Core.MessageHandlers.Request;
using Benzene.Core.MessageHandlers.Serialization;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Test.Examples;
using Benzene.MessagePack;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Examples;
using Benzene.Xml;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
// `using static` (not `using Benzene.NewtonsoftJson;`) brings AddNewtonsoftJson() into scope
// without making the unqualified `JsonSerializer` used throughout this file (the
// Benzene.Core.MessageHandlers.Serialization one) ambiguous with Benzene.NewtonsoftJson.JsonSerializer.
using static Benzene.NewtonsoftJson.DependencyInjectionExtensions;
using NewtonsoftJsonSerializer = Benzene.NewtonsoftJson.JsonSerializer;

namespace Benzene.Test.Core.Core.MessageHandling;

public class DeferredRequestMapperTest
{
    [Fact]
    public void GetsRequest()
    {
        var serializer = new JsonSerializer();
        var context = new BenzeneMessageContext(new BenzeneMessageRequest
        {
            Body = serializer.Serialize(new ExampleRequestPayload { Name = "some-name"})
        });

        var requestMapper = new RequestMapper<BenzeneMessageContext>(new BenzeneMessageGetter(), serializer);
        var requestFactory = new DeferredRequestMapper<BenzeneMessageContext>(requestMapper, context);

        var request = requestFactory.GetRequest<ExampleRequestPayload>();

        Assert.NotNull(request);
    }

    [Fact]
    public void GetsRequest_No_Mappers_Returns_Null()
    {
        var serviceResolver = ServiceResolverMother.CreateServiceResolver();
        var mediaFormatNegotiator = new MediaFormatNegotiator<BenzeneMessageContext>(
            Array.Empty<IMediaFormat<BenzeneMessageContext>>(),
            new JsonMediaFormat<BenzeneMessageContext>(new JsonSerializer()),
            serviceResolver);

        var context = new BenzeneMessageContext(new BenzeneMessageRequest());
        var requestFactory = new DeferredRequestMapper<BenzeneMessageContext>(
            new MultiSerializerOptionsRequestMapper<BenzeneMessageContext>(mediaFormatNegotiator,
                serviceResolver,
                Mock.Of<IMessageBodyGetter<BenzeneMessageContext>>(),
                Array.Empty<IRequestEnricher<BenzeneMessageContext>>()), context);

        var request = requestFactory.GetRequest<ExampleRequestPayload>();

        Assert.NotNull(request);
    }


    [Fact]
    public void GetsRequest_Default_Mapper_Returns_Request()
    {
        var serviceResolver = ServiceResolverMother.CreateServiceResolver();
        var mediaFormatNegotiator = new MediaFormatNegotiator<BenzeneMessageContext>(
            new IMediaFormat<BenzeneMessageContext>[] { new InlineMediaFormat<BenzeneMessageContext>("application/json", new JsonSerializer(), _ => true) },
            new JsonMediaFormat<BenzeneMessageContext>(new JsonSerializer()),
            serviceResolver);

        var context = new BenzeneMessageContext(new BenzeneMessageRequest());
        var requestFactory = new DeferredRequestMapper<BenzeneMessageContext>(
            new MultiSerializerOptionsRequestMapper<BenzeneMessageContext>(mediaFormatNegotiator,
                serviceResolver,
                Mock.Of<IMessageBodyGetter<BenzeneMessageContext>>(),
                Array.Empty<IRequestEnricher<BenzeneMessageContext>>()), context);

        var request = requestFactory.GetRequest<ExampleRequestPayload>();

        Assert.Null(request!.Name);
    }


    [Fact]
    public void GetsRequest_Multi()
    {
        var serializer = new JsonSerializer();
        var context = new BenzeneMessageContext(new BenzeneMessageRequest
        {
            Body = serializer.Serialize(new ExampleRequestPayload { Name = "some-name"})
        });

        var serviceResolver = ServiceResolverMother.CreateServiceResolver();
        var mediaFormatNegotiator = new MediaFormatNegotiator<BenzeneMessageContext>(
            new IMediaFormat<BenzeneMessageContext>[] { new InlineMediaFormat<BenzeneMessageContext>("application/json", serializer, _ => true) },
            new JsonMediaFormat<BenzeneMessageContext>(serializer),
            serviceResolver);

        var requestMapper = new MultiSerializerOptionsRequestMapper<BenzeneMessageContext>(mediaFormatNegotiator,
                serviceResolver,
                new BenzeneMessageGetter(),
                Array.Empty<IRequestEnricher<BenzeneMessageContext>>());

        var requestFactory = new DeferredRequestMapper<BenzeneMessageContext>(requestMapper, context);

        var request = requestFactory.GetRequest<ExampleRequestPayload>();

        Assert.NotNull(request);
    }

    [Fact]
    public void GetsRequest_Multi_Xml()
    {
        var serializer = new XmlSerializer();
        var context = new BenzeneMessageContext(new BenzeneMessageRequest
        {
            Headers = new Dictionary<string, string> { { "content-type", "application/xml" }},
            Body = serializer.Serialize(new ExampleRequestPayload { Name = "some-name"})
        });

        var services = ServiceResolverMother.CreateServiceCollection();
        services.UsingBenzene(x => x.AddBenzeneMessage().AddXml());

        var serviceResolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
        var mediaFormatNegotiator = serviceResolver.GetService<IMediaFormatNegotiator<BenzeneMessageContext>>();

        var requestMapper = new MultiSerializerOptionsRequestMapper<BenzeneMessageContext>(
                mediaFormatNegotiator,
                serviceResolver,
                new BenzeneMessageGetter(),
                Array.Empty<IRequestEnricher<BenzeneMessageContext>>());

        var requestFactory = new DeferredRequestMapper<BenzeneMessageContext>(requestMapper, context);
        var request = requestFactory.GetRequest<ExampleRequestPayload>();

        Assert.Equal("some-name", request!.Name);
    }

    [Fact]
    public void GetsRequest_Multi_MessagePack()
    {
        var serializer = new MessagePackSerializer();
        var context = new BenzeneMessageContext(new BenzeneMessageRequest
        {
            Headers = new Dictionary<string, string> { { "content-type", "application/msgpack" }},
            Body = serializer.Serialize(new ExampleRequestPayload { Name = "some-name"})
        });

        var services = ServiceResolverMother.CreateServiceCollection();
        services.UsingBenzene(x => x.AddBenzeneMessage().AddMessagePack());

        var serviceResolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
        var mediaFormatNegotiator = serviceResolver.GetService<IMediaFormatNegotiator<BenzeneMessageContext>>();

        var requestMapper = new MultiSerializerOptionsRequestMapper<BenzeneMessageContext>(
                mediaFormatNegotiator,
                serviceResolver,
                new BenzeneMessageGetter(),
                Array.Empty<IRequestEnricher<BenzeneMessageContext>>());

        var requestFactory = new DeferredRequestMapper<BenzeneMessageContext>(requestMapper, context);
        var request = requestFactory.GetRequest<ExampleRequestPayload>();

        Assert.Equal("some-name", request!.Name);
    }

    [Fact]
    public void GetsRequest_Multi_NewtonsoftJson()
    {
        // application/json is also the process default's content type, but the default
        // JsonMediaFormat is only ever injected as the negotiator's fallback (never a negotiated
        // IMediaFormat<TContext> candidate - see its own doc comment), so AddNewtonsoftJson()
        // registering a real candidate at the same content type wins the negotiation and the body
        // is read through Json.NET rather than the System.Text.Json default.
        var serializer = new NewtonsoftJsonSerializer();
        var context = new BenzeneMessageContext(new BenzeneMessageRequest
        {
            Headers = new Dictionary<string, string> { { "content-type", "application/json" }},
            Body = serializer.Serialize(new ExampleRequestPayload { Name = "some-name"})
        });

        var services = ServiceResolverMother.CreateServiceCollection();
        services.UsingBenzene(x => x.AddBenzeneMessage().AddNewtonsoftJson());

        var serviceResolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
        var mediaFormatNegotiator = serviceResolver.GetService<IMediaFormatNegotiator<BenzeneMessageContext>>();

        var requestMapper = new MultiSerializerOptionsRequestMapper<BenzeneMessageContext>(
                mediaFormatNegotiator,
                serviceResolver,
                new BenzeneMessageGetter(),
                Array.Empty<IRequestEnricher<BenzeneMessageContext>>());

        var requestFactory = new DeferredRequestMapper<BenzeneMessageContext>(requestMapper, context);
        var request = requestFactory.GetRequest<ExampleRequestPayload>();

        Assert.Equal("some-name", request!.Name);
    }

    [Fact]
    public void GetsRequest_BenzeneMessageContext_IsWiredForTheBytePath()
    {
        var serializer = new JsonSerializer();
        var body = serializer.Serialize(new ExampleRequestPayload { Name = "some-name" });
        var context = new BenzeneMessageContext(new BenzeneMessageRequest { Body = body });

        var services = ServiceResolverMother.CreateServiceCollection();
        services.UsingBenzene(x => x.AddBenzeneMessage());

        var serviceResolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());

        // Phase 4's reference transport: BenzeneMessageContext registers IMessageBodyBytesGetter,
        // and JsonSerializer implements IPayloadSerializer, so MultiSerializerOptionsRequestMapper
        // resolves both and prefers the byte path.
        var bytesGetter = serviceResolver.GetService<IMessageBodyBytesGetter<BenzeneMessageContext>>();
        Assert.Equal(body, System.Text.Encoding.UTF8.GetString(bytesGetter.GetBodyBytes(context).Span));

        var mediaFormatNegotiator = serviceResolver.GetService<IMediaFormatNegotiator<BenzeneMessageContext>>();
        var requestMapper = new MultiSerializerOptionsRequestMapper<BenzeneMessageContext>(
            mediaFormatNegotiator, serviceResolver, new BenzeneMessageGetter(),
            Array.Empty<IRequestEnricher<BenzeneMessageContext>>());

        var requestFactory = new DeferredRequestMapper<BenzeneMessageContext>(requestMapper, context);
        var request = requestFactory.GetRequest<ExampleRequestPayload>();

        Assert.Equal("some-name", request!.Name);
    }
}
