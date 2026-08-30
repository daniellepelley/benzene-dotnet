using System.Threading.Tasks;
using Amazon.Lambda.S3Events;
using Benzene.Abstractions.Results;
using Benzene.Aws.Lambda.S3;
using Benzene.Aws.Lambda.S3.TestHelpers;
using Benzene.Core.MessageHandlers;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Aws.Helpers;
using Benzene.Test.Examples;
using Benzene.Testing;
using Xunit;

namespace Benzene.Test.Aws.S3;

public class S3MessagePipelineTest
{
    private static S3Event CreateRequest()
    {
        return new S3Event
        {
            Records =
            [
                new S3Event.S3EventNotificationRecord
                {
                    EventName = "some-event"
                }
            ]
        };
    }

    [Fact]
    public async Task Send()
    {
        string eventName = null;

        var host = new EntryPointMiddleApplicationBuilder<S3Event, S3RecordContext>()
            .ConfigureServices(services =>
            {
                services
                    .ConfigureServiceCollection()
                    .UsingBenzene(x => x.AddS3());
            })
            .Configure(app => app
                .OnResponse("Check Response", context =>
                {
                    eventName = context.S3EventNotificationRecord.EventName;
                }))
            // This test is about the record reaching the pipeline, not message routing - it never sets
            // a MessageResult, so escalating on that (#229's null-result fix) would be unrelated noise.
            .Build(x => new S3Application(x, new S3Options { RaiseOnFailureStatus = false }));

        await host.SendAsync(CreateRequest());

        Assert.Equal("some-event", eventName);
    }

    [Fact]
    public async Task Send_UnknownEventName_WithPresetTopic_RoutesToPresetTopic()
    {
        // #227: .UsePresetTopic() previously threw a BenzeneResolutionException on every S3 record
        // because AddS3() never registered PresetTopicHolder or wrapped S3MessageTopicGetter in
        // PresetTopicMessageTopicGetter<S3RecordContext> - so PresetTopicMiddleware<S3RecordContext>
        // could never resolve the holder it needs.
        IBenzeneResult messageResult = null;

        var host = new EntryPointMiddleApplicationBuilder<S3Event, S3RecordContext>()
            .ConfigureServices(services =>
            {
                services
                    .ConfigureServiceCollection()
                    .UsingBenzene(x => x.AddS3());
            })
            .Configure(app => app
                .UsePresetTopic(Defaults.Topic)
                .OnResponse("Check Response", context =>
                {
                    messageResult = context.MessageResult;
                })
                .UseMessageHandlers())
            .Build(x => new S3Application(x));

        // No handler is registered for this event name - the preset supplies the route instead.
        var request = MessageBuilder.Create("no-such-event", Defaults.MessageAsObject).AsS3();

        await host.SendAsync(request);

        Assert.True(messageResult.IsSuccessful);
    }
}
