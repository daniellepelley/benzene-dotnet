using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Benzene.Clients;
using Benzene.Clients.Azure.ServiceBus;
using Benzene.Core.Middleware;
using Benzene.Results;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Benzene.Test.Clients.Azure.ServiceBus;

/// <summary>
/// WP-I coverage debt: <see cref="ServiceBusBenzeneMessageClient"/> had zero direct unit tests before
/// this (see <c>work/bug-fix-rulings-round14-15-2026-08.md</c> WP-I). Mirrors the conventions in
/// <c>SnsBenzeneMessageClientTest</c> - success path, transport-throws-and-is-mapped-correctly, and
/// null-logger-doesn't-throw (the #266 fix).
/// </summary>
public class ServiceBusBenzeneMessageClientTest
{
    private static Mock<ServiceBusSender> MockSender(Exception? sendThrows = null)
    {
        var mockSender = new Mock<ServiceBusSender>();
        var setup = mockSender.Setup(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()));
        if (sendThrows is not null)
        {
            setup.ThrowsAsync(sendThrows);
        }
        else
        {
            setup.Returns(Task.CompletedTask);
        }

        return mockSender;
    }

    [Fact]
    public async Task SendMessageAsync_SendSucceeds_ReturnsAccepted()
    {
        var client = new ServiceBusBenzeneMessageClient(MockSender().Object,
            NullLogger<ServiceBusBenzeneMessageClient>.Instance, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);
    }

    [Fact]
    public async Task SendMessageAsync_ThrowingSender_ReturnsServiceUnavailable()
    {
        var client = new ServiceBusBenzeneMessageClient(MockSender(new InvalidOperationException("boom")).Object,
            NullLogger<ServiceBusBenzeneMessageClient>.Instance, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
        Assert.Contains(result.Errors, e => e.Message.Contains("boom"));
    }

    // #266: a null logger must not make the catch block's own LogError throw and mask the real
    // send failure.
    [Fact]
    public async Task SendMessageAsync_NullLogger_DoesNotThrow_AndStillReturnsServiceUnavailable()
    {
        var client = new ServiceBusBenzeneMessageClient(MockSender(new InvalidOperationException("boom")).Object,
            logger: null!, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
    }

    [Fact]
    public async Task SendMessageAsync_PrebuiltPipeline_SendSucceeds_ReturnsAccepted()
    {
        var pipeline = new MiddlewarePipelineBuilder<ServiceBusSendMessageContext>(new NullBenzeneServiceContainer())
            .UseServiceBusClient(MockSender().Object)
            .Build();

        var client = new ServiceBusBenzeneMessageClient(pipeline,
            NullLogger<ServiceBusBenzeneMessageClient>.Instance, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);
    }
}
