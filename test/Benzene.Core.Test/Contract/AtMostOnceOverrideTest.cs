using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda.KafkaEvents;
using Amazon.Lambda.SQSEvents;
using Amazon.SQS.Model;
using Azure.Messaging.ServiceBus;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Aws.Lambda.DynamoDb;
using Benzene.Aws.Lambda.Kafka;
using Benzene.Aws.Lambda.Kinesis;
using Benzene.Aws.Lambda.Sqs;
using Benzene.Aws.Sqs.Consumer;
using Benzene.Azure.Function.BlobStorage;
using Benzene.Azure.Function.Core;
using Benzene.Azure.Function.CosmosDb;
using Benzene.Azure.ServiceBus;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Benzene.Test.Examples;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using DynamoDbEvent = Benzene.Aws.Lambda.DynamoDb.DynamoDbEvent;
using KafkaContext = Benzene.Aws.Lambda.Kafka.KafkaContext;
using SqsMessage = Amazon.SQS.Model.Message;

namespace Benzene.Test.Contract;

// transport-bindings.md §1, obligation 8 (D16): every one-way binding MUST offer an at-most-once
// override that acknowledges whatever the outcome - a failure result, no result, or an uncaught error.
// These are the bindings that had none; the ones whose existing knobs already compose to it
// (RaiseOnFailureStatus = false + CatchExceptions = true, RabbitMQ AckMode.AutoAck, the stream workers'
// skip-and-continue defaults) are pinned by SettlementDefaultsConformanceTest and the matrix.
public class AtMostOnceOverrideTest
{
    private static Mock<IServiceResolverFactory> ResolverFactory(Action<Mock<IServiceResolver>>? setup = null)
    {
        var resolver = new Mock<IServiceResolver>();
        resolver.Setup(x => x.GetService<ISetCurrentTransport>()).Returns(Mock.Of<ISetCurrentTransport>());
        resolver.Setup(x => x.GetService<ILogger<SqsApplication>>()).Returns(NullLogger<SqsApplication>.Instance);
        resolver.Setup(x => x.GetService<ILogger<KafkaApplication>>()).Returns(NullLogger<KafkaApplication>.Instance);
        resolver.Setup(x => x.GetService<ILogger<DynamoDbApplication>>()).Returns(NullLogger<DynamoDbApplication>.Instance);
        resolver.Setup(x => x.GetService<ILogger<SqsConsumerApplication>>()).Returns(NullLogger<SqsConsumerApplication>.Instance);
        setup?.Invoke(resolver);
        var factory = new Mock<IServiceResolverFactory>();
        factory.Setup(x => x.CreateScope()).Returns(resolver.Object);
        return factory;
    }

    // ---- AWS Lambda SQS: SqsBatchFailureMode.AtMostOnce ----

    [Fact]
    public async Task LambdaSqs_AtMostOnce_ReportsNoFailure_ForAFailureResultNoResultOrException()
    {
        var pipeline = new Mock<IMiddlewarePipeline<SqsMessageContext>>();
        pipeline.Setup(x => x.HandleAsync(It.IsAny<SqsMessageContext>(), It.IsAny<IServiceResolver>()))
            .Returns((SqsMessageContext context, IServiceResolver _) =>
            {
                switch (context.SqsMessage.MessageId)
                {
                    case "failure": context.MessageResult = BenzeneResult.UnexpectedError(); break;
                    case "throws": throw new InvalidOperationException("boom");
                    // "no-result": nothing set
                }

                return Task.CompletedTask;
            });

        var application = new SqsApplication(pipeline.Object, new SqsOptions { BatchFailureMode = SqsBatchFailureMode.AtMostOnce });
        var @event = new SQSEvent
        {
            Records = new[] { "failure", "no-result", "throws" }
                .Select(id => new SQSEvent.SQSMessage { MessageId = id }).ToList()
        };

        var response = await application.HandleAsync(@event, ResolverFactory().Object);

        Assert.Empty(response.BatchItemFailures);
    }

