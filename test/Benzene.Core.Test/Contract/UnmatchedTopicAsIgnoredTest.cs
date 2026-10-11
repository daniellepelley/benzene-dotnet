using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon.Lambda.SNSEvents;
using Amazon.Lambda.SQSEvents;
using Amazon.SQS.Model;
using Benzene.Abstractions;
using Benzene.Abstractions.Middleware;
using Benzene.Aws.Lambda.Core;
using Benzene.Aws.Lambda.Core.TestHelpers;
using Benzene.Aws.Lambda.Kafka;
using Benzene.Aws.Lambda.Kafka.TestHelpers;
using Benzene.Aws.Lambda.Sns;
using Benzene.Aws.Lambda.Sns.TestHelpers;
using Benzene.Aws.Lambda.Sqs;
using Benzene.Aws.Lambda.Sqs.TestHelpers;
using Benzene.Aws.Sqs;
using Benzene.Aws.Sqs.Consumer;
using Benzene.Aws.Sqs.TestHelpers;
using Benzene.Core.MessageHandlers;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Benzene.Test.Aws.Helpers;
using Benzene.Test.Examples;
using Benzene.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using KafkaContext = Benzene.Aws.Lambda.Kafka.KafkaContext;

namespace Benzene.Test.Contract;

// transport-bindings.md §1, obligation 8 (D16): an unmatched topic is a not-found failure, so a one-way
// binding does not acknowledge it - and a binding SHOULD offer to treat it as `ignored`, for a
// subscriber on a shared bus that sees other services' topics. UseUnmatchedTopicAsIgnored() is that
// option, with one name and shape on every binding's pipeline; off unless called.
public class UnmatchedTopicAsIgnoredTest
{
    private const string OtherServicesTopic = "someone-else:order-shipped";

    // ---- a queue: AWS Lambda SQS ----

