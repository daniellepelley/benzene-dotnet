using Amazon.DynamoDBv2;
using Benzene.Abstractions.DI;

namespace Benzene.EventSourcing.DynamoDb;

/// <summary>
/// Registration helpers for the DynamoDB-backed <see cref="IEventStore"/>.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Registers a <see cref="DynamoDbEventStore"/> as the <see cref="IEventStore"/>, resolving
    /// <c>IAmazonDynamoDB</c> from DI (the consumer registers the client and provisions the table).
    /// </summary>
    /// <param name="services">The Benzene service container.</param>
    /// <param name="tableName">The event table (composite key: string partition + numeric sort).</param>
    /// <param name="partitionKeyAttribute">The stream-id partition-key attribute. Defaults to <c>pk</c>.</param>
    /// <param name="sortKeyAttribute">The version sort-key attribute. Defaults to <c>version</c>.</param>
    /// <returns>The service container, for chaining.</returns>
    public static IBenzeneServiceContainer AddDynamoDbEventStore(
        this IBenzeneServiceContainer services,
        string tableName,
        string partitionKeyAttribute = "pk",
        string sortKeyAttribute = "version")
    {
        services.AddSingleton<IEventStore>(resolver =>
            new DynamoDbEventStore(
                resolver.GetService<IAmazonDynamoDB>(), tableName, partitionKeyAttribute, sortKeyAttribute));
        return services;
    }
}
