using System.Linq;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Benzene.Examples.App.Handlers;
using Benzene.Examples.App.Model.Messages;
using Benzene.Examples.Aws.Tests.Helpers;
using Benzene.Aws.Lambda.Core.TestHelpers;
using Benzene.Examples.Aws.Tests.Helpers.Builders;
using Benzene.Testing;
using Xunit;

namespace Benzene.Examples.Aws.Tests.Integration;

[Trait("Category", "Integration")]
[Collection("Sequential")]
public class DeleteOrderTest : InMemoryOrdersTestBase
{
    private const string CreateOrder = MessageTopicNames.OrderCreate;
    private const string DeleteOrder = MessageTopicNames.OrderDelete;

    public DeleteOrderTest()
        :base(BenzeneTestHost.Create<StartUp>().BuildAwsLambdaHost())
    { }
    private static DeleteOrderMessage CreateDeleteOrderMessage(string orderId)
    {
        return new DeleteOrderMessage()
        {
            Id = orderId
        };
    }

    private CreateOrderMessage CreateCreateOrderMessage()
    {
        return new CreateOrderMessage
        {
            Name = Defaults.Order.Name,
            Status = Defaults.Order.Status,
        };
    }

    [Fact]
    public async Task DeleteOrder_SNSEvent()
    {
        //Create Order event
        var snsCreateEvent = AwsEventBuilder.CreateSnsEvent(CreateOrder, CreateCreateOrderMessage());
        await TestLambdaHosting.SendEventAsync(snsCreateEvent);
        var order = GetPersistedOrders().First();

        //Update Order event
        var snsDeleteEvent = AwsEventBuilder.CreateSnsEvent(DeleteOrder, CreateDeleteOrderMessage(order.Id.ToString()));
        await TestLambdaHosting.SendEventAsync(snsDeleteEvent);
        var orders =GetPersistedOrders().ToArray();

        Assert.Empty(orders);
    }

    [Fact]
    public async Task DeleteOrder_SQSEvent()
    {
        //Create Order event
        var snsCreateEvent = AwsEventBuilder.CreateSqsEvent(CreateOrder, CreateCreateOrderMessage());
        await TestLambdaHosting.SendEventAsync(snsCreateEvent);
        var order = GetPersistedOrders().First();
        Assert.NotNull(order);

        //Update Order event
        var sqsDeleteEvent = AwsEventBuilder.CreateSqsEvent(DeleteOrder, CreateDeleteOrderMessage(order.Id.ToString()));
        await TestLambdaHosting.SendEventAsync(sqsDeleteEvent);
        var orders = GetPersistedOrders();

        Assert.Empty(orders);
    }

    [Fact]
    public async Task CreateOrder_ApiGateway()
    {
        var createRequest = new ApiGatewayProxyRequestBuilder("POST", "/orders")
            .WithBody(CreateCreateOrderMessage())
            .Build();

        await TestLambdaHosting.SendEventAsync<APIGatewayProxyResponse>(createRequest);

        var order = GetPersistedOrders().First();
        Assert.NotNull(order);

        var apiGatewayProxyRequest = new ApiGatewayProxyRequestBuilder("DELETE", $"/orders/{order.Id}")
            .Build();

        var response = await TestLambdaHosting.SendEventAsync<APIGatewayProxyResponse>(apiGatewayProxyRequest);
        //Update Order event
           
        Assert.Equal(204, response.StatusCode);

        var orders = GetPersistedOrders();

        Assert.Empty(orders);
    }

}
