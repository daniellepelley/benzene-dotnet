using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.MessageHandlers.StartUpChecks;
using Benzene.Core.Messages;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Benzene.Test.Core.Core.StartUpChecks;

/// <summary>
/// Version selection's start-up check: two declared defaults fail start-up; the valid-but-surprising
/// arrangements (no default, an ignored declaration, a cast tie) are logged. Plus the declaration
/// itself travelling from <c>[Message(..., IsDefault = true)]</c> to the lookup.
/// </summary>
public class VersionSelectionStartUpCheckTest
{
    private const string Topic = "test:version-selection";

    public class Request { }

    [Message(Topic, "v1", IsDefault = true)]
    public class DefaultV1Handler : IMessageHandler<Request, Request>
    {
        public Task<IBenzeneResult<Request>> HandleAsync(Request request) => Task.FromResult(BenzeneResult.Ok(request));
    }

    [Message(Topic, "v2")]
    public class V2Handler : IMessageHandler<Request, Request>
    {
        public Task<IBenzeneResult<Request>> HandleAsync(Request request) => Task.FromResult(BenzeneResult.Ok(request));
    }

    // Deliberately no [Message]: the test assembly is scanned whole by other tests, and a second
    // declared default discovered there would fail every one of their start-ups. Registered explicitly.
    public class SecondDefaultV3Handler : IMessageHandler<Request, Request>
    {
        public Task<IBenzeneResult<Request>> HandleAsync(Request request) => Task.FromResult(BenzeneResult.Ok(request));
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(Messages);
        public void Dispose() { }

        private sealed class Logger : ILogger
        {
            private readonly List<string> _messages;
            public Logger(List<string> messages) => _messages = messages;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => _messages.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }

    private static (IServiceResolverFactory Factory, RecordingLoggerProvider Logs) Build(params Type[] handlers)
    {
        var logs = new RecordingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(x => x.AddProvider(logs));
        new MicrosoftBenzeneServiceContainer(services).AddBenzene().AddMessageHandlers(handlers);
        return (new MicrosoftServiceResolverFactory(services), logs);
    }

    [Fact]
    public void TheAttributeDeclaresTheDefault_AndAnUnversionedMessageReachesIt()
    {
        var (factory, _) = Build(typeof(DefaultV1Handler), typeof(V2Handler));
        factory.RunStartUpChecks();

        using var resolver = factory.CreateScope();
        var lookUp = resolver.GetService<IMessageHandlerDefinitionLookUp>();

        Assert.Equal(typeof(DefaultV1Handler), lookUp.FindHandler(new Topic(Topic))!.HandlerType);
        Assert.Equal(typeof(V2Handler), lookUp.FindHandler(new Topic(Topic, "v2"))!.HandlerType);
        Assert.Null(lookUp.FindHandler(new Topic(Topic, "v9")));
    }

    [Fact]
    public void TwoDeclaredDefaults_FailStartUp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        new MicrosoftBenzeneServiceContainer(services).AddBenzene()
            .AddMessageHandlers(new[] { typeof(DefaultV1Handler) })
            .AddScoped<SecondDefaultV3Handler>()
            .AddSingleton<IMessageHandlerDefinition>(_ => MessageHandlerDefinition.CreateInstance(
                Topic, "v3", typeof(Request), typeof(Request), typeof(SecondDefaultV3Handler), isDefault: true));
        var factory = new MicrosoftServiceResolverFactory(services);

        var exception = Assert.Throws<BenzeneStartUpCheckException>(() => factory.RunStartUpChecks());

        Assert.Contains("version-selection", exception.FailedChecks);
        Assert.Contains(Topic, exception.Message);
    }

    [Fact]
    public void OnlyVersionedHandlers_NoDefault_IsAWarningNotAFailure()
    {
        var (factory, logs) = Build(typeof(V2Handler));

        factory.RunStartUpChecks();

        Assert.Contains(logs.Messages, x => x.StartsWith("Warning") && x.Contains(Topic) && x.Contains("IsDefault = true"));
    }

    [Fact]
    public void ACastTie_IsLoggedNamingTheTieBreak_NotAFailure()
    {
        var logs = new RecordingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(x => x.AddProvider(logs));
        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddBenzene().AddMessageHandlers(new[] { typeof(V2Handler) });
        // Both handlers share a request type, so "v0" reaches v2 and an extra explicit v4 handler; the
        // default (none declared here) is not among them.
        container.AddSingleton<IMessageHandlerDefinition>(_ => MessageHandlerDefinition.CreateInstance(
            Topic, "v4", typeof(Request), typeof(Request), typeof(V2Handler)));
        container.AddSingleton<IPayloadVersionCasts>(_ => new EveryVersionCastsToRequest("v0"));
        var factory = new MicrosoftServiceResolverFactory(services);

        factory.RunStartUpChecks();

        Assert.Contains(logs.Messages, x => x.Contains("'v0'") && x.Contains("tie-break") && x.Contains("'v4'"));
        using var resolver = factory.CreateScope();
        Assert.Equal("v4", resolver.GetService<IMessageHandlerDefinitionLookUp>().FindHandler(new Topic(Topic, "v0"))!.Topic.Version);
    }

    private sealed class EveryVersionCastsToRequest : IPayloadVersionCasts
    {
        private readonly string _version;
        public EveryVersionCastsToRequest(string version) => _version = version;
        public bool CanCast(string topicId, string fromVersion, Type requestType) => fromVersion == _version && requestType == typeof(Request);
        public IReadOnlyCollection<string> GetVersions(string topicId) => new[] { _version };
    }
}
