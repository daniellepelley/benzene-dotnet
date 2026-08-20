using System;
using Benzene.Abstractions.MessageHandlers;
using Benzene.CodeGen.Cli.Core.Commands.Build;
using Benzene.CodeGen.Client;
using Benzene.CodeGen.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Schema.OpenApi.EventService;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Cli;

// Phase 3b: the first tests CodeBuilderFactory has ever had - see
// work/archive/spec-mesh-tooling-implementation-plan-2026-08.md Phase 3b steps 4/5/7. Covers the --output mode
// validation (an unknown value used to silently fall through to the service client) and the new
// --namespace/--topics wiring into ClientSdkOptions.
public class CodeBuilderFactoryTest
{
    public class CreateOrder { public string Id { get; set; } = ""; }
    public class Order { public string Id { get; set; } = ""; public string Status { get; set; } = ""; }

    private static EventServiceDocument OrdersDocument() =>
        new IMessageHandlerDefinition[]
        {
            MessageHandlerDefinition.CreateInstance("order:create", typeof(CreateOrder), typeof(Order)),
            MessageHandlerDefinition.CreateInstance("order:cancel", typeof(CreateOrder), typeof(Order)),
        }.ToEventServiceDocument();

    [Fact]
    public void UnknownOutput_Throws_ListingValidValues()
    {
        var payload = new BuildPayload { Output = "bogus", ServiceName = "Orders" };

        var exception = Assert.Throws<ArgumentException>(() => new CodeBuilderFactory().Create(payload));

        Assert.Contains("bogus", exception.Message);
        Assert.Contains("client", exception.Message);
        Assert.Contains("topic-client", exception.Message);
        Assert.Contains("message-handlers", exception.Message);
        Assert.Contains("readme", exception.Message);
        // api-gateway's fate is frozen (2026-08-12 plan amendment) - its case still works, but it
        // stays out of the advertised/valid-values list.
        Assert.DoesNotContain("api-gateway", exception.Message);
    }

    [Fact]
    public void ApiGateway_StillWorks_DespiteBeingUndocumented()
    {
        var payload = new BuildPayload { Output = "api-gateway", LambdaName = "orders-func" };

        var builder = new CodeBuilderFactory().Create(payload);

        Assert.IsType<Benzene.CodeGen.ApiGateway.ApiGatewayBuilderV1>(builder);
    }

    [Fact]
    public void ExplicitNamespace_UsedExactly_ForClientMode()
    {
        var payload = new BuildPayload { Output = "client", ServiceName = "Orders", Namespace = "Acme.Orders.Clients" };

        var builder = new CodeBuilderFactory().Create(payload);
        var files = builder.BuildCodeFiles(OrdersDocument()).ToFilesDictionary();

        Assert.Contains("namespace Acme.Orders.Clients", files["OrdersServiceClient.cs"].ToLines());
    }

    [Fact]
    public void ExplicitNamespace_UsedAsRoot_ForTopicClientMode()
    {
        var payload = new BuildPayload { Output = "topic-client", ServiceName = "Orders", Namespace = "Acme.Orders.Clients" };

        var builder = new CodeBuilderFactory().Create(payload);
        var files = builder.BuildCodeFiles(OrdersDocument()).ToFilesDictionary();

        Assert.Contains("namespace Acme.Orders.Clients.OrderCreate", files["OrderCreate/OrderCreateServiceClient.cs"].ToLines());
    }

    [Fact]
    public void Topics_ScopesClientMode_MethodsInterfaceAndRequiredTopicsTogether()
    {
        var payload = new BuildPayload { Output = "client", ServiceName = "Orders", Topics = "order:create" };

        var builder = new CodeBuilderFactory().Create(payload);
        var files = builder.BuildCodeFiles(OrdersDocument()).ToFilesDictionary();

        Assert.Contains("CreateOrderAsync", files["OrdersServiceClient.cs"]);
        Assert.DoesNotContain("CancelOrderAsync", files["OrdersServiceClient.cs"]);
        Assert.Contains("CreateOrderAsync", files["IOrdersServiceClient.cs"]);
        Assert.DoesNotContain("CancelOrderAsync", files["IOrdersServiceClient.cs"]);
        Assert.Contains(@"RequiredTopics = { ""order:create"" }", files["OrdersServiceClient.cs"]);
    }

