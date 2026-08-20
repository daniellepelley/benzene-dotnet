using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.EventHubs;
using Azure.Messaging.ServiceBus;
using Benzene.Abstractions.DI;
using Benzene.Azure.Function.BlobStorage;
using Benzene.Azure.Function.Core;
using Benzene.Azure.Function.CosmosDb;
using Benzene.Azure.Function.EventGrid;
using Benzene.Azure.Function.EventHub.Function;
using Benzene.Azure.Function.Kafka;
using Benzene.Azure.Function.Kafka.TestHelpers;
using Benzene.Azure.Function.QueueStorage;
using Benzene.Azure.Function.ServiceBus;
using Benzene.Azure.Function.Timer;
using Benzene.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.Middleware;
using Benzene.Test.Examples;
using Benzene.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Azure;

/// <summary>
/// Phase 3 of the cancellation initiative (<c>work/archive/cancellation-design-2026-08.md</c>): each Azure Functions
/// isolated-worker non-HTTP transport's <c>Handle*</c> extension now takes a cancellation token and
/// seeds it into the per-invocation scope, so a handler that resolves
/// <see cref="ICancellationTokenAccessor"/> observes whatever token the trigger method forwards - the
/// same token the source generator now emits as a bound <c>CancellationToken</c> trigger parameter.
/// </summary>
public class AzureFunctionCancellationTest
{
    [Fact]
    public async Task ServiceBus_HandleServiceBusMessages_WithToken_SeedsTheAccessor()
    {
        CancellationToken observed = default;
        using var cts = new CancellationTokenSource();

        var app = new InlineAzureFunctionStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app
                .UseServiceBus(serviceBus => serviceBus
                    .Use("Capture", (IServiceResolver resolver, ServiceBusContext _, Func<Task> next) =>
                    {
                        observed = resolver.GetService<ICancellationTokenAccessor>().CancellationToken;
                        return next();
                    })))
            .Build();

        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(new BinaryData("{}"));
        await app.HandleServiceBusMessages(cts.Token, message);

