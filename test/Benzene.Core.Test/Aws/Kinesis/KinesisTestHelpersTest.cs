using System.Collections.Generic;
using System.Threading.Tasks;
using Benzene.Aws.Lambda.Core.AwsEventStream;
using Benzene.Aws.Lambda.Kinesis;
using Benzene.Aws.Lambda.Kinesis.TestHelpers;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Aws.Helpers;
using Benzene.Test.Examples;
using Benzene.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Aws.Kinesis;

public class KinesisTestHelpersTest
{
    [Fact]
    public async Task AsKinesis_BuildsABatchThatRoutesThroughTheKinesisStreamPipeline()
    {
        var collected = new List<string>();
        var services = ServiceResolverMother.CreateServiceCollection();
        var app = new MiddlewarePipelineBuilder<AwsEventStreamContext>(new MicrosoftBenzeneServiceContainer(services));

        app.UseKinesisStream(kinesis => kinesis
            .UseStream<KinesisEventRecord>(async (records, _) =>
            {
                await foreach (var record in records)
                {
                    collected.Add(record.Kinesis.GetDataAsString());
                }
            })
        );

        var kinesisEvent = MessageBuilder.Create("unused-topic", Defaults.MessageAsObject).AsKinesis(numberOfRecords: 3);

        await app.Build().HandleAsync(AwsEventStreamContextBuilder.Build(kinesisEvent), new MicrosoftServiceResolverAdapter(services.BuildServiceProvider()));

        Assert.Equal(3, collected.Count);
        Assert.All(collected, json => Assert.Contains(Defaults.Name, json));
    }
}