    private static IBenzeneTestHost SqsHost(bool ignoreUnmatched)
    {
        return new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services
                .ConfigureServiceCollection()
                .AddTransient<ILogger<MessageRouter<SqsMessageContext>>>(_ => NullLogger<MessageRouter<SqsMessageContext>>.Instance)
                .UsingBenzene(x => x.AddSqs()))
            .Configure(app => app.UseSqs(sqs =>
            {
                if (ignoreUnmatched)
                {
                    sqs.UseUnmatchedTopicAsIgnored();
                }

                sqs.UseMessageHandlers();
            }))
            .BuildHost();
    }

    [Fact]
    public async Task Sqs_Default_AnUnmatchedTopicIsNotAcknowledged()
    {
        var response = await SqsHost(ignoreUnmatched: false)
            .SendSqsAsync(MessageBuilder.Create(OtherServicesTopic, Defaults.MessageAsObject).AsSqs());

        Assert.Single(response.BatchItemFailures);
    }

    [Fact]
    public async Task Sqs_UnmatchedTopicAsIgnored_AnUnmatchedTopicIsAcknowledged()
    {
        var response = await SqsHost(ignoreUnmatched: true)
            .SendSqsAsync(MessageBuilder.Create(OtherServicesTopic, Defaults.MessageAsObject).AsSqs());

        Assert.Empty(response.BatchItemFailures);
    }

    [Fact]
    public async Task Sqs_UnmatchedTopicAsIgnored_AMatchedTopicStillRoutes()
    {
        var response = await SqsHost(ignoreUnmatched: true)
            .SendSqsAsync(MessageBuilder.Create(Defaults.Topic, Defaults.MessageAsObject).AsSqs());

        Assert.Empty(response.BatchItemFailures);
    }

    [Fact]
    public async Task Sqs_UnmatchedTopicAsIgnored_AnUnservedReservedTopicIsStillNotFound()
    {
        // A reserved topic this service does not serve is a not-found the operator should see, never
        // ignored (wire-contracts §7). Not benzene:ping: the router answers that one itself.
        var response = await SqsHost(ignoreUnmatched: true)
            .SendSqsAsync(MessageBuilder.Create("benzene:not-served-here", Defaults.MessageAsObject).AsSqs());

        Assert.Single(response.BatchItemFailures);
    }

    // ---- one-shot: AWS Lambda SNS ----

    private static Task SendSns(bool ignoreUnmatched, string topic)
    {
        var host = new EntryPointMiddleApplicationBuilder<SNSEvent, SnsRecordContext>()
            .ConfigureServices(services => services
                .ConfigureServiceCollection()
                .UsingBenzene(x => x.AddSns()))
            .Configure(app =>
            {
                if (ignoreUnmatched)
                {
                    app.UseUnmatchedTopicAsIgnored();
                }

                app.UseMessageHandlers();
            })
            .Build(x => new SnsApplication(x));

        return host.SendAsync(MessageBuilder.Create(topic, Defaults.MessageAsObject).AsSns());
    }

    [Fact]
    public async Task Sns_Default_AnUnmatchedTopicFailsTheInvocation()
    {
        await Assert.ThrowsAsync<SnsMessageProcessingException>(() => SendSns(ignoreUnmatched: false, OtherServicesTopic));
    }

    [Fact]
    public async Task Sns_UnmatchedTopicAsIgnored_AnUnmatchedTopicIsAcknowledged()
    {
        await SendSns(ignoreUnmatched: true, OtherServicesTopic);
    }

    // ---- a stream: AWS Lambda MSK/Kafka ----

    private static Task<KafkaBatchResponse> SendKafka(bool ignoreUnmatched, string topic)
    {
        var services = ServiceResolverMother.CreateServiceCollection();
        services
            .AddTransient<ILogger<MessageRouter<KafkaContext>>>(_ => NullLogger<MessageRouter<KafkaContext>>.Instance)
            .UsingBenzene(x => x.AddKafka());

        var pipeline = new MiddlewarePipelineBuilder<KafkaContext>(new MicrosoftBenzeneServiceContainer(services));
        if (ignoreUnmatched)
        {
            pipeline.UseUnmatchedTopicAsIgnored();
        }

        pipeline.UseMessageHandlers();

        var application = new KafkaApplication(pipeline.Build());
        return application.HandleAsync(MessageBuilder.Create(topic, Defaults.MessageAsObject).AsAwsKafkaEvent(),
            new MicrosoftServiceResolverFactory(services));
    }

    [Fact]
    public async Task Kafka_Default_AnUnmatchedTopicStopsThePartition()
    {
        // The router answers an unmatched topic not-found - a failure result, not "no result" - so the
        // partition is redriven from it (the stream shape allows redeliver as its default).
        var response = await SendKafka(ignoreUnmatched: false, OtherServicesTopic);

        Assert.Single(response.BatchItemFailures);
    }

    [Fact]
    public async Task Kafka_UnmatchedTopicAsIgnored_AnUnmatchedTopicIsAcknowledged()
    {
        var response = await SendKafka(ignoreUnmatched: true, OtherServicesTopic);

        Assert.Empty(response.BatchItemFailures);
    }

    // ---- a self-hosted worker: the SQS consumer ----

    [Fact]
    public async Task SqsConsumer_UnmatchedTopicAsIgnored_TheMessageCountsAsSuccessful_SoItIsDeleted()
    {
        var services = ServiceResolverMother.CreateServiceCollection();
        services
            .AddTransient<ILogger<MessageRouter<SqsConsumerMessageContext>>>(_ => NullLogger<MessageRouter<SqsConsumerMessageContext>>.Instance)
            .UsingBenzene(x => x.AddSqsConsumer());

        var pipeline = new MiddlewarePipelineBuilder<SqsConsumerMessageContext>(new MicrosoftBenzeneServiceContainer(services));
        pipeline.UseUnmatchedTopicAsIgnored().UseMessageHandlers();

        var application = new SqsConsumerApplication(pipeline.Build());
        var message = MessageBuilder.Create(OtherServicesTopic, Defaults.MessageAsObject).AsSqsMessage();

        var result = await application.HandleAsync(new ReceiveMessageResponse { Messages = new List<Message> { message } },
            new MicrosoftServiceResolverFactory(services));

        Assert.Equal(message.MessageId, Assert.Single(result.SuccessfulMessages).MessageId);
        Assert.Empty(result.FailedMessages);
    }

    // ---- the middleware itself ----

    [Fact]
    public async Task Middleware_RecordsIgnored_ForAnUnmatchedTopic()
    {
        var services = ServiceResolverMother.CreateServiceCollection();
        services
            .AddTransient<ILogger<MessageRouter<SqsMessageContext>>>(_ => NullLogger<MessageRouter<SqsMessageContext>>.Instance)
            .UsingBenzene(x => x.AddSqs());

        Abstractions.Results.IBenzeneResult? recorded = null;
        var pipeline = new MiddlewarePipelineBuilder<SqsMessageContext>(new MicrosoftBenzeneServiceContainer(services));
        pipeline
            .OnResponse("capture", context => recorded = context.MessageResult)
            .UseUnmatchedTopicAsIgnored()
            .UseMessageHandlers();

        var application = new SqsApplication(pipeline.Build());
        await application.HandleAsync(MessageBuilder.Create(OtherServicesTopic, Defaults.MessageAsObject).AsSqs(),
            new MicrosoftServiceResolverFactory(services));

        Assert.NotNull(recorded);
        Assert.Equal(BenzeneResultStatus.Ignored, recorded!.Status);
        Assert.True(recorded.IsSuccessful);
    }
}
