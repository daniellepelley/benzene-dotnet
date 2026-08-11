using Amazon.Runtime;
using Amazon.SQS;
using Benzene.Abstractions.Hosting;
using Benzene.Aws.Sqs;
using Benzene.Aws.Sqs.Consumer;
using Benzene.Core.MessageHandlers;
using Benzene.Examples.K8sTransports.Domain;
using Benzene.Microsoft.Dependencies;
using Benzene.SelfHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Examples.K8sTransports.SqsWorker;

/// <summary>
/// The SQS leg: a long-running worker pod polling one queue, dispatching every message to the same
/// <see cref="PlaceOrderMessageHandler"/> the Api/ pod exposes over HTTP. No application code here
/// references the handler directly - <c>UseMessageHandlers()</c> below finds it by reflection because
/// this project references Domain/, exactly the same discovery Api/ and KafkaWorker/ each do
/// independently.
/// </summary>
public class Startup : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // UseSqs (below) wires the SQS mappers and UseMessageHandlers() discovers PlaceOrderMessageHandler
        // - nothing Benzene-specific to register here.
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
        var config = new SqsConsumerConfig
        {
            QueueUrl = configuration["QUEUE_URL"]
                ?? throw new InvalidOperationException("QUEUE_URL is not set"),
            MaxNumberOfMessages = 10,
        };

        // A LocalStack/emulator endpoint is opt-in via SQS_SERVICE_URL (see compose/docker-compose.yml);
        // unset (the real-AWS/EKS case), AmazonSQSClient() falls through to the SDK's default credential
        // chain - an IRSA-mapped pod service account on EKS, same as every other Benzene AWS client. This
        // worker never prescribes how you authenticate, same principle as the Service Bus/Event Hub
        // workers in docs/getting-started-worker.md.
        var localEndpoint = configuration["SQS_SERVICE_URL"];
        var sqsClient = string.IsNullOrEmpty(localEndpoint)
            ? new AmazonSQSClient()
            : new AmazonSQSClient(new BasicAWSCredentials("local", "local"),
                new AmazonSQSConfig { ServiceURL = localEndpoint });

        app.UseWorker(worker => worker.UseSqs(
            config,
            new SqsClientFactory(sqsClient),
            sqs => sqs.UseMessageHandlers()));
    }
}
