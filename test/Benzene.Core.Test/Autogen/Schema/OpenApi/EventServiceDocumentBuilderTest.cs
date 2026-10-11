using System;
using System.Linq;
using System.Text.Json.Nodes;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.Info;
using Benzene.Core.Messages;
using Benzene.ResponseEvents;
using Benzene.Http.Routing;
using Benzene.Schema.OpenApi;
using Benzene.Schema.OpenApi.EventService;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;
using Xunit;

namespace Benzene.Test.Autogen.Schema.OpenApi;

public class EventServiceDocumentBuilderTest
{
    [Fact]
    public void SerializeAndDeserializeTest()
    {
        var messageHandlerDefinition = MessageHandlerDefinition.CreateInstance("tenant:create",
            typeof(Example),
            typeof(Inner[]));

        var responseEventDefinition = new ResponseEventDefinition("tenant:created", typeof(Example));
        
        var httpEndpointDefinition = new HttpEndpointDefinition("GET", "/some-route", "tenant:create");
        
        var messageSenderDefinition = MessageSenderDefinition.CreateInstance("tenant:updated", typeof(Example));

        var eventServiceDocumentBuilder = new EventServiceDocumentBuilder(new SchemaBuilder());
        var doc = eventServiceDocumentBuilder
            .AddService("benzene-tenant-core-func", "1.0", "Core Tenant Data", "semver")
            .AddMessageHandlerDefinitions(new[] { messageHandlerDefinition })
            .AddBroadcastEventDefinitions(new[] { responseEventDefinition })
            .AddMessageSenderDefinitions(new[] { messageSenderDefinition })
            .AddHttpEndpointDefinitions(new []{ httpEndpointDefinition }, new[] { messageHandlerDefinition })
            .Build();
            
        var json = doc.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0);

        var doc2 = new EventServiceDocumentDeserializer().Deserialize(json);

