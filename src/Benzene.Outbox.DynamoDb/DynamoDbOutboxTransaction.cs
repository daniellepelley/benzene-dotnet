using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Benzene.Outbox.DynamoDb;

/// <summary>
/// The default <see cref="IDynamoDbOutboxTransaction"/>: drains the scoped <see cref="BufferedOutboxStage"/>
/// and appends one <c>Put</c> <see cref="TransactWriteItem"/> per staged envelope to the caller's own
/// application items, then issues exactly one <c>TransactWriteItemsAsync</c> call. Registered scoped
/// (one instance per message, sharing that message's <see cref="BufferedOutboxStage"/>) via
/// <see cref="Extensions.AddDynamoDbOutboxTransaction"/>.
/// </summary>
public class DynamoDbOutboxTransaction : IDynamoDbOutboxTransaction
{
    // TransactWriteItems is atomic but bounded; a commit larger than this must be split by the caller.
    private const int MaxTransactionItems = 100;

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly BufferedOutboxStage _stage;
    private readonly string _tableName;
    private readonly string _partitionKeyAttribute;

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamoDbOutboxTransaction"/> class.
    /// </summary>
    /// <param name="dynamoDb">The DynamoDB client.</param>
    /// <param name="stage">The scope's staging buffer, drained on <see cref="CommitAsync"/>.</param>
    /// <param name="tableName">The outbox table staged envelopes are written to (the same table <see cref="DynamoDbOutboxStore"/> reads from).</param>
    /// <param name="partitionKeyAttribute">The table's string partition-key attribute name. Defaults to <c>id</c> - MUST match the value given to <see cref="DynamoDbOutboxStore"/>.</param>
    public DynamoDbOutboxTransaction(
        IAmazonDynamoDB dynamoDb,
        BufferedOutboxStage stage,
        string tableName,
        string partitionKeyAttribute = "id")
    {
        _dynamoDb = dynamoDb;
        _stage = stage;
        _tableName = tableName;
        _partitionKeyAttribute = partitionKeyAttribute;
    }

    /// <inheritdoc />
    public async Task CommitAsync(IReadOnlyList<TransactWriteItem> applicationItems, CancellationToken cancellationToken = default)
    {
        applicationItems ??= Array.Empty<TransactWriteItem>();

        // Validate against the staged COUNT before draining - DrainStaged() is destructive (it clears
        // the buffer with no way to put envelopes back), so a commit this method is about to refuse
        // must not first destroy the very envelopes the caller could otherwise still retry staging
        // with (e.g. after splitting the write). Only once we know the commit will actually proceed do
        // we drain and consume the buffer.
        var stagedCount = _stage.StagedCount;

        if (applicationItems.Count == 0 && stagedCount == 0)
        {
            throw new InvalidOperationException(
                "Nothing to commit: no outbox envelopes were staged (via UseOutbox in Transactional mode) " +
                "and no application transact items were supplied.");
        }

        var total = applicationItems.Count + stagedCount;
        if (total > MaxTransactionItems)
        {
            throw new InvalidOperationException(
                $"Cannot commit {total} item(s) in one DynamoDB transaction " +
                $"({applicationItems.Count} application item(s) + {stagedCount} staged outbox envelope(s)); " +
                $"DynamoDB transactions are limited to {MaxTransactionItems} items. Split the write.");
        }

        // Non-destructive peek, not DrainStaged() - the transact-item list must be built from
        // envelopes still in the buffer, so that if TransactWriteItemsAsync itself throws (throttling,
        // a conditional-check failure, ...) the staged envelopes are still there for a caller's retry.
        // Only DrainStaged() - after the call below has actually succeeded - is allowed to consume them.
        var staged = _stage.Peek();
        var transactItems = new List<TransactWriteItem>(total);
        transactItems.AddRange(applicationItems);
        foreach (var envelope in staged)
        {
            transactItems.Add(new TransactWriteItem
            {
                Put = new Put
                {
                    TableName = _tableName,
                    Item = DynamoDbOutboxItemMapper.ToItem(envelope, _partitionKeyAttribute)
                }
            });
        }

        await _dynamoDb.TransactWriteItemsAsync(
            new TransactWriteItemsRequest { TransactItems = transactItems }, cancellationToken);

        // Only consume the buffer once the write above has actually succeeded.
        _stage.DrainStaged();
    }
}