        Assert.Equal(cts.Token, observed);
    }

    [Fact]
    public async Task EventHub_HandleEventHub_WithToken_SeedsTheAccessor()
    {
        CancellationToken observed = default;
        using var cts = new CancellationTokenSource();

        var app = new InlineAzureFunctionStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app
                .UseEventHub(eventHub => eventHub
                    .Use("Capture", (IServiceResolver resolver, EventHubContext _, Func<Task> next) =>
                    {
                        observed = resolver.GetService<ICancellationTokenAccessor>().CancellationToken;
                        return next();
                    })))
            .Build();

        await app.HandleEventHub(cts.Token, new EventData(BinaryData.FromString("{}")));

        Assert.Equal(cts.Token, observed);
    }

    [Fact]
    public async Task Kafka_HandleKafkaEvents_WithToken_SeedsTheAccessor()
    {
        CancellationToken observed = default;
        using var cts = new CancellationTokenSource();

        var app = new InlineAzureFunctionStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app
                .UseKafka(kafka => kafka
                    .Use("Capture", (IServiceResolver resolver, KafkaContext _, Func<Task> next) =>
                    {
                        observed = resolver.GetService<ICancellationTokenAccessor>().CancellationToken;
                        return next();
                    })))
            .Build();

        var record = MessageBuilder.Create(Defaults.Topic, Defaults.MessageAsObject).AsAzureKafkaEvent();
        await app.HandleKafkaEvents(cts.Token, record);

        Assert.Equal(cts.Token, observed);
    }

    [Fact]
    public async Task QueueStorage_HandleQueueMessage_WithToken_SeedsTheAccessor()
    {
        CancellationToken observed = default;
        using var cts = new CancellationTokenSource();

        var app = new InlineAzureFunctionStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app
                .UseQueueStorage(queue => queue
                    .Use("Capture", (IServiceResolver resolver, QueueStorageContext _, Func<Task> next) =>
                    {
                        observed = resolver.GetService<ICancellationTokenAccessor>().CancellationToken;
                        return next();
                    })))
            .Build();

        await app.HandleQueueMessage("some-text", cts.Token);

        Assert.Equal(cts.Token, observed);
    }

    [Fact]
    public async Task BlobStorage_HandleBlob_WithToken_SeedsTheAccessor()
    {
        CancellationToken observed = default;
        using var cts = new CancellationTokenSource();

        var app = new InlineAzureFunctionStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app
                .UseBlobStorage(blob => blob
                    .Use("Capture", (IServiceResolver resolver, BlobStorageContext _, Func<Task> next) =>
                    {
                        observed = resolver.GetService<ICancellationTokenAccessor>().CancellationToken;
                        return next();
                    })))
            .Build();

        await app.HandleBlob("some/blob.txt", "content", cts.Token);

        Assert.Equal(cts.Token, observed);
    }

    [Fact]
    public async Task EventGrid_HandleEventGridEvent_WithToken_SeedsTheAccessor()
    {
        CancellationToken observed = default;
        using var cts = new CancellationTokenSource();

        var app = new InlineAzureFunctionStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app
                .UseEventGrid(eventGrid => eventGrid
                    .Use("Capture", (IServiceResolver resolver, EventGridContext _, Func<Task> next) =>
                    {
                        observed = resolver.GetService<ICancellationTokenAccessor>().CancellationToken;
                        return next();
                    })))
            .Build();

        var eventJson = $$"""
        {
            "id": "event-1",
            "topic": "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct",
            "subject": "/blobServices/default/containers/orders",
            "eventType": "{{Defaults.Topic}}",
            "eventTime": "2026-07-17T10:00:00Z",
            "dataVersion": "1.0",
            "data": {{Defaults.Message}}
        }
        """;

        await app.HandleEventGridEvent(eventJson, cts.Token);

        Assert.Equal(cts.Token, observed);
    }

    [Fact]
    public async Task CosmosDb_HandleCosmosDbChanges_WithToken_SeedsTheAccessor()
    {
        CancellationToken observed = default;
        using var cts = new CancellationTokenSource();

        var app = new InlineAzureFunctionStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app
                .UseCosmosDbChangeFeed<string>(feed => feed
                    .Use("Capture", (IServiceResolver resolver, Benzene.Core.Middleware.StreamContext<string> _, Func<Task> next) =>
                    {
                        observed = resolver.GetService<ICancellationTokenAccessor>().CancellationToken;
                        return next();
                    })))
            .Build();

        await app.HandleCosmosDbChanges(new List<string> { "doc-1" }, cts.Token);

        Assert.Equal(cts.Token, observed);
    }

    [Fact]
    public async Task Timer_HandleTimer_WithToken_SeedsTheAccessor()
    {
        CancellationToken observed = default;
        using var cts = new CancellationTokenSource();

        var app = new InlineAzureFunctionStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app
                .UseTimerTrigger(timer => timer
                    .Use("Capture", (IServiceResolver resolver, TimerContext _, Func<Task> next) =>
                    {
                        observed = resolver.GetService<ICancellationTokenAccessor>().CancellationToken;
                        return next();
                    })))
            .Build();

        await app.HandleTimer(cts.Token);

        Assert.Equal(cts.Token, observed);
    }

    private sealed class TokenCapturingEntryPoint<TEvent> : Benzene.Abstractions.Middleware.IEntryPointMiddlewareApplication<TEvent>
    {
        public CancellationToken? LastToken { get; private set; }

        public Task SendAsync(TEvent @event) => SendAsync(@event, CancellationToken.None);

        public Task SendAsync(TEvent @event, CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AzureFunctionApp_HandleAsync_FireAndForget_ForwardsTheTokenToTheEntryPoint()
    {
        using var cts = new CancellationTokenSource();
        var entryPoint = new TokenCapturingEntryPoint<string>();

        var app = new AzureFunctionApp(
            new (string? Key, Func<IServiceResolverFactory, Benzene.Abstractions.Middleware.IEntryPointMiddlewareApplication> Factory)[]
            {
                (null, _ => entryPoint)
            },
            Moq.Mock.Of<IServiceResolverFactory>());

        await app.HandleAsync("event", cancellationToken: cts.Token);

        Assert.Equal(cts.Token, entryPoint.LastToken);
    }

    [Fact]
    public async Task AzureFunctionApp_HandleAsync_WithResponse_ForwardsTheTokenToTheEntryPoint()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken observed = default;

        var pipeline = new Benzene.Core.Middleware.MiddlewarePipelineBuilder<string>(new Benzene.Microsoft.Dependencies.MicrosoftBenzeneServiceContainer())
            .Use((IServiceResolver resolver, string _, Func<Task> next) =>
            {
                observed = resolver.GetService<ICancellationTokenAccessor>().CancellationToken;
                return next();
            })
            .Build();

        var middlewareApplication = new Benzene.Core.Middleware.MiddlewareApplication<string, string, string>(pipeline, e => e, c => c);
        var entryPoint = new Benzene.Core.Middleware.EntryPointMiddlewareApplication<string, string>(
            middlewareApplication, CreateResolverFactoryWithAccessor());

        var app = new AzureFunctionApp(
            new (string? Key, Func<IServiceResolverFactory, Benzene.Abstractions.Middleware.IEntryPointMiddlewareApplication> Factory)[]
            {
                (null, _ => entryPoint)
            },
            Moq.Mock.Of<IServiceResolverFactory>());

        var result = await app.HandleAsync<string, string>("event", cancellationToken: cts.Token);

        Assert.Equal("event", result);
        Assert.Equal(cts.Token, observed);
    }

    private static IServiceResolverFactory CreateResolverFactoryWithAccessor()
    {
        var services = new ServiceCollection();
        services.AddScoped<CancellationTokenAccessor>();
        services.AddScoped<ICancellationTokenAccessor>(x => x.GetService<CancellationTokenAccessor>());
        return new Benzene.Microsoft.Dependencies.MicrosoftServiceResolverFactory(services);
    }
}
