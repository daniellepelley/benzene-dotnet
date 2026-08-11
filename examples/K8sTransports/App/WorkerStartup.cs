using Amazon.Runtime;
using Amazon.SQS;
using Benzene.Abstractions.Hosting;
using Benzene.Aws.Sqs;
using Benzene.Aws.Sqs.Consumer;
using Benzene.Core.MessageHandlers;
using Benzene.Examples.K8sTransports.Domain;
using Benzene.Kafka.Core;
using Benzene.Microsoft.Dependencies;
using Benzene.SelfHost;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Examples.K8sTransports.App;

/// <summary>
/// The SQS and Kafka legs, together: both dispatch to the same <see cref="PlaceOrderMessageHandler"/>
/// the HTTP leg exposes over Kestrel. Wired via <c>IHostBuilder.UseBenzene&lt;WorkerStartup&gt;()</c>
/// (through <c>builder.Host</c>) in <c>Program.cs</c>, which hands <see cref="Configure"/> a
/// <c>WorkerApplicationBuilder</c> - calling <c>app.UseHttp(...)</c> here would silently no-op, the
/// mirror image of <see cref="HttpStartup"/>'s comment. <c>worker.UseSqs(...).UseKafka(...)</c> below
/// chains - each call registers its own worker with the same <c>IBenzeneWorkerStartup</c>, and
/// <c>Benzene.HostedService</c> wraps the two of them as ONE <c>IHostedService</c>
/// (a <c>CompositeBenzeneWorker</c>) that starts/stops together with Kestrel, in this one process.
/// </summary>
public class WorkerStartup : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // UseSqs/UseKafka (below) wire their own mappers and UseMessageHandlers() discovers
        // PlaceOrderMessageHandler - nothing Benzene-specific to register here.
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
        var sqsConfig = new SqsConsumerConfig
        {
            QueueUrl = configuration["QUEUE_URL"]
                ?? throw new InvalidOperationException("QUEUE_URL is not set"),
            MaxNumberOfMessages = 10,
        };

        // A LocalStack/emulator endpoint is opt-in via SQS_SERVICE_URL (see compose/docker-compose.yml);
        // unset (the real-AWS/EKS case), AmazonSQSClient() falls through to the SDK's default credential
        // chain - an IRSA-mapped pod service account on EKS, same as every other Benzene AWS client.
        var localEndpoint = configuration["SQS_SERVICE_URL"];
        var sqsClient = string.IsNullOrEmpty(localEndpoint)
            ? new AmazonSQSClient()
            : new AmazonSQSClient(new BasicAWSCredentials("local", "local"),
                new AmazonSQSConfig { ServiceURL = localEndpoint });

        var kafkaConfig = new BenzeneKafkaConfig
        {
            ConsumerConfig = new ConsumerConfig
            {
                BootstrapServers = configuration["KAFKA_BOOTSTRAP_SERVERS"]
                    ?? throw new InvalidOperationException("KAFKA_BOOTSTRAP_SERVERS is not set"),
                SecurityProtocol = SecurityProtocol.Plaintext,
                GroupId = "orders-kafka-worker",
                AutoOffsetReset = AutoOffsetReset.Earliest,
            },
            Topics = new[] { "order-place" },
        };

        app.UseWorker(worker => worker
            .UseSqs(sqsConfig, new SqsClientFactory(sqsClient), sqs => sqs.UseMessageHandlers())
            .UseKafka<Ignore, string>(kafkaConfig, kafka => kafka.UseMessageHandlers()));
    }
}