    [Fact]
    public async Task LambdaSqs_AtMostOnce_InfrastructureFailure_StillFailsTheInvocation()
    {
        var pipeline = new Mock<IMiddlewarePipeline<SqsMessageContext>>();
        pipeline.Setup(x => x.HandleAsync(It.IsAny<SqsMessageContext>(), It.IsAny<IServiceResolver>()))
            .ThrowsAsync(new Benzene.Core.Exceptions.BenzeneResolutionException("mis-wired"));

        var application = new SqsApplication(pipeline.Object, new SqsOptions { BatchFailureMode = SqsBatchFailureMode.AtMostOnce });

        await Assert.ThrowsAnyAsync<Exception>(() => application.HandleAsync(
            new SQSEvent { Records = new List<SQSEvent.SQSMessage> { new() { MessageId = "m" } } }, ResolverFactory().Object));
    }

    // ---- AWS Lambda Kafka: KafkaBatchFailureMode.AtMostOnce ----

    [Fact]
    public async Task LambdaKafka_AtMostOnce_SkipsAFailedRecord_AndReportsNothing()
    {
        var processed = new List<long>();
        var pipeline = new Mock<IMiddlewarePipeline<KafkaContext>>();
        pipeline.Setup(x => x.HandleAsync(It.IsAny<KafkaContext>(), It.IsAny<IServiceResolver>()))
            .Returns((KafkaContext context, IServiceResolver _) =>
            {
                processed.Add(context.KafkaEventRecord.Offset);
                if (context.KafkaEventRecord.Offset == 1)
                {
                    throw new InvalidOperationException("boom");
                }

                context.MessageResult = context.KafkaEventRecord.Offset == 0 ? BenzeneResult.UnexpectedError() : BenzeneResult.Ok();
                return Task.CompletedTask;
            });

        var application = new KafkaApplication(pipeline.Object, new KafkaOptions { BatchFailureMode = KafkaBatchFailureMode.AtMostOnce });
        var @event = new KafkaEvent
        {
            Records = new Dictionary<string, IList<KafkaEvent.KafkaEventRecord>>
            {
                ["topic-0"] = new List<KafkaEvent.KafkaEventRecord>
                {
                    new() { Topic = "topic", Offset = 0 }, new() { Topic = "topic", Offset = 1 }, new() { Topic = "topic", Offset = 2 }
                }
            }
        };

        var response = await application.HandleAsync(@event, ResolverFactory().Object);

        Assert.Empty(response.BatchItemFailures);
        Assert.Equal(new long[] { 0, 1, 2 }, processed);
    }

    // ---- AWS Lambda DynamoDB Streams: DynamoDbOptions.AtMostOnce ----

    private static DynamoDbEvent DynamoDbEventWith(params string[] sequenceNumbers) => new()
    {
        Records = sequenceNumbers.Select(sequence => new DynamoDbStreamRecord
        {
            EventId = sequence,
            EventName = "INSERT",
            EventSourceArn = "arn:aws:dynamodb:eu-west-1:123456789012:table/orders/stream/2024",
            Dynamodb = new DynamoDbStreamData { SequenceNumber = sequence }
        }).ToList()
    };

    [Fact]
    public void DynamoDbOptions_DefaultIsNotAtMostOnce()
    {
        Assert.False(new DynamoDbOptions().AtMostOnce);
    }

