using Benzene.Examples.App.Handlers;
using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace Benzene.Examples.Kafka.Test.Helpers;

public static class KafkaSetUp 
{
    private static KafkaSender _sender;

    /// <summary>
    /// The test broker: this project's docker-compose advertises its host listener on localhost:29092.
    /// KAFKA_BOOTSTRAP_SERVERS overrides it; <see cref="WorkerSetUp"/> hands the same value to the
    /// worker, so the producer and the consumer under test always talk to one broker.
    /// </summary>
    public static string BootstrapServers =>
        Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") is { Length: > 0 } configured
            ? configured
            : "localhost:29092";

    public static async Task SendAsync(string topic, object message)
    {
        await _sender.SendAsync(topic, message);
    }
    
    public static async Task SetUpAsync()
    {
        ProducerConfig producerConfig = new()
        {
            BootstrapServers = BootstrapServers,
            SaslMechanism = SaslMechanism.Plain,
            SecurityProtocol = SecurityProtocol.Plaintext,
        };

        await DeleteAllTopics(producerConfig);
        await CreateTopics(producerConfig, MessageTopicNames.OrderCreate, MessageTopicNames.OrderDelete, MessageTopicNames.OrderGet, MessageTopicNames.OrderGetAll, MessageTopicNames.OrderUpdate);
        
        _sender = new KafkaSender(producerConfig);
    }

    public static async Task TearDownAsync()
    {
        ProducerConfig producerConfig = new()
        {
            BootstrapServers = BootstrapServers,
            SaslMechanism = SaslMechanism.Plain,
            SecurityProtocol = SecurityProtocol.Plaintext,
        };

        await DeleteAllTopics(producerConfig);
        _sender.Dispose();
        _sender = null;
    }
    
    private static async Task DeleteAllTopics(ProducerConfig producerConfig)
    {
        try
        {
            var adminClient = new AdminClientBuilder(producerConfig).Build();

            var metadata = adminClient.GetMetadata(TimeSpan.FromSeconds(10));
            var topicNames = metadata.Topics.Select(a => a.Topic).ToArray();

            if (topicNames.Any())
            {
                await adminClient.DeleteTopicsAsync(topicNames);
            }
        }
        catch (CreateTopicsException createTopicsException)
        {
            Console.WriteLine(createTopicsException);
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception);
            throw;
        }
    }
    
    private static async Task CreateTopics(ProducerConfig producerConfig, params string[] topicNames)
    {
        try
        {
            var adminClient = new AdminClientBuilder(producerConfig).Build();
            var topicSpecifications = topicNames.Select(topicName => new TopicSpecification
            {
                Name = topicName
            }).ToArray();
            await adminClient.CreateTopicsAsync(topicSpecifications);
        }
        catch (CreateTopicsException createTopicsException)
        {
            Console.WriteLine(createTopicsException);
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception);
            throw;
        }
    }
}