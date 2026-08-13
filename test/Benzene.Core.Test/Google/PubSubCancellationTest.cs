using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Hosting;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.GoogleCloud.Functions.PubSub;
using Benzene.GoogleCloud.Functions.PubSub.TestHelpers;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Benzene.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Google;

/// <summary>
/// Phase 3 of the cancellation initiative (<c>work/cancellation-design.md</c>): the Cloud Functions
/// Framework's invocation token, previously discarded by <c>GooglePubSubFunctionHost.HandleAsync</c>
/// and (independently) by <see cref="PubSubMiddlewareApplication"/> never seeding the scope, now
/// reaches the handler via <see cref="ICancellationTokenAccessor"/>.
/// </summary>
public class PubSubCancellationRequest
{
    public string Name { get; set; }
}

public class PubSubCancellationCapturingHandler(ICancellationTokenAccessor accessor)
    : IMessageHandler<PubSubCancellationRequest, Void>
{
    public static CancellationToken? Observed { get; set; }

    public Task<IBenzeneResult<Void>> HandleAsync(PubSubCancellationRequest request)
    {
        Observed = accessor.CancellationToken;
        return Task.FromResult(BenzeneResult.Ok(new Void()));
    }
}

public class PubSubCancellationStartUp : BenzeneStartUp
{
    public const string Topic = "pubsub-cancellation-test-topic";

    public override IConfiguration GetConfiguration() => new ConfigurationBuilder().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        => services.UsingBenzene(x => x
            .AddBenzene()
            .AddMessageHandlers()
            .AddScoped<PubSubCancellationCapturingHandler>()
            .AddSingleton<IMessageHandlerDefinition>(_ => MessageHandlerDefinition.CreateInstance(
                Topic, "", typeof(PubSubCancellationRequest), typeof(Void), typeof(PubSubCancellationCapturingHandler))));

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration) => app
        .UsePubSub(pubsub => pubsub.UseMessageHandlers());
}

public class PubSubCancellationTest
{
    [Fact]
    public async Task SendPubSubAsync_WithToken_HandlerObservesItViaTheAccessor()
    {
        PubSubCancellationCapturingHandler.Observed = null;
        using var cts = new CancellationTokenSource();

        var function = BenzeneTestHost.Create<PubSubCancellationStartUp>().BuildGooglePubSubFunctionHost();
        var data = MessageBuilder.Create(PubSubCancellationStartUp.Topic, new PubSubCancellationRequest { Name = "world" }).AsPubSubEvent();

        await function.SendPubSubAsync(data, cts.Token);

        Assert.Equal(cts.Token, PubSubCancellationCapturingHandler.Observed);
    }

    [Fact]
    public async Task SendPubSubAsync_WithoutToken_HandlerObservesNone()
    {
        PubSubCancellationCapturingHandler.Observed = null;

        var function = BenzeneTestHost.Create<PubSubCancellationStartUp>().BuildGooglePubSubFunctionHost();
        var data = MessageBuilder.Create(PubSubCancellationStartUp.Topic, new PubSubCancellationRequest { Name = "world" }).AsPubSubEvent();

        await function.SendPubSubAsync(data);

        Assert.Equal(CancellationToken.None, PubSubCancellationCapturingHandler.Observed);
    }
}
