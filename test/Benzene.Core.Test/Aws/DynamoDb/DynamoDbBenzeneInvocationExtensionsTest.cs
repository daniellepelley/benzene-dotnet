using System.Threading.Tasks;
using Benzene.Abstractions.Hosting;
using Benzene.Aws.Lambda.DynamoDb;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Aws.DynamoDb;

public class DynamoDbBenzeneInvocationExtensionsTest
{
    [Fact]
    public async Task UseBenzeneInvocation_SetsInvocationIdToRecordEventId()
    {
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);

        var builder = new MiddlewarePipelineBuilder<DynamoDbRecordContext>(container);
        builder.UseBenzeneInvocation();
        builder.Use((_, next) => next());

        var pipeline = builder.Build();
        using var factory = new MicrosoftServiceResolverFactory(services);
        using var resolver = factory.CreateScope();

        var dynamoDbEvent = new DynamoDbEvent
        {
            Records = new System.Collections.Generic.List<DynamoDbStreamRecord>
            {
                new() { EventId = "ddb-evt-789", EventSource = "aws:dynamodb" }
            }
        };
        var context = DynamoDbRecordContext.CreateInstance(dynamoDbEvent, dynamoDbEvent.Records[0]);

        await pipeline.HandleAsync(context, resolver);
        var resolved = resolver.GetService<IBenzeneInvocation>();

        Assert.Equal("ddb-evt-789", resolved.InvocationId);
        Assert.Equal("AwsLambda", resolved.Platform);
    }
}
