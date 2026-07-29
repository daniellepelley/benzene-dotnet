using System.Net;
using Benzene.Example.Asp.Test.Helpers;
using Benzene.Examples.App.Handlers;
using Benzene.Examples.App.Model.Messages;
using Xunit;

namespace Benzene.Example.Asp.Test.Integration;

[Trait("Category", "Integration")]
[Collection("Sequential")]
public class CreateOrderTest : InMemoryOrdersTestBase
{
    private const string SomeStatus = "some-status";
    private const string SomeName = "some-name";
    private const string CreateOrder = MessageTopicNames.OrderCreate;

    private CreateOrderMessage CreateCreateOrderMessage()
    {
        return new CreateOrderMessage { Status = SomeStatus, Name = SomeName };
    }

    [Fact]
    public async Task CreateOrder_ApiGateway()
    {
        var response = await _client.SendAsync(new RequestBuilder(HttpMethod.Post, "/orders")
            .WithBody(CreateCreateOrderMessage()).Build()
        );
        
        var orders = GetPersistedOrders();

        Assert.Single(orders);
        Assert.Equal(SomeStatus, orders[0].Status);
        Assert.Equal(SomeName, orders[0].Name);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(await response.Content.ReadAsStringAsync());
    }

}