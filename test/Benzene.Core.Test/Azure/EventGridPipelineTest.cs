using System.Threading.Tasks;
using Benzene.Azure.Function.Core;
using Benzene.Azure.Function.EventGrid;
using Benzene.Core.MessageHandlers;
using Benzene.Test.Examples;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Benzene.Test.Azure;

public class EventGridPipelineTest
{
    private static IAzureFunctionApp CreateApp(Mock<IExampleService> mockExampleService)
    {
        return new InlineAzureFunctionStartUp()
            .ConfigureServices(services => services
                .ConfigureServiceCollection()
                .AddSingleton(mockExampleService.Object)
            ).Configure(app => app
                .UseEventGrid(eventGrid => eventGrid
                    .UseMessageHandlers()))
            .Build();
    }

    [Fact]
    public async Task EventGridSchemaEvent_RoutesByEventType_AndDeliversDataAsPayload()
    {
        var mockExampleService = new Mock<IExampleService>();
        var app = CreateApp(mockExampleService);

        var eventJson = $$"""
        {
            "id": "event-1",
            "topic": "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct",
            "subject": "/blobServices/default/containers/orders",
            "eventType": "{{Defaults.Topic}}",
            "eventTime": "2026-07-17T10:00:00Z",
            "dataVersion": "1.0",
            "data": {{Defaults.Message}}
        }
        """;

        await app.HandleEventGridEvent(eventJson);

        mockExampleService.Verify(x => x.Register(Defaults.Name));
    }

    [Fact]
    public async Task CloudEventsSchemaEvent_RoutesByType_AndDeliversDataAsPayload()
    {
        var mockExampleService = new Mock<IExampleService>();
        var app = CreateApp(mockExampleService);

        var eventJson = $$"""
        {
            "specversion": "1.0",
            "id": "event-2",
            "source": "/mycontext",
            "subject": "orders/42",
            "type": "{{Defaults.Topic}}",
            "time": "2026-07-17T10:00:00Z",
            "data": {{Defaults.Message}}
        }
        """;

        await app.HandleEventGridEvent(eventJson);

        mockExampleService.Verify(x => x.Register(Defaults.Name));
    }

    // Round 14-15 #235: EventGridTriggerEvent.Parse used to run eagerly as a method argument, before
    // dispatch even started - a JsonException from malformed input was an unguarded throw straight out
    // of this call, bypassing EventGridOptions.CatchExceptions (and AzureFunctionBatchApplicationBase's
    // own catch/escalate/log machinery) entirely. Now the parse happens inside the dispatched
    // pipeline's own per-event try, so a malformed delivery goes through the exact same path any other
    // per-event failure does. Default CatchExceptions=false still cascades the SAME JsonException
    // (matching Event Grid's retain-on-failure settlement default - see
    // work/settlement-consistency-fix-plan.md) - proving this via the real trigger-dispatch path
    // (app.HandleEventGridEvent), not a mocked pipeline.
    [Fact]
    public async Task MalformedJson_DefaultOptions_ThrowsJsonExceptionAndCascades()
    {
        var mockExampleService = new Mock<IExampleService>();
        var app = CreateApp(mockExampleService);

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => app.HandleEventGridEvent("not valid json"));

