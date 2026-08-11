using Amazon.Runtime;
using Amazon.SQS;
using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Aws.Sqs;
using Benzene.Aws.Sqs.Consumer;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Examples.K8sTransports.Domain;
using Benzene.Http;
using Benzene.Kafka.Core;
using Benzene.Microsoft.Dependencies;
using Benzene.SelfHost;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Examples.K8sTransports.App;

/// <summary>
/// The whole service in one startup: HTTP (Kestrel via <c>UseAspNet</c>), SQS, and Kafka wired as
/// three peer workers, all dispatching to the same <see cref="PlaceOrderMessageHandler"/>. ASP.NET
/// Core here is purely the HTTP host for Benzene - no controllers, no other ASP.NET middleware - so
/// it takes its place inside <c>UseWorker</c> exactly like the other transports, and
/// <c>Program.cs</c> stays the plain generic host. If this process ever grows real ASP.NET surface
/// (controllers, minimal APIs), switch the HTTP leg to the embedded mode instead:
/// <c>WebApplicationBuilder.UseBenzene&lt;HttpStartup&gt;()</c> with <c>app.UseHttp(...)</c>,
/// alongside <c>builder.Host.UseBenzene&lt;WorkerStartup&gt;()</c> for the workers - see
/// docs/getting-started-aspnet.md.
/// </summary>
public class Startup : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // One registration for all three transports: the handler (with its [Message] +
        // [HttpEndpoint] attributes) and the HTTP route table built from it.
        services.UsingBenzene(x => x
            .AddMessageHandlers(new[] { typeof(PlaceOrderMessageHandler) })
            .AddHttpMessageHandlers());
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

        // Three transports, three UseX calls, one worker host. UseAspNet listens on the port
        // Kubernetes gives the container (the readinessProbe and Service target this).
        app.UseWorker(worker => worker
            .UseAspNet(
                asp => asp.UseMessageHandlers(),
                options => options.Urls = $"http://0.0.0.0:{configuration["PORT"] ?? "8080"}")
            .UseSqs(sqsConfig, new SqsClientFactory(sqsClient), sqs => sqs.UseMessageHandlers())
            .UseKafka<Ignore, string>(kafkaConfig, kafka => kafka.UseMessageHandlers()));
    }
}
