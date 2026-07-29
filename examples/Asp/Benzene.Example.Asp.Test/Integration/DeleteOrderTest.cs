using System.Net;
using Benzene.Example.Asp.Test.Helpers;
using Benzene.Example.Asp.Test.Helpers.Builders;
using Benzene.Examples.App.Handlers;
using Benzene.Examples.App.Model.Messages;
using Xunit;

namespace Benzene.Example.Asp.Test.Integration;

[Trait("Category", "Integration")]
[Collection("Sequential")]
public class DeleteOrderTest : InMemoryOrdersTestBase
{
    private const string CreateOrder = MessageTopicNames.OrderCreate;
    private const string DeleteOrder = MessageTopicNames.OrderDelete;

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
    public async Task CreateOrder_ApiGateway()
    {
        await _client.SendAsync(new RequestBuilder(HttpMethod.Post, "/orders")
            .WithBody(CreateCreateOrderMessage())
            .Build());

        var order = GetPersistedOrders().First();
        Assert.NotNull(order);

        var response = await _client.SendAsync(new RequestBuilder(HttpMethod.Delete, $"/orders/{order.Id}")
            .WithBody(CreateCreateOrderMessage())
            .Build());
           
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var orders = GetPersistedOrders();

        Assert.Empty(orders);
    }

}