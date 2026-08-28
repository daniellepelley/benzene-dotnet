using System;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Benzene.Clients;
using Benzene.Clients.Azure.QueueStorage;
using Benzene.Core.Middleware;
using Benzene.Results;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Benzene.Test.Clients.Azure.QueueStorage;

/// <summary>
/// WP-I coverage debt: <see cref="QueueStorageBenzeneMessageClient"/> had zero direct unit tests
/// before this (see <c>work/bug-fix-rulings-round14-15-2026-08.md</c> WP-I). Mirrors the conventions
/// in <c>SnsBenzeneMessageClientTest</c> - success path, transport-throws-and-is-mapped-correctly,
/// and null-logger-doesn't-throw (the #266 fix).
/// </summary>
public class QueueStorageBenzeneMessageClientTest
{
    private static Mock<QueueClient> MockQueueClient(Exception? sendThrows = null)
    {
        var mock = new Mock<QueueClient>();
        var setup = mock.Setup(x => x.SendMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()));
        if (sendThrows is not null)
        {
            setup.ThrowsAsync(sendThrows);
        }
        else
        {
            setup.ReturnsAsync((Response<SendReceipt>)null);
        }

        return mock;
    }

    [Fact]
    public async Task SendMessageAsync_SendSucceeds_ReturnsAccepted()
    {
        var client = new QueueStorageBenzeneMessageClient(MockQueueClient().Object,
            NullLogger<QueueStorageBenzeneMessageClient>.Instance, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);
    }

    [Fact]
    public async Task SendMessageAsync_ThrowingQueueClient_ReturnsServiceUnavailable()
    {
        var client = new QueueStorageBenzeneMessageClient(MockQueueClient(new InvalidOperationException("boom")).Object,
            NullLogger<QueueStorageBenzeneMessageClient>.Instance, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
        Assert.Contains(result.Errors, e => e.Message.Contains("boom"));
    }

    // #266: a null logger must not make the catch block's own LogError throw and mask the real
    // send failure.
    [Fact]
    public async Task SendMessageAsync_NullLogger_DoesNotThrow_AndStillReturnsServiceUnavailable()
    {
        var client = new QueueStorageBenzeneMessageClient(MockQueueClient(new InvalidOperationException("boom")).Object,
            logger: null!, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
    }

    [Fact]
    public async Task SendMessageAsync_PrebuiltPipeline_SendSucceeds_ReturnsAccepted()
    {
        var pipeline = new MiddlewarePipelineBuilder<QueueStorageSendMessageContext>(new NullBenzeneServiceContainer())
            .UseQueueStorageClient(MockQueueClient().Object)
            .Build();

        var client = new QueueStorageBenzeneMessageClient(pipeline,
            NullLogger<QueueStorageBenzeneMessageClient>.Instance, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);
    }
}