        var json2 = doc2.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0);

        Assert.Equal(json, json2);
    }

    [Fact]
    public void Build_WritesTheDocumentFields_IdentityAndBothHashes()
    {
        var doc = new EventServiceDocumentBuilder(new SchemaBuilder())
            .AddService("tenants", "1.4.2", "Tenant data", "semver")
            .AddMessageHandlerDefinitions(new[] { MessageHandlerDefinition.CreateInstance("tenant:create", typeof(Example), typeof(Inner)) })
            .Build();

        var json = JsonNode.Parse(doc.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0))!.AsObject();

        Assert.Equal("openapi-3.0", json["schemaDialect"]!.GetValue<string>());
        Assert.Equal("tenants", json["service"]!.GetValue<string>());
        Assert.Equal("1.4.2", json["serviceVersion"]!.GetValue<string>());
        Assert.Equal("semver", json["versionScheme"]!.GetValue<string>());
        Assert.Equal("Tenant data", json["description"]!.GetValue<string>());
        Assert.Equal(doc.ContractHash, json["contractHash"]!.GetValue<string>());
        Assert.Equal(doc.RegistrationHash, json["registrationHash"]!.GetValue<string>());
        Assert.StartsWith("sha256:", doc.ContractHash);
        Assert.NotNull(json["sends"]);
        foreach (var gone in new[] { "openapi", "info", "tags" })
        {
            Assert.False(json.ContainsKey(gone), $"'{gone}' is no longer a document field");
        }
    }

    [Fact]
    public void Build_NeverListsReservedTopics()
    {
        var domain = MessageHandlerDefinition.CreateInstance("tenant:create", typeof(Example), typeof(Inner));
        var spec = MessageHandlerDefinition.CreateInstance("benzene:spec", typeof(Example), typeof(Inner));
        var health = MessageHandlerDefinition.CreateInstance("benzene:healthcheck", typeof(Example), typeof(Inner));

        var doc = new EventServiceDocumentBuilder(new SchemaBuilder())
            .AddMessageHandlerDefinitions(new[] { domain, spec, health })
            .AddBroadcastEventDefinitions(new[] { new ResponseEventDefinition("benzene:mesh:register", typeof(Inner)) })
            .Build();

        Assert.Equal(new[] { "tenant:create" }, doc.Requests.Select(x => x.Topic));
        Assert.Empty(doc.Events);
        Assert.DoesNotContain("reserved", doc.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0));
    }

    [Fact]
    public void Build_ANoPayloadHandlerHasNoResponse_AndAOneWaySendHasNoResponse()
    {
        var noPayload = MessageHandlerDefinition.CreateInstance("tenant:delete", typeof(Example), typeof(Benzene.Abstractions.Results.Void));
        var oneWay = MessageSenderDefinition.CreateInstance("audit:log", typeof(Inner));
        var withReply = MessageSenderDefinition.CreateInstance("billing:quote", typeof(Inner), typeof(Example));

        var doc = new EventServiceDocumentBuilder(new SchemaBuilder())
            .AddMessageHandlerDefinitions(new[] { noPayload })
            .AddMessageSenderDefinitions(new[] { oneWay, withReply })
            .Build();

        Assert.Null(Assert.Single(doc.Requests).Response);
        Assert.Null(Assert.Single(doc.Sends, x => x.Topic == "audit:log").Response);
        Assert.NotNull(Assert.Single(doc.Sends, x => x.Topic == "billing:quote").Response);
        Assert.DoesNotContain("Void", doc.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0));

        var json = JsonNode.Parse(doc.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0))!;
        Assert.False(json["requests"]![0]!.AsObject().ContainsKey("response"));

        var roundTripped = new EventServiceDocumentDeserializer().Deserialize(json.ToJsonString());
        Assert.Null(Assert.Single(roundTripped.Requests).Response);
        Assert.Null(Assert.Single(roundTripped.Sends, x => x.Topic == "audit:log").Response);
    }

    [Fact]
    public void Build_ATopicDeclaredAsBothAnEventAndASend_Fails()
    {
        var builder = new EventServiceDocumentBuilder(new SchemaBuilder())
            .AddBroadcastEventDefinitions(new[] { new ResponseEventDefinition("order:placed", typeof(Inner)) })
            .AddMessageSenderDefinitions(new[] { MessageSenderDefinition.CreateInstance("order:placed", typeof(Inner)) });

        var error = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("order:placed", error.Message);
    }

    [Fact]
    public void Build_SynthesizesNoExamples()
    {
        var doc = new EventServiceDocumentBuilder(new SchemaBuilder())
            .AddMessageHandlerDefinitions(new[] { MessageHandlerDefinition.CreateInstance("tenant:create", typeof(Example), typeof(Inner)) })
            .AddBroadcastEventDefinitions(new[] { new ResponseEventDefinition("tenant:created", typeof(Inner)) })
            .Build();

        Assert.Null(doc.Requests[0].Example);
        Assert.Null(doc.Events[0].Example);
        Assert.DoesNotContain("\"example\"", doc.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0));
    }

    [Fact]
    public void Build_WritesVersion_ForVersionedRequestsAndEvents_ButOmitsItWhenUnversioned()
    {
        var versionedRequest = MessageHandlerDefinition.CreateInstance("shipping:booked", "v2", typeof(Example), typeof(Inner), typeof(object));
        var unversionedRequest = MessageHandlerDefinition.CreateInstance("tenant:create", typeof(Example), typeof(Inner));
        var versionedEvent = new ResponseEventDefinition(new Topic("shipping:booked", "v2"), typeof(Example));

        var doc = new EventServiceDocumentBuilder(new SchemaBuilder())
            .AddMessageHandlerDefinitions(new[] { versionedRequest, unversionedRequest })
            .AddBroadcastEventDefinitions(new[] { versionedEvent })
            .Build();

        Assert.Equal("v2", Assert.Single(doc.Requests, x => x.Topic == "shipping:booked").Version);
        Assert.Equal(string.Empty, Assert.Single(doc.Requests, x => x.Topic == "tenant:create").Version);
        Assert.Equal("v2", Assert.Single(doc.Events, x => x.Topic == "shipping:booked").Version);

        var json = doc.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0);
        Assert.Contains("\"version\": \"v2\"", json);

        // Round-trips through the deserializer.
        var roundTripped = new EventServiceDocumentDeserializer().Deserialize(json);
        Assert.Equal("v2", Assert.Single(roundTripped.Requests, x => x.Topic == "shipping:booked").Version);
        Assert.Equal(string.Empty, Assert.Single(roundTripped.Requests, x => x.Topic == "tenant:create").Version);
        Assert.Equal("v2", Assert.Single(roundTripped.Events, x => x.Topic == "shipping:booked").Version);
    }

    [Fact]
    public void Build_WritesTransports_WhenAnyAreRegistered_ButOmitsTheFieldWhenNone()
    {
        var transportsInfo = new TransportsInfo(new ITransportInfo[] { new TransportInfo("sqs"), new TransportInfo("http") });

        var doc = new EventServiceDocumentBuilder(new SchemaBuilder())
            .AddTransportsInfo(transportsInfo)
            .Build();

        Assert.Equal(new[] { "sqs", "http" }, doc.Transports);

        var json = doc.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0);
        Assert.Contains("\"transports\"", json);

        var roundTripped = new EventServiceDocumentDeserializer().Deserialize(json);
        Assert.Equal(new[] { "sqs", "http" }, roundTripped.Transports);

        var docWithNoTransports = new EventServiceDocumentBuilder(new SchemaBuilder()).Build();
        Assert.Empty(docWithNoTransports.Transports);

        var jsonWithNoTransports = docWithNoTransports.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0);
        Assert.DoesNotContain("\"transports\"", jsonWithNoTransports);

        var roundTrippedWithNoTransports = new EventServiceDocumentDeserializer().Deserialize(jsonWithNoTransports);
        Assert.Empty(roundTrippedWithNoTransports.Transports);
    }
}