    [Fact]
    public async Task DynamoDb_AtMostOnce_ContinuesPastFailures_AndReportsNothing()
    {
        var processed = new List<string>();
        var pipeline = new Mock<IMiddlewarePipeline<DynamoDbRecordContext>>();
        pipeline.Setup(x => x.HandleAsync(It.IsAny<DynamoDbRecordContext>(), It.IsAny<IServiceResolver>()))
            .Returns((DynamoDbRecordContext context, IServiceResolver _) =>
            {
                var sequence = context.Record.Dynamodb.SequenceNumber;
                processed.Add(sequence);
                if (sequence == "2")
                {
                    throw new InvalidOperationException("boom");
                }

                if (sequence == "1")
                {
                    context.MessageResult = BenzeneResult.UnexpectedError();
                }
                else if (sequence == "4")
                {
                    context.MessageResult = BenzeneResult.Ok();
                }
                // "3": no result

                return Task.CompletedTask;
            });

        var application = new DynamoDbApplication(pipeline.Object, new DynamoDbOptions { AtMostOnce = true });

        var response = await application.HandleAsync(DynamoDbEventWith("1", "2", "3", "4"), ResolverFactory().Object);

        Assert.Empty(response.BatchItemFailures);
        Assert.Equal(new[] { "1", "2", "3", "4" }, processed);
    }

    [Fact]
    public async Task DynamoDb_Default_StopsAtTheFirstFailure()
    {
        var pipeline = new Mock<IMiddlewarePipeline<DynamoDbRecordContext>>();
        pipeline.Setup(x => x.HandleAsync(It.IsAny<DynamoDbRecordContext>(), It.IsAny<IServiceResolver>()))
            .Returns((DynamoDbRecordContext context, IServiceResolver _) =>
            {
                context.MessageResult = context.Record.Dynamodb.SequenceNumber == "2" ? BenzeneResult.UnexpectedError() : BenzeneResult.Ok();
                return Task.CompletedTask;
            });

        var application = new DynamoDbApplication(pipeline.Object);

        var response = await application.HandleAsync(DynamoDbEventWith("1", "2", "3"), ResolverFactory().Object);

        Assert.Equal("2", Assert.Single(response.BatchItemFailures).ItemIdentifier);
    }

    [Fact]
    public async Task DynamoDb_AtMostOnce_InfrastructureFailure_StillStopsTheBatch()
    {
        var pipeline = new Mock<IMiddlewarePipeline<DynamoDbRecordContext>>();
        pipeline.Setup(x => x.HandleAsync(It.IsAny<DynamoDbRecordContext>(), It.IsAny<IServiceResolver>()))
            .ThrowsAsync(new Benzene.Core.Exceptions.BenzeneResolutionException("mis-wired"));

        var application = new DynamoDbApplication(pipeline.Object, new DynamoDbOptions { AtMostOnce = true });

        var response = await application.HandleAsync(DynamoDbEventWith("1", "2"), ResolverFactory().Object);

        Assert.Equal("1", Assert.Single(response.BatchItemFailures).ItemIdentifier);
    }

    // ---- AWS Lambda Kinesis: KinesisStreamOptions.AtMostOnce ----

    [Fact]
    public async Task Kinesis_AtMostOnce_ThrowingPipeline_CheckpointsTheWholeBatch()
    {
        var services = ServiceResolverMother.CreateServiceCollection();
        var pipeline = new MiddlewarePipelineBuilder<StreamContext<KinesisEventRecord>>(new MicrosoftBenzeneServiceContainer(services))
            .UseStream<KinesisEventRecord>((_, _) => throw new InvalidOperationException("boom"))
            .Build();
        var application = new KinesisStreamApplication(pipeline, new KinesisStreamOptions { AtMostOnce = true, CatchExceptions = false });

        var @event = new KinesisEvent
        {
            Records = new[] { "1", "2", "3" }.Select(sequence => new KinesisEventRecord
            {
                EventSource = "aws:kinesis",
                EventId = "shardId-000000000000:" + sequence,
                Kinesis = new KinesisRecordData { SequenceNumber = sequence }
            }).ToList()
        };

        var response = await application.HandleAsync(@event, ServiceResolverMother.CreateServiceResolverFactory());

        Assert.Empty(response.BatchItemFailures);
    }