    [Fact]
    public void Topics_ScopesTopicClientMode_WhichClientsExist()
    {
        var payload = new BuildPayload { Output = "topic-client", ServiceName = "Orders", Topics = "order:create" };

        var builder = new CodeBuilderFactory().Create(payload);
        var files = builder.BuildCodeFiles(OrdersDocument()).ToFilesDictionary();

        Assert.Contains("OrderCreate/OrderCreateServiceClient.cs", files.Keys);
        Assert.DoesNotContain("OrderCancel/OrderCancelServiceClient.cs", files.Keys);
        Assert.Contains(@"RequiredTopics = { ""order:create"" }", files["OrderCreate/OrderCreateServiceClient.cs"]);
    }

    [Fact]
    public void Topics_CommaDelimited_ParsesMultipleTopics()
    {
        var payload = new BuildPayload { Output = "topic-client", ServiceName = "Orders", Topics = "order:create,order:cancel" };

        var builder = new CodeBuilderFactory().Create(payload);
        var files = builder.BuildCodeFiles(OrdersDocument()).ToFilesDictionary();

        Assert.Contains("OrderCreate/OrderCreateServiceClient.cs", files.Keys);
        Assert.Contains("OrderCancel/OrderCancelServiceClient.cs", files.Keys);
    }

    [Fact]
    public void Topics_UnknownTopic_Throws_NamingTheDocumentsValidTopics()
    {
        var payload = new BuildPayload { Output = "client", ServiceName = "Orders", Topics = "order:delete" };
        var builder = new CodeBuilderFactory().Create(payload);

        var exception = Assert.Throws<ArgumentException>(() => builder.BuildCodeFiles(OrdersDocument()));
        Assert.Contains("order:delete", exception.Message);
        Assert.Contains("order:create", exception.Message);
        Assert.Contains("order:cancel", exception.Message);
    }

    [Fact]
    public void ClientMode_EmitsTheDiRegistrationExtension()
    {
        var payload = new BuildPayload { Output = "client", ServiceName = "Orders", Namespace = "Acme.Orders.Clients" };

        var builder = new CodeBuilderFactory().Create(payload);
        var files = builder.BuildCodeFiles(OrdersDocument()).ToFilesDictionary();

        Assert.Contains("AddScoped<IOrdersServiceClient, OrdersServiceClient>()", files["OrdersServiceClientRegistration.cs"]);
    }

    [Fact]
    public void TopicClientMode_EmitsPerClientRegistrations_AndAnAggregateNamedFromServiceName()
    {
        // --service-name names no atomic client (each is named from its own topic) but it does name
        // the aggregate registration, so the CLI passes it through for exactly that.
        var payload = new BuildPayload { Output = "topic-client", ServiceName = "Orders", Namespace = "Acme.Orders.Clients" };

        var builder = new CodeBuilderFactory().Create(payload);
        var files = builder.BuildCodeFiles(OrdersDocument()).ToFilesDictionary();

        Assert.Contains("AddScoped<IOrderCreateServiceClient, OrderCreateServiceClient>()",
            files["OrderCreate/OrderCreateServiceClientRegistration.cs"]);
        Assert.Contains("AddOrderCreateServiceClient();", files["OrdersClientsRegistration.cs"]);
        Assert.Contains("AddOrderCancelServiceClient();", files["OrdersClientsRegistration.cs"]);
    }

    [Fact]
    public void NoNamespaceOrTopics_KeepsOriginalDerivationAndAllTopics()
    {
        var payload = new BuildPayload { Output = "client", ServiceName = "Orders" };

        var builder = new CodeBuilderFactory().Create(payload);
        var files = builder.BuildCodeFiles(OrdersDocument()).ToFilesDictionary();

        Assert.Contains("namespace Orders.Client.Orders", files["OrdersServiceClient.cs"].ToLines());
        Assert.Contains("CreateOrderAsync", files["OrdersServiceClient.cs"]);
        Assert.Contains("CancelOrderAsync", files["OrdersServiceClient.cs"]);
    }
}
