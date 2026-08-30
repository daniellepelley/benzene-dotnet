using System;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Core.MessageHandlers;
using Benzene.Http.Routing;
using Benzene.Schema.OpenApi;
using Benzene.Schema.OpenApi.OpenApi;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using Xunit;

namespace Benzene.Test.Autogen.Schema.OpenApi;

public class OpenApiDocumentBuilderTest
{
    // AddHttpEndpointDefinitions maps every operation eagerly (not deferred to Build()), so the
    // exception surfaces from that call itself.
    private static OpenApiDocument BuildDocumentWithMethod(string method, string topic = "tenant:create", string path = "/some-route")
    {
        var messageHandlerDefinition = MessageHandlerDefinition.CreateInstance(topic, typeof(Example), typeof(Inner));
        var httpEndpointDefinition = new HttpEndpointDefinition(method, path, topic);

        return new OpenApiDocumentBuilder(new SchemaBuilder())
            .AddInfo(new OpenApiInfo { Title = "svc", Version = "1.0" })
            .AddHttpEndpointDefinitions(
                new IHttpEndpointDefinition[] { httpEndpointDefinition },
                new IMessageHandlerDefinition[] { messageHandlerDefinition })
            .Build();
    }

    // #241: MapOperationType indexed a fixed 8-verb dictionary directly, so any HTTP verb outside
    // that set - a real but unsupported one (CONNECT), or a plain typo (Gett) - crashed the whole
    // spec build with an opaque KeyNotFoundException naming neither the bad verb nor which
    // handler/topic/path it came from.

    [Theory]
    [InlineData("Gett")]
    [InlineData("CONNECT")]
    public void AddHttpEndpointDefinitions_UnsupportedMethod_ThrowsDescriptiveException_NamingVerbAndTopicAndPath(string method)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildDocumentWithMethod(method, topic: "tenant:create", path: "/some-route"));

        Assert.Contains(method, ex.Message);
        Assert.Contains("tenant:create", ex.Message);
        Assert.Contains("/some-route", ex.Message);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("GET")]
    [InlineData("Get")]
    public void AddHttpEndpointDefinitions_SupportedMethod_CaseInsensitive_MapsSuccessfully(string method)
    {
        var doc = BuildDocumentWithMethod(method);

        var operation = Assert.Single(doc.Paths["/some-route"].Operations);
        Assert.Equal(OperationType.Get, operation.Key);
    }

    [Fact]
    public void SerializeAndDeserializeTest()
    {
        var messageHandlerDefinition = MessageHandlerDefinition.CreateInstance("tenant:create",
            typeof(Example),
            typeof(Inner));

        var httpEndpointDefinition = new HttpEndpointDefinition("GET", "/some-route", "tenant:create");

        var schemaBuilder = new OpenApiDocumentBuilder(new SchemaBuilder());
        var doc = schemaBuilder
            .AddInfo(new OpenApiInfo
            {
                Title = "benzene-tenant-core-func",
                Version = "1.0",
                Description = "Core Tenant Data"
            })
            .AddTag(new OpenApiTag
            {
                Name = "Core Service"
            })
            .AddTag(new OpenApiTag
            {
                Name = "benzene"
            })
            .AddHttpEndpointDefinitions(new IHttpEndpointDefinition[] { httpEndpointDefinition}, new IMessageHandlerDefinition[] { messageHandlerDefinition })
            .Build();
            
        var yaml = doc.SerializeAsYaml(OpenApiSpecVersion.OpenApi3_0);

        var doc1 = new OpenApiStringReader().Read(yaml, out _);
        var yaml1 = doc1.SerializeAsYaml(OpenApiSpecVersion.OpenApi3_0);

        Assert.Equal(yaml, yaml1);
    }
}

