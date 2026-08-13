using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Benzene.Outbox;
using Benzene.Outbox.DynamoDb;
using Moq;
using Xunit;

namespace Benzene.Test.Outbox.DynamoDb;

public class DynamoDbOutboxTransactionTest
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Mock<IAmazonDynamoDB> MockDynamo() => new(MockBehavior.Strict);

    private static OutboxEnvelope NewEnvelope(string id)
        => new(id, "payments:capture", "\"p\"", typeof(string).AssemblyQualifiedName!, new Dictionary<string, string>(), Now);

    private static TransactWriteItem AppItem(string id)
        => new()
        {
            Put = new Put
            {
                TableName = "orders",
                Item = new Dictionary<string, AttributeValue> { ["id"] = new AttributeValue { S = id } }
            }
        };

    [Fact]
    public async Task Commit_CombinesApplicationItemsAndStagedEnvelopes_IntoOneTransactWriteItemsCall()
    {
        var dynamo = MockDynamo();
        TransactWriteItemsRequest? captured = null;
        dynamo.Setup(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<TransactWriteItemsRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new TransactWriteItemsResponse());

        var stage = new BufferedOutboxStage();
        await stage.StageAsync(NewEnvelope("env-1"));
        await stage.StageAsync(NewEnvelope("env-2"));

        var transaction = new DynamoDbOutboxTransaction(dynamo.Object, stage, "outbox");

        await transaction.CommitAsync([AppItem("order-1")]);

        Assert.Equal(3, captured!.TransactItems.Count);
        Assert.Equal("orders", captured.TransactItems[0].Put.TableName);
        Assert.Equal("outbox", captured.TransactItems[1].Put.TableName);
        Assert.Equal("env-1", captured.TransactItems[1].Put.Item["id"].S);
        Assert.Equal("outbox", captured.TransactItems[2].Put.TableName);
        Assert.Equal("env-2", captured.TransactItems[2].Put.Item["id"].S);
    }

    [Fact]
    public async Task Commit_DrainsTheStage_SoASecondCommitOnlySeesNewlyStagedEnvelopes()
    {
        var dynamo = MockDynamo();
        dynamo.Setup(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactWriteItemsResponse());

        var stage = new BufferedOutboxStage();
        await stage.StageAsync(NewEnvelope("env-1"));
        var transaction = new DynamoDbOutboxTransaction(dynamo.Object, stage, "outbox");
        await transaction.CommitAsync([AppItem("order-1")]);

        TransactWriteItemsRequest? second = null;
        dynamo.Setup(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<TransactWriteItemsRequest, CancellationToken>((r, _) => second = r)
            .ReturnsAsync(new TransactWriteItemsResponse());
        await transaction.CommitAsync([AppItem("order-2")]);

        Assert.Single(second!.TransactItems);
    }

    [Fact]
    public async Task Commit_MoreThanOneHundredItems_ThrowsAndNeverCallsDynamo()
    {
        var dynamo = MockDynamo();
        var stage = new BufferedOutboxStage();
        await stage.StageAsync(NewEnvelope("env-1"));
        var transaction = new DynamoDbOutboxTransaction(dynamo.Object, stage, "outbox");
        var appItems = Enumerable.Range(0, 100).Select(i => AppItem($"order-{i}")).ToList();

        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync(appItems));
        dynamo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Commit_NothingStagedAndNoApplicationItems_Throws()
    {
        var dynamo = MockDynamo();
        var stage = new BufferedOutboxStage();
        var transaction = new DynamoDbOutboxTransaction(dynamo.Object, stage, "outbox");

        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync(Array.Empty<TransactWriteItem>()));
        dynamo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Commit_OnlyStagedEnvelopes_NoApplicationItems_Succeeds()
    {
        var dynamo = MockDynamo();
        TransactWriteItemsRequest? captured = null;
        dynamo.Setup(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<TransactWriteItemsRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new TransactWriteItemsResponse());

        var stage = new BufferedOutboxStage();
        await stage.StageAsync(NewEnvelope("env-1"));
        var transaction = new DynamoDbOutboxTransaction(dynamo.Object, stage, "outbox");

        await transaction.CommitAsync(Array.Empty<TransactWriteItem>());

        Assert.Single(captured!.TransactItems);
    }
}