        mockExampleService.Verify(x => x.Register(It.IsAny<string>()), Times.Never);
    }

    // The other half of #235: with CatchExceptions opted in, the same malformed delivery is now
    // actually caught (previously it crashed the whole invocation regardless of this setting, since
    // the throw happened before the option's own catch clause was ever reached).
    [Fact]
    public async Task MalformedJson_CatchExceptionsTrue_IsCaughtAndSwallowed()
    {
        var mockExampleService = new Mock<IExampleService>();
        var app = new InlineAzureFunctionStartUp()
            .ConfigureServices(services => services
                .ConfigureServiceCollection()
                .AddSingleton(mockExampleService.Object)
            ).Configure(appBuilder => appBuilder
                .UseEventGrid(eventGrid => eventGrid
                    .UseMessageHandlers(), configure: options => options.CatchExceptions = true))
            .Build();

        // Reaching the end without throwing proves the malformed-JSON failure was caught, not left to
        // crash the whole invocation.
        await app.HandleEventGridEvent("not valid json");

        mockExampleService.Verify(x => x.Register(It.IsAny<string>()), Times.Never);
    }

    // A well-formed delivery through the same raw-JSON dispatch path used by the tests above must be
    // unaffected by #235's fix - regression guard alongside the malformed-input cases.
    [Fact]
    public async Task WellFormedJson_ThroughRawJsonDispatchPath_StillRoutesNormally()
    {
        var mockExampleService = new Mock<IExampleService>();
        var app = CreateApp(mockExampleService);

        var eventJson = $$"""
        {
            "id": "event-well-formed",
            "eventType": "{{Defaults.Topic}}",
            "data": {{Defaults.Message}}
        }
        """;

        await app.HandleEventGridEvent(eventJson);

        mockExampleService.Verify(x => x.Register(Defaults.Name));
    }

    // Focused unit coverage for EventGridContext's raw-JSON constructor: the parse failure is cached,
    // not re-attempted, and every access after the first throws the SAME exception instance - this is
    // what lets EventGridBatchApplication.GetLogId/CreateProcessingException safely catch-and-fall-back
    // instead of the log path itself throwing a second time while merely trying to report the first
    // failure (see EventGridBatchApplication.SafeGetLogId's own doc comment).
    [Fact]
    public void EventGridContext_RawJsonConstructor_MalformedJson_EventAccessThrowsSameExceptionEveryTime()
    {
        var context = new EventGridContext("not valid json");

        var first = Assert.Throws<System.Text.Json.JsonException>(() => context.Event);
        var second = Assert.Throws<System.Text.Json.JsonException>(() => context.Event);

        Assert.Same(first, second);
    }

    [Fact]
    public void Parse_EventGridSchema_MapsEnvelopeFields()
    {
        var parsed = EventGridTriggerEvent.Parse("""
        {
            "id": "event-1",
            "topic": "/subscriptions/sub",
            "subject": "some-subject",
            "eventType": "Custom.Event",
            "eventTime": "2026-07-17T10:00:00Z",
            "dataVersion": "2.1",
            "data": { "name": "value" }
        }
        """);

        Assert.Equal("event-1", parsed.Id);
        Assert.Equal("Custom.Event", parsed.EventType);
        Assert.Equal("some-subject", parsed.Subject);
        Assert.Equal("/subscriptions/sub", parsed.Source);
        Assert.Equal("2.1", parsed.DataVersion);
        Assert.NotNull(parsed.EventTime);
        Assert.NotNull(parsed.Data);
    }

    [Fact]
    public void Parse_CloudEventsSchema_MapsTypeAndSource()
    {
        var parsed = EventGridTriggerEvent.Parse("""
        {
            "specversion": "1.0",
            "id": "event-2",
            "source": "/mycontext",
            "type": "Custom.Event",
            "time": "2026-07-17T10:00:00Z"
        }
        """);

        Assert.Equal("Custom.Event", parsed.EventType);
        Assert.Equal("/mycontext", parsed.Source);
        Assert.Null(parsed.Data);
    }

    [Fact]
    public async Task EnvelopeFields_SurfaceAsHeaders()
    {
        var headersGetter = new EventGridMessageHeadersGetter();
        var context = new EventGridContext(EventGridTriggerEvent.Parse("""
        {
            "id": "event-1",
            "topic": "/subscriptions/sub",
            "subject": "some-subject",
            "eventType": "Custom.Event"
        }
        """));

        var headers = headersGetter.GetHeaders(context);

        Assert.Equal("event-1", headers["id"]);
        Assert.Equal("some-subject", headers["subject"]);
        Assert.Equal("/subscriptions/sub", headers["source"]);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task EventWithoutData_BindsEmptyBody()
    {
        var bodyGetter = new EventGridMessageBodyGetter();
        var context = new EventGridContext(EventGridTriggerEvent.Parse("""{ "eventType": "Custom.Event" }"""));

        Assert.Equal("{}", bodyGetter.GetBody(context));
        await Task.CompletedTask;
    }
}
