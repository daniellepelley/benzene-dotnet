using System.Threading.Tasks;
using Amazon.Lambda.S3Events;
using Benzene.Abstractions.DI;
using Benzene.Aws.Lambda.Core.AwsEventStream;
using Benzene.Aws.Lambda.EventBridge;
using Benzene.Aws.Lambda.EventBridge.TestHelpers;
using Benzene.Aws.Lambda.S3;
using Benzene.Aws.Lambda.Sns;
using Benzene.Aws.Lambda.Sns.TestHelpers;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Aws.Helpers;
using Benzene.Test.Examples;
using Benzene.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Aws;

/// <summary>
/// The fire-and-forget bindings — SNS, EventBridge, S3 — write no response body, so they must mark the
/// <see cref="AwsEventStreamContext"/> claimed explicitly. Without it <c>AwsLambdaEntryPoint</c> cannot
/// tell a handled-but-responseless invocation from an unroutable one and throws "the event type has not
/// been recognized" <em>after</em> the records were processed.
/// </summary>
/// <remarks>
/// Each binding's own pipeline test drives the inner record pipeline directly and asserts on the record
/// context, which never observes the outer claim — so the gap was invisible there and only surfaced when
/// an SNS event went through the full entry point (the example's thread-safety test). These lock the
/// outer claim the entry point actually depends on.
/// </remarks>
public class ResponselessBindingsMarkHandledTest
{
    [Fact]
    public async Task SnsEvent_MarksTheOuterContextHandled()
    {
        var services = ServiceResolverMother.CreateServiceCollection();
        var app = new MiddlewarePipelineBuilder<AwsEventStreamContext>(new MicrosoftBenzeneServiceContainer(services));
        app.UseSns(sns => sns.Use(null, (_, next) => next()));

        var context = AwsEventStreamContextBuilder.Build(
            MessageBuilder.Create(Defaults.Topic, Defaults.MessageAsObject).AsSns());
        await app.Build().HandleAsync(context, Resolver(services));

        Assert.True(context.Handled);
    }

    [Fact]
    public async Task EventBridgeEvent_MarksTheOuterContextHandled()
    {
        var services = ServiceResolverMother.CreateServiceCollection();
        var app = new MiddlewarePipelineBuilder<AwsEventStreamContext>(new MicrosoftBenzeneServiceContainer(services));
        app.UseEventBridge(eventBridge => eventBridge.Use(null, (_, next) => next()));

        var context = AwsEventStreamContextBuilder.Build(
            MessageBuilder.Create(Defaults.Topic, Defaults.MessageAsObject).AsEventBridge());
        await app.Build().HandleAsync(context, Resolver(services));

        Assert.True(context.Handled);
    }

    [Fact]
    public async Task S3Event_MarksTheOuterContextHandled()
    {
        var services = ServiceResolverMother.CreateServiceCollection();
        var app = new MiddlewarePipelineBuilder<AwsEventStreamContext>(new MicrosoftBenzeneServiceContainer(services));
        app.UseS3(s3 => s3.Use(null, (_, next) => next()));

        var s3Event = new S3Event
        {
            Records =
            [
                new S3Event.S3EventNotificationRecord { EventName = "ObjectCreated:Put", EventSource = "aws:s3" }
            ]
        };
        var context = AwsEventStreamContextBuilder.Build(s3Event);
        await app.Build().HandleAsync(context, Resolver(services));

        Assert.True(context.Handled);
    }

    private static IServiceResolver Resolver(IServiceCollection services)
        => new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
}
