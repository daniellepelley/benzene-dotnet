using Benzene.ResponseEvents;
using Benzene.Schema.OpenApi;
using Benzene.Schema.OpenApi.AsyncApi;
using ByteBard.AsyncAPI;
using ByteBard.AsyncAPI.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Xunit;

namespace Benzene.Test.Autogen.Schema.OpenApi;

public class AsyncApiDocumentBuilderJsonTest
{
    private SchemaBuilder _schemaBuilder;
    private const string Topic = "tenant:created";
    private const string Topic2 = "tenant:updated";

    // #169: SchemaBuilder's reflection path now camelCases property names to match the wire.
    // AddJsonEvent infers its schema straight off the literal JSON's own keys, so that literal must
    // be camelCased the same way a real event body would be.
    private static readonly JsonSerializerSettings CamelCase = new() { ContractResolver = new CamelCasePropertyNamesContractResolver() };

    [Fact]
    public void Json_SimpleType()
    {
        var responseEventDefinition = new ResponseEventDefinition(Topic, typeof(Inner));

        var json = JsonConvert.SerializeObject(new Inner
        {
            Title = "some-title",
            Value = 42,
            Date = new System.DateTime(2023, 1, 1)
        }, CamelCase);

        var doc = CreateBuilder()
            .AddBroadcastEventDefinitions(new[] { responseEventDefinition })
            .Build();

        var doc2 = CreateBuilder()
            .AddJsonEvent(Topic, "Inner", json)
            .Build();

        var doc1Json = doc.SerializeAsJson(AsyncApiVersion.AsyncApi3_0);
        var doc2Json = doc2.SerializeAsJson(AsyncApiVersion.AsyncApi3_0);

        Assert.Equal(doc1Json, doc2Json);
    }

    [Fact]
    public void Json_NestedType()
    {
        var responseEventDefinition1 = new ResponseEventDefinition(Topic, typeof(Example));
        var responseEventDefinition2 = new ResponseEventDefinition(Topic2, typeof(Inner));

        var json = JsonConvert.SerializeObject(new Example
        {
            Title = "some-title",
            Value = 42,
            Inner = new[] {
                new Inner
                {
                    Title = "some-title",
                    Value = 42,
                    Date  = new System.DateTime(2023, 1, 1)
                }
            }
        }, CamelCase);

        var json2 = JsonConvert.SerializeObject(new Inner
        {
            Title = "some-title",
            Value = 42,
            Date = new System.DateTime(2023, 1, 1)
        }, CamelCase);

        var doc = CreateBuilder()
            .AddBroadcastEventDefinition(responseEventDefinition1)
            .AddBroadcastEventDefinition(responseEventDefinition2)
            .Build();

        var doc2 = CreateBuilder()
            .AddJsonEvent(Topic, "Example", json)
            .AddJsonEvent(Topic2, "Inner", json2)
            .Build();

        var doc1Json = doc.SerializeAsJson(AsyncApiVersion.AsyncApi3_0);
        var doc2Json = doc2.SerializeAsJson(AsyncApiVersion.AsyncApi3_0);

        Assert.Equal(doc1Json, doc2Json);
    }

    private AsyncApiDocumentBuilder CreateBuilder()
    {
        _schemaBuilder = new SchemaBuilder();
        return new AsyncApiDocumentBuilder(_schemaBuilder)
            .AddInfo(new AsyncApiInfo()
            {
                Title = "benzene-tenant-core-func",
                Version = "1.0",
                Description = "Core Tenant Data"
            })
            .AddTag(new AsyncApiTag
            {
                Name = "Core Service"
            })
            .AddTag(new AsyncApiTag
            {
                Name = "benzene"
            });
    }
}

