using Amazon.DynamoDBv2;
using Benzene.Abstractions.DI;

namespace Benzene.Outbox.DynamoDb;

/// <summary>
/// Registration helpers for the DynamoDB-backed <see cref="IOutboxStore"/> and the DynamoDB unit of
/// work for <see cref="OutboxWriteMode.Transactional"/> capture.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Registers a <see cref="DynamoDbOutboxStore"/> as the <see cref="IOutboxStore"/>, resolving
    /// <c>IAmazonDynamoDB</c> from DI (the consumer registers the client and provisions the table -
    /// see <see cref="DynamoDbOutboxStore"/>'s remarks for the table shape). Pair with
    /// <c>Benzene.Outbox</c>'s <c>AddOutbox(...)</c> wherever an outbound route in this process calls
    /// <c>UseOutbox()</c>, and/or with <c>AddOutboxDispatcherWorker()</c>/a DynamoDB-Streams relay
    /// handler for dispatch.
    /// </summary>
    /// <param name="services">The Benzene service container.</param>
    /// <param name="tableName">The DynamoDB table that stores outbox envelopes.</param>
    /// <param name="retentionPeriod">How long a dispatched envelope lives before native TTL removes it. Defaults to 7 days.</param>
    /// <param name="partitionKeyAttribute">The table's string partition-key attribute name. Defaults to <c>id</c>.</param>
    /// <param name="pendingIndexName">The sparse GSI's name (see <see cref="DynamoDbOutboxStore"/>'s remarks). Defaults to <c>pending-index</c>.</param>
    /// <returns>The service container, for chaining.</returns>
    public static IBenzeneServiceContainer AddDynamoDbOutboxStore(
        this IBenzeneServiceContainer services,
        string tableName,
        TimeSpan? retentionPeriod = null,
        string partitionKeyAttribute = "id",
        string pendingIndexName = "pending-index")
    {
        services.TryAddSingleton<IOutboxStore>(resolver =>
            new DynamoDbOutboxStore(
                resolver.GetService<IAmazonDynamoDB>(),
                tableName,
                retentionPeriod,
                partitionKeyAttribute,
                pendingIndexName));
        return services;
    }

    /// <summary>
    /// Registers a scoped <see cref="DynamoDbOutboxTransaction"/> as the <see cref="IDynamoDbOutboxTransaction"/> -
    /// the outbox-aware unit of work a handler commits through when a route uses
    /// <c>UseOutbox(o =&gt; o.WriteMode = OutboxWriteMode.Transactional)</c>. Requires
    /// <c>Benzene.Outbox</c>'s <c>AddOutbox(...)</c> (for the scoped <c>BufferedOutboxStage</c> this
    /// drains) to already be registered.
    /// </summary>
    /// <param name="services">The Benzene service container.</param>
    /// <param name="tableName">The outbox table staged envelopes are written to (the same table given to <see cref="AddDynamoDbOutboxStore"/>).</param>
    /// <param name="partitionKeyAttribute">The table's string partition-key attribute name. Defaults to <c>id</c> - MUST match <see cref="AddDynamoDbOutboxStore"/>'s value.</param>
    /// <returns>The service container, for chaining.</returns>
    public static IBenzeneServiceContainer AddDynamoDbOutboxTransaction(
        this IBenzeneServiceContainer services,
        string tableName,
        string partitionKeyAttribute = "id")
    {
        services.TryAddScoped<IDynamoDbOutboxTransaction>(resolver =>
            new DynamoDbOutboxTransaction(
                resolver.GetService<IAmazonDynamoDB>(),
                resolver.GetService<BufferedOutboxStage>(),
                tableName,
                partitionKeyAttribute));
        return services;
    }
}
