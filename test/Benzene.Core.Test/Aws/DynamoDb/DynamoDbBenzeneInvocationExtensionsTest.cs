using System.Threading.Tasks;
using Benzene.Abstractions.Hosting;
using Benzene.Aws.Lambda.Core.AwsEventStream;
using Benzene.Aws.Lambda.DynamoDb;
using Benzene.Aws.Lambda.DynamoDb.TestHelpers;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Aws.Helpers;
using Benzene.Test.Examples;
using Benzene.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Aws.DynamoDb;

public class DynamoDbBenzeneInvocationExtensionsTest
{
    [Fact]
    public async Task UseDynamoDb_RealEntryPoint_ResolvesIBenzeneInvocationInsideTheRecordPipeline()
    {
        // Coverage gap closed: the test below exercises UseBenzeneInvocation() directly on a bare
        // DynamoDbRecordContext builder, never going through the real UseDynamoDb(...) entry point an
        // application actually calls (which wires it up via CreateMiddlewarePipeline + app.Register(...)
        // against the OUTER AwsEventStreamContext-level container). This test goes through that real
        // entry point - AwsEventStreamContext -> DynamoDbLambdaHandler -> DynamoDbApplication's
        // per-record scope -> the record pipeline - to prove the wiring actually holds end-to-end.
        IBenzeneInvocation resolved = null;

        var services = new ServiceCollection();
        var app = new MiddlewarePipelineBuilder<AwsEventStreamContext>(new MicrosoftBenzeneServiceContainer(services));
        app.UseDynamoDb(dynamoDb => dynamoDb
            .Use(null, (resolver, _, next) =>
            {
                resolved = resolver.GetService<IBenzeneInvocation>();
                return next();
            })
        );

        var request = MessageBuilder.Create("example-orders:INSERT", Defaults.MessageAsObject).AsDynamoDb();

        using var factory = new MicrosoftServiceResolverFactory(services);
        using var resolver = factory.CreateScope();
        await app.Build().HandleAsync(AwsEventStreamContextBuilder.Build(request), resolver);

        Assert.NotNull(resolved);
        Assert.False(string.IsNullOrEmpty(resolved.InvocationId));
        Assert.Equal("AwsLambda", resolved.Platform);
    }

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
