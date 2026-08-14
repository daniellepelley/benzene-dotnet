using Amazon.Runtime;
using Amazon.S3;
using Amazon.SQS;
using Amazon.SQS.Model;
using Benzene.Aws.Sqs;
using Benzene.Aws.Sqs.Consumer;
using Benzene.ClaimCheck;
using Benzene.ClaimCheck.Aws.S3;
using Benzene.Clients;
using Benzene.Clients.Aws.Sqs;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Middleware;
using Benzene.HostedService;
using Benzene.Integration.Test.Fixtures;
using Benzene.Integration.Test.Helpers;
using Benzene.Microsoft.Dependencies;
using Benzene.SelfHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Integration.Test.ClaimCheck;

// Proves the claim-check pattern end to end against real infrastructure: an outbound route with
// UseClaimCheck().UseSqs(...) offloads a payload well over the SQS/SNS/EventBridge 256 KB family
// limit to a real S3 bucket, carries the s3:// reference on the benzene-claim-check header, and the
// standalone (non-Lambda) Benzene.Aws.Sqs consumer pipeline's UseClaimCheck<SqsConsumerMessageContext>()
// hydrates it back to the full body before the handler ever sees it. See
// work/claim-check-plan.md Phase 4.
[Collection(DockerEmulatorCollection.Name)]
public class ClaimCheckS3IntegrationTest
{
    private const string ServiceUrl = "http://localhost:4566";
    private const string QueueName = "claim-check-test-queue";
    private const string Bucket = "claim-check-test-bucket";
    private const string Topic = "orders:claim-check";

    [Fact]
    public async Task OffloadOverSqs_ThenHydrateOnConsume_DeliversTheFullOversizedPayloadThroughS3()
    {
        var queueUrl = $"{ServiceUrl}/000000000000/{QueueName}";
        var received = new List<string>();

        var sqsClient = new AmazonSQSClient(
            new AnonymousAWSCredentials(),
            new AmazonSQSConfig { ServiceURL = ServiceUrl });
        var s3Client = new AmazonS3Client(
            new AnonymousAWSCredentials(),
            new AmazonS3Config { ServiceURL = ServiceUrl, ForcePathStyle = true });

        // LocalStack takes a few seconds to become ready after the container starts - retry rather
        // than a fixed sleep (same pattern as SqsConsumerPipelineTest).
        await CreateQueueWithRetryAsync(sqsClient);
        await CreateBucketWithRetryAsync(s3Client);

        var sqsClientFactory = new SqsClientFactory(sqsClient);

        // The consumer side: its own independent DI container (InlineSelfHostedStartUp always builds
        // one - see its own remarks), so it needs its own IAmazonS3 + store registration, exactly
        // like the sender side below needs its own.
        var inlineSelfHostedStartUp = new InlineSelfHostedStartUp()
            .ConfigureServices(services => services
                .AddSingleton<IAmazonS3>(s3Client)
                .UsingBenzene(b => b
                    .AddBenzene()
                    .AddS3ClaimCheckStore(Bucket)))
            .Configure(worker => worker.UseSqs(
                new SqsConsumerConfig
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 10
                },
                sqsClientFactory,
                pipeline => pipeline
                    .UseClaimCheck<SqsConsumerMessageContext>()
                    .OnRequest(r => received.Add(r.Message.Body))));

        var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IAmazonS3>(s3Client);
                services.UsingBenzene(b => b
                    .AddBenzene()
                    .AddS3ClaimCheckStore(Bucket)
                    .AddOutboundRouting(routing => routing
                        .Route(Topic, route => route
                            .UseClaimCheck()
                            .UseSqs(queueUrl, healthCheck: false))));
                services.AddHostedService(x => inlineSelfHostedStartUp.BuildHostedService());
            })
            .Build();

        var sender = host.Services.GetRequiredService<IBenzeneMessageSender>();

        // Comfortably over every transport in the 256 KB family (SQS/SNS/EventBridge) and over the
        // package's own 192 KiB default threshold, so the offload genuinely triggers.
        var oversizedPayload = new string('x', 300 * 1024);
        await sender.SendAsync<OversizedOrder, Void>(Topic, new OversizedOrder { Payload = oversizedPayload });

        await host.StartAsync();
        await Task.Delay(2000);
        await host.StopAsync();

        var body = Assert.Single(received);
        Assert.Contains(oversizedPayload, body);
        // The wire body actually round-tripped through S3 - not the tiny placeholder the offload
        // middleware sends over SQS itself.
        Assert.DoesNotContain("_benzeneClaimCheck", body);
    }

    private static async Task CreateQueueWithRetryAsync(AmazonSQSClient sqsClient)
    {
        var deadline = DateTime.UtcNow.AddSeconds(180);
        Exception? lastException = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await sqsClient.CreateQueueAsync(new CreateQueueRequest(QueueName));
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        throw new TimeoutException("Timed out waiting for the LocalStack SQS emulator to become ready.", lastException);
    }

    private static async Task CreateBucketWithRetryAsync(AmazonS3Client s3Client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(180);
        Exception? lastException = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await s3Client.PutBucketAsync(Bucket);
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        throw new TimeoutException("Timed out waiting for the LocalStack S3 emulator to become ready.", lastException);
    }
}

public class OversizedOrder
{
    public string Payload { get; set; } = "";
}