    // ---- Self-hosted SQS consumer: SqsConsumerAckMode.AtMostOnce ----

    [Fact]
    public async Task SqsConsumer_AtMostOnce_AThrowingMessageDoesNotPropagate()
    {
        var pipeline = new Mock<IMiddlewarePipeline<SqsConsumerMessageContext>>();
        pipeline.Setup(x => x.HandleAsync(It.IsAny<SqsConsumerMessageContext>(), It.IsAny<IServiceResolver>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var application = new SqsConsumerApplication(pipeline.Object, new SqsConsumerOptions { AckMode = SqsConsumerAckMode.AtMostOnce });

        var result = await application.HandleAsync(
            new ReceiveMessageResponse { Messages = new List<SqsMessage> { new() { MessageId = "m", ReceiptHandle = "r" } } },
            ResolverFactory().Object);

        Assert.Equal("m", Assert.Single(result.FailedMessages).MessageId);
    }

    [Fact]
    public async Task SqsConsumer_AtMostOnce_InfrastructureFailure_Propagates_SoNothingIsDeleted()
    {
        var pipeline = new Mock<IMiddlewarePipeline<SqsConsumerMessageContext>>();
        pipeline.Setup(x => x.HandleAsync(It.IsAny<SqsConsumerMessageContext>(), It.IsAny<IServiceResolver>()))
            .ThrowsAsync(new Benzene.Core.Exceptions.BenzeneResolutionException("mis-wired"));

        var application = new SqsConsumerApplication(pipeline.Object, new SqsConsumerOptions { AckMode = SqsConsumerAckMode.AtMostOnce });

        await Assert.ThrowsAnyAsync<Exception>(() => application.HandleAsync(
            new ReceiveMessageResponse { Messages = new List<SqsMessage> { new() { MessageId = "m", ReceiptHandle = "r" } } },
            ResolverFactory().Object));
    }

    [Fact]
    public async Task SqsConsumer_AtMostOnce_DeletesTheFailedMessagesToo()
    {
        var sqs = new Mock<Amazon.SQS.IAmazonSQS>();
        sqs.SetupSequence(x => x.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiveMessageResponse
            {
                Messages = new List<SqsMessage> { new() { MessageId = "ok", ReceiptHandle = "r1" }, new() { MessageId = "failed", ReceiptHandle = "r2" } }
            })
            .ReturnsAsync(new ReceiveMessageResponse { Messages = new List<SqsMessage>() });

        using var stop = new CancellationTokenSource(5000);
        List<DeleteMessageBatchRequestEntry>? deleted = null;
        sqs.Setup(x => x.DeleteMessageBatchAsync(It.IsAny<DeleteMessageBatchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DeleteMessageBatchRequest, CancellationToken>((request, _) =>
            {
                deleted = request.Entries;
                stop.Cancel();
            })
            .ReturnsAsync(new DeleteMessageBatchResponse());

        var clientFactory = new Mock<ISqsClientFactory>();
        clientFactory.Setup(x => x.Create()).Returns(sqs.Object);

        var pipeline = new Mock<IMiddlewarePipeline<SqsConsumerMessageContext>>();
        pipeline.Setup(x => x.HandleAsync(It.IsAny<SqsConsumerMessageContext>(), It.IsAny<IServiceResolver>()))
            .Returns((SqsConsumerMessageContext context, IServiceResolver _) =>
            {
                context.MessageResult = context.Message.MessageId == "ok" ? BenzeneResult.Ok() : BenzeneResult.UnexpectedError();
                return Task.CompletedTask;
            });

        var options = new SqsConsumerOptions { AckMode = SqsConsumerAckMode.AtMostOnce };
        var resolverFactory = ResolverFactory(resolver =>
            resolver.Setup(x => x.GetService<ILogger<SqsConsumer>>()).Returns(NullLogger<SqsConsumer>.Instance));
        var consumer = new SqsConsumer(resolverFactory.Object, new SqsConsumerApplication(pipeline.Object, options),
            new SqsConsumerConfig { QueueUrl = "some-url", MaxNumberOfMessages = 10 }, clientFactory.Object, options);

        try
        {
            await consumer.StartAsync(stop.Token);
        }
        catch (OperationCanceledException)
        {
        }

        Assert.NotNull(deleted);
        Assert.Equal(new[] { "failed", "ok" }, deleted!.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal));
    }

    // ---- Self-hosted Service Bus worker: ServiceBusConsumerAckMode.AtMostOnce ----

    [Fact]
    public async Task ServiceBusWorker_AtMostOnce_AThrowingHandler_IsCompleted_NotAbandoned()
    {
        var pipeline = new Mock<IMiddlewarePipeline<ServiceBusConsumerContext>>();
        pipeline.Setup(x => x.HandleAsync(It.IsAny<ServiceBusConsumerContext>(), It.IsAny<IServiceResolver>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var resolverFactory = ResolverFactory(resolver =>
            resolver.Setup(x => x.GetService<ILogger<BenzeneServiceBusWorker>>()).Returns(NullLogger<BenzeneServiceBusWorker>.Instance));
        var worker = new BenzeneServiceBusWorker(resolverFactory.Object, new ServiceBusConsumerApplication(pipeline.Object),
            new BenzeneServiceBusConfig { QueueName = "orders", AckMode = ServiceBusConsumerAckMode.AtMostOnce },
            Mock.Of<IServiceBusClientFactory>());

        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(messageId: "m");
        var receiver = new Mock<ServiceBusReceiver>();
        receiver.Setup(x => x.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var onProcessMessage = typeof(BenzeneServiceBusWorker).GetMethod("OnProcessMessageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)onProcessMessage.Invoke(worker, new object[] { new ProcessMessageEventArgs(message, receiver.Object, CancellationToken.None) })!;

        receiver.Verify(x => x.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
        receiver.Verify(x => x.AbandonMessageAsync(It.IsAny<ServiceBusReceivedMessage>(),
            It.IsAny<IDictionary<string, object>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- Azure Functions Cosmos DB Change Feed trigger: CosmosDbChangeFeedOptions.AtMostOnce ----

    private class ChangedDocument
    {
        public string Id { get; set; } = string.Empty;
    }

    [Fact]
    public async Task CosmosDbTrigger_AtMostOnce_AThrowingBatch_ReturnsNormally()
    {
        var app = new InlineAzureFunctionStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app
                .UseCosmosDbChangeFeed<ChangedDocument>(
                    feed => feed.UseStream<ChangedDocument>((_, _) => throw new InvalidOperationException("boom")),
                    options => options.AtMostOnce = true))
            .Build();

        await app.HandleCosmosDbChanges(new[] { new ChangedDocument { Id = "1" } });
    }

    // ---- Azure Functions Blob trigger: BlobStorageOptions.AtMostOnce ----

    [Fact]
    public async Task BlobTrigger_Default_AThrowingDeliveryPropagates_SoTheHostRetries()
    {
        var app = new InlineAzureFunctionStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app.UseBlobStorage(blob => blob.UseBlob((Benzene.Azure.Function.BlobStorage.BlobTriggerEvent _) => throw new InvalidOperationException("boom"))))
            .Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => app.HandleBlob("uploads/a.json", "{}"));
    }

    [Fact]
    public async Task BlobTrigger_AtMostOnce_AThrowingDeliveryReturnsNormally()
    {
        var app = new InlineAzureFunctionStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app.UseBlobStorage(
                blob => blob.UseBlob((Benzene.Azure.Function.BlobStorage.BlobTriggerEvent _) => throw new InvalidOperationException("boom")),
                options => options.AtMostOnce = true))
            .Build();

        await app.HandleBlob("uploads/a.json", "{}");
    }
}
