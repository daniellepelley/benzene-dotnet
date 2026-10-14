using System.Collections.Generic;
using System;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.SQSEvents;
using System.Net;
using System.Threading;
using Amazon.SQS;
using Amazon.SQS.Model;
using Benzene.Aws.Lambda.Core.TestHelpers;
using Benzene.Clients;
using Benzene.Examples.App.Handlers;
using Benzene.Examples.App.Model.Messages;
using Benzene.Examples.Aws.Tests.Helpers;
using Benzene.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Benzene.Examples.Aws.Tests.Integration;

/// <summary>
/// Proves the ingress/egress symmetry demo (release plan Tier 2.3) is actually reachable, in
/// isolation from the order-persistence tests in <c>CreateOrderTest</c>. Regression coverage for a
/// real bug: <c>DependenciesBuilder</c>'s <c>.AddMessageHandlers(...)</c> only scanned the shared
/// App domain's assembly, so <c>PublishOrderCreatedMessageHandler</c> (defined in this project's
/// own assembly) was never discoverable - no topic route, no HTTP route - despite compiling and
/// looking wired. No existing test in this suite called it before this one.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class PublishOrderCreatedTest
{
    [Fact]
    public async Task PublishOrderCreated_ApiGateway_SendsOnTheOrderCreatedTopic()
    {
        EnvironmentSetUp.SetUp();
        var fakeSender = new FakeBenzeneMessageSender();
        using var testLambdaHosting = BenzeneTestHost.Create<StartUp>()
            .WithServices(services => services.AddSingleton<IBenzeneMessageSender>(fakeSender))
            .BuildAwsLambdaTestHost();

        var orderCreated = new OrderCreatedEvent { Id = Guid.NewGuid(), Name = "acme" };
        var apiGatewayProxyRequest = new ApiGatewayProxyRequestBuilder("POST", "/orders/publish-created")
            .WithBody(orderCreated)
            .Build();

        var response = await testLambdaHosting.SendEventAsync<APIGatewayProxyResponse>(apiGatewayProxyRequest);

        Assert.Equal(202, response.StatusCode);
        Assert.Equal(MessageTopicNames.OrderCreated, fakeSender.LastTopic);

        // Assert the message *body* too, not just the topic - the egress demo's whole point is that
        // the OrderCreatedEvent handed to the handler is what gets published on the wire. This mirrors
        // the Azure example's egress test (Egress_PublishOrderCreated_SendsOnTheOrderCreatedTopic), so
        // both hosts prove ingress->handler->egress carries the payload through, not only the topic.
        var sent = Assert.IsType<OrderCreatedEvent>(fakeSender.LastRequest);
        Assert.Equal(orderCreated.Id, sent.Id);
        Assert.Equal("acme", sent.Name);
    }

    [Fact]
    public async Task OrderCreated_SQSEvent_ReachesTheNotificationHandler()
    {
        // The ingress half: the order_created message the egress demo publishes is routed by this
        // function's SQS trigger to OrderCreatedNotificationHandler.
        EnvironmentSetUp.SetUp();
        using var testLambdaHosting = BenzeneTestHost.Create<StartUp>().BuildAwsLambdaTestHost();
        var orderCreated = new OrderCreatedEvent { Id = Guid.NewGuid(), Name = "acme" };

        var response = await testLambdaHosting.SendEventAsync<SQSBatchResponse>(
            AwsEventBuilder.CreateSqsEvent(MessageTopicNames.OrderCreated, orderCreated));

        Assert.Empty(response?.BatchItemFailures ?? new List<SQSBatchResponse.BatchItemFailure>());
        Assert.Contains(orderCreated.Id, OrderCreatedNotificationHandler.Notified);
    }

    [Fact]
    public async Task PublishOrderCreated_TheQueueFailsOnce_TheSendIsRetriedAndTheCallerNeverSeesTheFailure()
    {
        // The route's UseRetry(OutboundRetry.Transient): the first SendMessage fails in transport, which
        // the SQS converter records as service-unavailable; the retry sends again and the second attempt
        // is accepted.
        EnvironmentSetUp.SetUp();
        var sqs = SqsFailingTimes(1);
        using var testLambdaHosting = BenzeneTestHost.Create<StartUp>()
            .WithServices(services => services.AddSingleton(sqs.Object))
            .BuildAwsLambdaTestHost();

        var response = await testLambdaHosting.SendEventAsync<APIGatewayProxyResponse>(PublishRequest());

        // 200, not the 202 of the fake-sender test above: the real SQS route maps SendMessage's HTTP
        // 200 to ok.
        Assert.Equal(200, response.StatusCode);
        sqs.Verify(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task PublishOrderCreated_TheQueueKeepsFailing_GivesUpAfterTheRetriesAndAnswers503()
    {
        EnvironmentSetUp.SetUp();
        var sqs = SqsFailingTimes(int.MaxValue);
        using var testLambdaHosting = BenzeneTestHost.Create<StartUp>()
            .WithServices(services => services.AddSingleton(sqs.Object))
            .BuildAwsLambdaTestHost();

        var response = await testLambdaHosting.SendEventAsync<APIGatewayProxyResponse>(PublishRequest());

        Assert.Equal(503, response.StatusCode);
        // One attempt plus the route's two retries.
        sqs.Verify(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    private static APIGatewayProxyRequest PublishRequest()
    {
        return new ApiGatewayProxyRequestBuilder("POST", "/orders/publish-created")
            .WithBody(new OrderCreatedEvent { Id = Guid.NewGuid(), Name = "acme" })
            .Build();
    }

    private static Mock<IAmazonSQS> SqsFailingTimes(int failures)
    {
        var calls = 0;
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref calls) <= failures
                ? throw new AmazonSQSException("Service Unavailable")
                : Task.FromResult(new SendMessageResponse { HttpStatusCode = HttpStatusCode.OK }));
        return sqs;
    }
}
