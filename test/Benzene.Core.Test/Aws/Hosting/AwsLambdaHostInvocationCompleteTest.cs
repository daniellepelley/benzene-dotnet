using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Amazon.Lambda.TestUtilities;
using Benzene.Abstractions.Hosting;
using Benzene.Aws.Lambda.Core;
using Benzene.Aws.Lambda.Core.TestHelpers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using LogLevel = global::Microsoft.Extensions.Logging.LogLevel;

namespace Benzene.Test.Aws.Hosting;

// Records every log call, so the test can assert the OnInvocationCompleteAsync failure was logged
// rather than silently swallowed.
public class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, Exception Exception, string Message)> Entries { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        Entries.Add((logLevel, exception, formatter(state, exception)));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

public class InvocationOutcomeException : Exception
{
    public InvocationOutcomeException(string message) : base(message) { }
}

// The pipeline itself throws unconditionally, before any response is written - simulating the
// invocation's real exception (exception A).
public class ThrowingPipelineStartUp : BenzeneStartUp
{
    // Set by the test before the host is built - the startup is `new()`-constructed by
    // AwsLambdaHost's constructor, so there's no constructor to pass a capturing logger through
    // (same static-state pattern as UseAspNetWorkerTest.Port).
    public static CapturingLogger<AwsLambdaHost<ThrowingPipelineStartUp>> Logger = new();

    public override IConfiguration GetConfiguration() => new ConfigurationBuilder().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        => services
            .AddSingleton<ILogger<AwsLambdaHost<ThrowingPipelineStartUp>>>(Logger)
            .UsingBenzene(x => x.AddBenzene());

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration) => app
        .UseAwsLambda(aws => aws
            .Use("ThrowA", _ => (context, next) => throw new InvocationOutcomeException("A")));
}

// #107: OnInvocationCompleteAsync (the documented telemetry-flush override point) throws exception B.
public class ThrowingOnCompleteHost<TStartUp> : AwsLambdaHost<TStartUp> where TStartUp : BenzeneStartUp, new()
{
    protected override Task OnInvocationCompleteAsync() => throw new InvocationOutcomeException("B");
}

public class AwsLambdaHostInvocationCompleteTest
{
    [Fact]
    public async Task OnInvocationCompleteFailure_DoesNotMaskTheInvocationsRealException_AndIsLogged()
    {
        ThrowingPipelineStartUp.Logger = new CapturingLogger<AwsLambdaHost<ThrowingPipelineStartUp>>();

        using var host = new AwsLambdaBenzeneTestHost(new ThrowingOnCompleteHost<ThrowingPipelineStartUp>());

        var thrown = await Assert.ThrowsAsync<InvocationOutcomeException>(() =>
            host.SendEventAsync(new object(), new TestLambdaContext()));

        // Exception A (the invocation's real outcome) is what propagates/is reported - not exception B
        // from the OnInvocationCompleteAsync override point.
        Assert.Equal("A", thrown.Message);

        // Exception B (the OnInvocationCompleteAsync failure) is logged, not silently swallowed.
        var logged = Assert.Single(ThrowingPipelineStartUp.Logger.Entries);
        Assert.Equal(LogLevel.Error, logged.Level);
        Assert.IsType<InvocationOutcomeException>(logged.Exception);
        Assert.Equal("B", logged.Exception.Message);
    }
}
