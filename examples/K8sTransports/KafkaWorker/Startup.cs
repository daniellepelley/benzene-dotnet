using Benzene.Abstractions.Hosting;
using Benzene.Core.MessageHandlers;
using Benzene.Examples.K8sTransports.Domain;
using Benzene.Kafka.Core;
using Benzene.Microsoft.Dependencies;
using Benzene.SelfHost;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Examples.K8sTransports.KafkaWorker;

/// <summary>
/// The Kafka leg: a long-running worker pod consuming one topic, dispatching every record to the
/// same <see cref="PlaceOrderMessageHandler"/> the Api/ pod exposes over HTTP and SqsWorker/ exposes
/// over SQS. The Kafka consumer routes by the record's <b>literal</b> Kafka topic name against a
/// handler's <c>[Message(...)]</c> value - no colon-separated topic-id convention the way HTTP/SQS
/// have one - which is why the shared handler is registered under the Kafka-legal
/// <c>"order-place"</c> rather than a colon-style topic (Kafka topic names may not contain ':').
/// </summary>
public class Startup : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // UseKafka (below) wires the Kafka mappers and UseMessageHandlers() discovers
        // PlaceOrderMessageHandler - nothing Benzene-specific to register here.
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
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

        app.UseWorker(worker =>
            worker.UseKafka<Ignore, string>(kafkaConfig, kafka => kafka.UseMessageHandlers()));
    }
}
