using Benzene.Abstractions.Hosting;
using Benzene.Core.MessageHandlers;
using Benzene.Examples.App.Handlers;
using Benzene.FluentValidation;
using Benzene.Kafka.Core;
using Benzene.Microsoft.Dependencies;
using Benzene.SelfHost;
using Confluent.Kafka;

namespace Benzene.Examples.Kafka;

public class StartUp : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
    {
        return DependenciesBuilder.GetConfiguration();
    }

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        DependenciesBuilder.Register(services, configuration);
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
        var benzeneKafkaConfig = new BenzeneKafkaConfig
        {
            ConsumerConfig = new ConsumerConfig
            {
                // The broker address, from configuration (KAFKA_BOOTSTRAP_SERVERS in the environment),
                // defaulting to this example's own docker-compose broker.
                BootstrapServers = configuration["KAFKA_BOOTSTRAP_SERVERS"] ?? "localhost:9092",
                SaslMechanism = SaslMechanism.Plain,
                SecurityProtocol = SecurityProtocol.Plaintext,
                GroupId = Guid.NewGuid().ToString(),
                AutoOffsetReset = AutoOffsetReset.Earliest
            },
            // The commands that make sense fire-and-forget over Kafka; get/getall are
            // request/response and have no reply channel on a Kafka topic.
            Topics = new[] { MessageTopicNames.OrderCreate, MessageTopicNames.OrderUpdate, MessageTopicNames.OrderDelete }
        };

        app.UseWorker(worker => worker
            .UseKafka<Ignore, string>(benzeneKafkaConfig, x =>
                x.UseMessageHandlers(handlers => handlers.UseFluentValidation())));
    }
}
