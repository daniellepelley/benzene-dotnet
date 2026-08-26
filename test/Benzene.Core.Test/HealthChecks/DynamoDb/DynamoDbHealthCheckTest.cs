using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Benzene.HealthChecks;
using Benzene.HealthChecks.Core;
using Benzene.HealthChecks.DynamoDb;
using Moq;
using Xunit;

namespace Benzene.Test.HealthChecks.DynamoDb;

public class DynamoDbHealthCheckTest
{
    [Fact]
    public async Task ExecuteAsync_TableActive_ReturnsHealthy()
    {
        var mock = new Mock<IAmazonDynamoDB>();
        mock.Setup(x => x.DescribeTableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DescribeTableResponse
            {
                HttpStatusCode = HttpStatusCode.OK,
                Table = new TableDescription { TableStatus = TableStatus.ACTIVE }
            });

        var result = await new DynamoDbHealthCheck("orders", mock.Object).ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Ok, result.Status);
        Assert.Equal("DynamoDb", result.Type);
        var dependency = Assert.Single(result.Dependencies);
        Assert.Equal("Table", dependency.Kind);
        Assert.Equal("orders", dependency.Name);
    }

    [Fact]
    public async Task ExecuteAsync_ClientThrows_ReturnsUnhealthy_WithTheTableDependency()
    {
        var mock = new Mock<IAmazonDynamoDB>();
        mock.Setup(x => x.DescribeTableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ResourceNotFoundException("no such table"));

        var result = await new DynamoDbHealthCheck("orders", mock.Object).ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Failed, result.Status);
        Assert.Equal("ResourceNotFoundException", result.Data["Error"]);
        Assert.Equal("orders", Assert.Single(result.Dependencies).Name);
    }

    // WP-K (#50): DynamoDbHealthCheck doesn't route through HealthCheckError.Classify at all (it builds
    // its Failed result directly), so the shared Classify fix can't reach it - before this fix, its own
    // catch (Exception ex) caught the OperationCanceledException the processor's timeout produces along
    // with every genuine SDK failure, misclassifying a timeout/shutdown as an ordinary transient
    // dependency failure ({"Error": "TaskCanceledException"}), indistinguishable from a real dead table.
    // It must instead propagate so ExceptionHandlingHealthCheck (which every check runs under via
    // HealthCheckProcessor) reports the distinct "Cancelled" outcome, the same way TcpHealthCheck's own
    // catch/rethrow already does.
    [Fact]
    public async Task ProcessorTimeout_OnAHungDescribeTable_ClassifiesAsCancelled_NotAnOrdinaryDependencyFailure()
    {
        var mock = new Mock<IAmazonDynamoDB>();
        mock.Setup(x => x.DescribeTableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                // Never completes on its own - only the processor's (much shorter) timeout ends this await.
                await Task.Delay(Timeout.Infinite, ct);
                return new DescribeTableResponse { HttpStatusCode = HttpStatusCode.OK };
            });

        var healthCheck = new DynamoDbHealthCheck("orders", mock.Object);
        var processor = new HealthCheckProcessor(TimeSpan.FromMilliseconds(50));

        var result = await processor.PerformHealthChecksAsync(new IHealthCheck[] { healthCheck });

        var response = result.PayloadAsObject as HealthCheckResponse;
        Assert.NotNull(response);
        var check = response!.HealthChecks["DynamoDb"];
        Assert.Equal(HealthCheckStatus.Failed, check.Status);
        // Not "TaskCanceledException" (the pre-fix misclassification) - the distinct "Cancelled" outcome.
        Assert.Equal("Cancelled", check.Data["Error"]);
    }
}
