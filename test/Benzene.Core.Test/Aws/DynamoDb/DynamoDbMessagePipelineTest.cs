using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Aws.Lambda.DynamoDb;
using Benzene.Aws.Lambda.DynamoDb.TestHelpers;
using Benzene.Core.MessageHandlers;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Benzene.Test.Aws.Helpers;
using Benzene.Test.Examples;
using Benzene.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Benzene.Test.Aws.DynamoDb;

[Message("example-orders:INSERT")]
public class ExampleOrderInsertedHandler : IMessageHandler<ExampleRequestPayload, Void>
{
    public Task<IBenzeneResult<Void>> HandleAsync(ExampleRequestPayload request)
    {
        return Task.FromResult(BenzeneResult.Ok(new Void()));
    }
}

public class DynamoDbMessagePipelineTest
{
    private static (DynamoDbApplication Application, MicrosoftServiceResolverFactory Factory) BuildApplication(
        System.Action<DynamoDbRecordContext> onResponse)
    {
        var services = ServiceResolverMother.CreateServiceCollection();
        services
            .AddTransient<ILogger<MessageRouter<DynamoDbRecordContext>>>(_ => NullLogger<MessageRouter<DynamoDbRecordContext>>.Instance)
            .AddTransient<ILogger>(_ => NullLogger.Instance)
            .UsingBenzene(x => x.AddDynamoDb());

        var pipeline = new MiddlewarePipelineBuilder<DynamoDbRecordContext>(new MicrosoftBenzeneServiceContainer(services));
        pipeline
            .OnResponse("Check Response", onResponse)
            .UseMessageHandlers();

        return (new DynamoDbApplication(pipeline.Build()), new MicrosoftServiceResolverFactory(services));
    }

    [Fact]
    public async Task Send()
    {
        bool? isSuccessful = null;
        var (application, factory) = BuildApplication(context => isSuccessful = context.MessageResult?.IsSuccessful);

        var request = MessageBuilder
            .Create("example-orders:INSERT", new ExampleRequestPayload { Name = "some-name" })
            .AsDynamoDb();

        var batchResponse = await application.HandleAsync(request, factory);

        Assert.True(isSuccessful);
        Assert.Empty(batchResponse.BatchItemFailures);
    }

    [Fact]
    public async Task Send_UnknownTopic_ReportsSequenceNumberAsBatchFailure()
    {
        bool? isSuccessful = null;
        var (application, factory) = BuildApplication(context => isSuccessful = context.MessageResult?.IsSuccessful);

        var request = MessageBuilder
            .Create("example-orders:UNKNOWN", new ExampleRequestPayload { Name = "some-name" })
            .AsDynamoDb();

        var batchResponse = await application.HandleAsync(request, factory);

        Assert.False(isSuccessful);
        var failure = Assert.Single(batchResponse.BatchItemFailures);
        Assert.Equal("1", failure.ItemIdentifier);
    }

    [Fact]
    public async Task Send_UnknownTopic_WithPresetTopic_RoutesToPresetTopic()
    {
        // #227: .UsePresetTopic() previously threw a BenzeneResolutionException on every DynamoDB
        // record because AddDynamoDb() never registered PresetTopicHolder or wrapped
        // DynamoDbMessageTopicGetter in PresetTopicMessageTopicGetter<DynamoDbRecordContext>.
        var services = ServiceResolverMother.CreateServiceCollection();
        services
            .AddTransient<ILogger<MessageRouter<DynamoDbRecordContext>>>(_ => NullLogger<MessageRouter<DynamoDbRecordContext>>.Instance)
            .AddTransient<ILogger>(_ => NullLogger.Instance)
            .UsingBenzene(x => x.AddDynamoDb());

        bool? isSuccessful = null;
        var pipeline = new MiddlewarePipelineBuilder<DynamoDbRecordContext>(new MicrosoftBenzeneServiceContainer(services));
        pipeline
            .UsePresetTopic(Defaults.Topic)
            .OnResponse("Check Response", context => isSuccessful = context.MessageResult?.IsSuccessful)
            .UseMessageHandlers();

        var application = new DynamoDbApplication(pipeline.Build());
        var factory = new MicrosoftServiceResolverFactory(services);

        // No handler is registered for "example-orders:UNKNOWN" - the preset supplies the route.
        var request = MessageBuilder
            .Create("example-orders:UNKNOWN", Defaults.MessageAsObject)
            .AsDynamoDb();

        var batchResponse = await application.HandleAsync(request, factory);

        Assert.True(isSuccessful);
        Assert.Empty(batchResponse.BatchItemFailures);
    }
}
