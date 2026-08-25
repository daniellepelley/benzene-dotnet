using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.HealthChecks;
using Benzene.HealthChecks.Core;
using Benzene.Results;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Examples;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace Benzene.Test.Plugins.HealthChecks;

public class HealthCheckTests
{
    [Fact]
    public async Task SimpleHealthCheck()
    {
        var sim = new SimpleHealthCheck();
        var result = await sim.ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Ok, result.Status);
    }

    [Fact]
    public async Task HandlesExceptions()
    {
        var mockHealthCheck = new Mock<IHealthCheck>();
        mockHealthCheck.Setup(x => x.ExecuteAsync(It.IsAny<CancellationToken>())).Throws<Exception>();

        var result =  await HealthCheckProcessor.PerformHealthChecksAsync(Defaults.HealthCheckTopic, new []{ mockHealthCheck.Object });

        var healthCheckResult = result.PayloadAsObject as HealthCheckResponse;
        Assert.False(healthCheckResult.IsHealthy);
    }

    [Fact]
    public async Task WarningResult_DoesNotFlipIsHealthy()
    {
        var warning = new Mock<IHealthCheck>();
        warning.Setup(x => x.Type).Returns("warn");
        warning.Setup(x => x.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(HealthCheckResult.CreateWarning("warn"));
        var ok = new Mock<IHealthCheck>();
        ok.Setup(x => x.Type).Returns("ok");
        ok.Setup(x => x.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(HealthCheckResult.CreateInstance(true, "ok"));

        var result = await HealthCheckProcessor.PerformHealthChecksAsync(Defaults.HealthCheckTopic, new[] { warning.Object, ok.Object });

        // A Warning is degraded-but-not-fatal: the aggregate stays healthy (200), unlike a Failed.
        var response = result.PayloadAsObject as HealthCheckResponse;
        Assert.True(response.IsHealthy);
        Assert.Equal(BenzeneResultStatus.Ok, result.Status);
    }

    [Fact]
    public async Task AnyFailedResult_FlipsIsHealthy_EvenAlongsideWarning()
    {
        var warning = new Mock<IHealthCheck>();
        warning.Setup(x => x.Type).Returns("warn");
        warning.Setup(x => x.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(HealthCheckResult.CreateWarning("warn"));
        var failed = new Mock<IHealthCheck>();
        failed.Setup(x => x.Type).Returns("fail");
        failed.Setup(x => x.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(HealthCheckResult.CreateInstance(false, "fail"));

        var result = await HealthCheckProcessor.PerformHealthChecksAsync(Defaults.HealthCheckTopic, new[] { warning.Object, failed.Object });

        var response = result.PayloadAsObject as HealthCheckResponse;
        Assert.False(response.IsHealthy);
        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
    }

    [Fact]
    public async Task Processor_CancelledCheck_IsReportedAsCancelled_NotAnOpaqueException()
    {
        var check = new Mock<IHealthCheck>();
        check.Setup(x => x.Type).Returns("c");
        check.Setup(x => x.ExecuteAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());

        var result = await HealthCheckProcessor.PerformHealthChecksAsync(Defaults.HealthCheckTopic, new[] { check.Object });

        var response = result.PayloadAsObject as HealthCheckResponse;
        var c = response.HealthChecks["c"];
        Assert.Equal(HealthCheckStatus.Failed, c.Status);
        Assert.Equal("Cancelled", c.Data["Error"]);
    }

    [Fact]
    public async Task Processor_WithShortTimeout_FailsSlowCheck()
    {
        // Since DelayHealthCheck now forwards the token it is given (as every conforming IHealthCheck
        // must - WP-7 #2), the processor's timeout genuinely cancels the wait: the check's own
        // Task.Delay observes the cancellation and throws, which ExceptionHandlingHealthCheck (the
        // decorator closest to the raw check) reports as "Cancelled" - a more accurate label than the
        // old "Timed Out" wording, since the call really was cancelled, not merely abandoned while it
        // kept running in the background. Genuine cancellation reaching the check is asserted directly
        // by Processor_WithShortTimeout_ActuallyCancelsAConformingCheck below.
        var slow = new DelayHealthCheck("slow", TimeSpan.FromSeconds(5));

        var result = await new HealthCheckProcessor(TimeSpan.FromMilliseconds(50)).PerformHealthChecksAsync(new IHealthCheck[] { slow });

        var response = result.PayloadAsObject as HealthCheckResponse;
        Assert.False(response.IsHealthy);
        var check = response.HealthChecks["slow"];
        Assert.Equal(HealthCheckStatus.Failed, check.Status);
        Assert.Equal("Cancelled", check.Data["Error"]);
    }

    [Fact]
    public async Task Processor_WithShortTimeout_ActuallyCancelsAConformingCheck()
    {
        // WP-7 #2's core deliverable: TimeOutHealthCheck links a timeout-derived CancellationTokenSource
        // and passes its token through, so a timeout genuinely cancels the underlying call instead of
        // just abandoning the awaited task. Assert this directly - not merely that the reported status
        // is Failed - by checking the check's own token was the one that requested cancellation.
        var check = new CancellationObservingHealthCheck(TimeSpan.FromSeconds(5));

        await new HealthCheckProcessor(TimeSpan.FromMilliseconds(50)).PerformHealthChecksAsync(new IHealthCheck[] { check });

        Assert.True(check.WasCancelled);
    }

    [Fact]
    public async Task Processor_WithinTimeout_ReturnsTheChecksActualResult()
    {
        var fast = new DelayHealthCheck("fast", TimeSpan.Zero);

        var result = await new HealthCheckProcessor(TimeSpan.FromSeconds(5)).PerformHealthChecksAsync(new IHealthCheck[] { fast });

        var response = result.PayloadAsObject as HealthCheckResponse;
        Assert.True(response.IsHealthy);
        Assert.Equal(HealthCheckStatus.Ok, response.HealthChecks["fast"].Status);
    }

    [Fact]
    public async Task Processor_CapturesPerCheckDuration()
    {
        var check = new DelayHealthCheck("timed", TimeSpan.FromMilliseconds(20));

        var result = await new HealthCheckProcessor().PerformHealthChecksAsync(new IHealthCheck[] { check });

        var response = result.PayloadAsObject as HealthCheckResponse;
        Assert.True(response.HealthChecks["timed"].Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task Dependencies_SurviveAggregation()
    {
        var dependencies = new[] { new HealthCheckDependency("Queue", "some-queue-url") };
        var mockHealthCheck = new Mock<IHealthCheck>();
        mockHealthCheck.Setup(x => x.ExecuteAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(HealthCheckResult.CreateInstance(true, "some-name", new Dictionary<string, object>(), dependencies));
        mockHealthCheck.Setup(x => x.Type).Returns("some-name");

        var result = await HealthCheckProcessor.PerformHealthChecksAsync(Defaults.HealthCheckTopic, new[] { mockHealthCheck.Object });

        var healthCheckResult = result.PayloadAsObject as HealthCheckResponse;
        var check = healthCheckResult.HealthChecks["some-name"];
        var dependency = Assert.Single(check.Dependencies);
        Assert.Equal("Queue", dependency.Kind);
        Assert.Equal("some-queue-url", dependency.Name);
    }

    [Fact]
    public async Task HealthCheckBuilderTest()
    {
        var serviceCollection = new ServiceCollection();

        var container = new MicrosoftBenzeneServiceContainer(serviceCollection);
        container.AddBenzene().AddBenzeneMessage().AddMessageHandlers();

        var middlewarePipelineBuilder = new MiddlewarePipelineBuilder<BenzeneMessageContext>(container);
        middlewarePipelineBuilder
            // .UseProcessResponse()
            .UseHealthCheck("benzene:healthcheck", x => x
                .AddHealthCheck<SimpleHealthCheck>()
                .AddHealthCheck(new SimpleHealthCheck())
                .AddHealthCheckFactory(new SimpleHealthCheckFactory())
                .AddHealthCheck(_ => new SimpleHealthCheck())
                .AddHealthCheck(resolver => resolver.GetService<SimpleHealthCheck>())
                .AddHealthCheck("inline", _ => Task.FromResult(HealthCheckResult.CreateInstance(false)))
                .AddHealthCheck("inline", _ => HealthCheckResult.CreateInstance(false))
                .AddHealthCheck(async _ => await Task.FromResult(HealthCheckResult.CreateInstance(false)))
                .AddHealthCheck(_ => HealthCheckResult.CreateInstance(false))
                .AddHealthCheck("inline", _ => Task.FromResult(false))
                .AddHealthCheck("inline", _ => false)
                .AddHealthCheck(async _ => await Task.FromResult(false))
                .AddHealthCheck(_ => false));

        
        var context = new BenzeneMessageContext(new BenzeneMessageRequest { Topic = "benzene:healthcheck" });

        await middlewarePipelineBuilder.Build().HandleAsync(context, new MicrosoftServiceResolverAdapter(serviceCollection.BuildServiceProvider()));

        Assert.NotNull(context.BenzeneMessageResponse);

        var response = JsonConvert.DeserializeObject<HealthCheckResponse>(context.BenzeneMessageResponse.Body);
        Assert.Equal(13, response.HealthChecks.Count);
    }
    
    [Fact]
    public async Task HealthCheckBuilder_WithExceptions_Test()
    {
        var serviceCollection = new ServiceCollection();

        var container = new MicrosoftBenzeneServiceContainer(serviceCollection);
        container.AddBenzene().AddBenzeneMessage().AddMessageHandlers();

        var middlewarePipelineBuilder = new MiddlewarePipelineBuilder<BenzeneMessageContext>(container);
        middlewarePipelineBuilder
            // .UseProcessResponse()
            .UseHealthCheck("benzene:healthcheck", x => x
                .AddHealthCheck<ExceptionThrowingHealthCheck>()
                .AddHealthCheck(new ExceptionThrowingHealthCheck())
                .AddHealthCheckFactory(new ExceptionThrowingHealthCheckFactory())
                .AddHealthCheck(_ => new ExceptionThrowingHealthCheck())
                .AddHealthCheck(_ => throw new Exception()));
        
        var context = new BenzeneMessageContext(new BenzeneMessageRequest { Topic = "benzene:healthcheck" });

        await middlewarePipelineBuilder.Build().HandleAsync(context, new MicrosoftServiceResolverAdapter(serviceCollection.BuildServiceProvider()));

        Assert.NotNull(context.BenzeneMessageResponse);

        var response = JsonConvert.DeserializeObject<HealthCheckResponse>(context.BenzeneMessageResponse.Body);
        Assert.Equal(5, response.HealthChecks.Count);
    }

}

public class ExceptionThrowingHealthCheckFactory : IHealthCheckFactory
{
    public IHealthCheck Create(IServiceResolver resolver)
    {
        throw new Exception();
    }
}

public class ExceptionThrowingHealthCheck : IHealthCheck
{
    public string Type { get; }
    public Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        throw new Exception();
    }
}

public class DelayHealthCheck : IHealthCheck
{
    private readonly TimeSpan _delay;
    public DelayHealthCheck(string type, TimeSpan delay)
    {
        Type = type;
        _delay = delay;
    }

    public string Type { get; }

    public async Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        if (_delay > TimeSpan.Zero)
        {
            // Forwards the token, like a conforming IHealthCheck must (WP-7 #2): this is what lets
            // the processor's timeout genuinely cancel the wait rather than merely abandon it.
            await Task.Delay(_delay, cancellationToken);
        }
        return HealthCheckResult.CreateInstance(true, Type);
    }
}

// A health check whose ExecuteAsync records whether the token it was given was actually the one that
// requested cancellation - proof the processor's timeout genuinely cancels the underlying call (WP-7
// #2), not merely abandons the awaited task while it keeps running in the background.
public class CancellationObservingHealthCheck : IHealthCheck
{
    private readonly TimeSpan _delay;
    public bool WasCancelled { get; private set; }

    public CancellationObservingHealthCheck(TimeSpan delay)
    {
        _delay = delay;
    }

    public string Type => "cancellation-observing";

    public async Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            WasCancelled = true;
            throw;
        }

        return HealthCheckResult.CreateInstance(true, Type);
    }
}


