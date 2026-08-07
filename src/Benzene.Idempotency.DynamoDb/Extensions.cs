using Amazon.DynamoDBv2;
using Benzene.Abstractions.DI;

namespace Benzene.Idempotency.DynamoDb;

/// <summary>
/// Registration helpers for the DynamoDB-backed <see cref="IIdempotencyStore"/>.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Registers a <see cref="DynamoDbIdempotencyStore"/> as the <see cref="IIdempotencyStore"/>,
    /// resolving <c>IAmazonDynamoDB</c> from DI (the consumer registers the client). Pair with
    /// <c>UseIdempotency()</c> on the pipeline for cross-instance de-duplication of at-least-once
    /// deliveries.
    /// </summary>
    /// <param name="services">The Benzene service container.</param>
    /// <param name="tableName">The DynamoDB table that stores idempotency records (string partition key; enable TTL on <c>expiresAt</c>).</param>
    /// <param name="timeToLive">How long a record lives before a key can be reclaimed. Defaults to 24 hours; must exceed the transport's maximum redelivery window.</param>
    /// <param name="partitionKeyAttribute">The table's string partition-key attribute name. Defaults to <c>pk</c>.</param>
    /// <returns>The service container, for chaining.</returns>
    public static IBenzeneServiceContainer AddDynamoDbIdempotencyStore(
        this IBenzeneServiceContainer services,
        string tableName,
        TimeSpan? timeToLive = null,
        string partitionKeyAttribute = "pk")
    {
        services.AddSingleton<IIdempotencyStore>(resolver =>
            new DynamoDbIdempotencyStore(
                resolver.GetService<IAmazonDynamoDB>(), tableName, timeToLive, partitionKeyAttribute));
        return services;
    }
}
