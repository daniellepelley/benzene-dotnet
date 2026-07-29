using System;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Abstractions.StartUpChecks;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.MessageHandlers.StartUpChecks;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Examples;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Core.Core.StartUpChecks;

/// <summary>
/// The start-up check phase: it runs, it reports, and one switch turns it all off.
/// </summary>
/// <remarks>
/// Benzene had five wiring checks across five packages, each opt-in, each with its own name and
/// severity, and no host called any of them. The one thing that did run at INIT — warm-up — swallowed
/// every exception, so a duplicate-topic error it had already discovered was discarded and re-thrown
/// on the first message. These tests are about the property that fixes: a check's failure survives.
/// </remarks>
public class StartUpCheckTest
{
    private sealed class ThrowingCheck : IStartUpCheck
    {
        public string Name => "always-fails";
        public void Check(IServiceResolver resolver) => throw new InvalidOperationException("the thing is wrong");
    }

    private sealed class AlsoThrowingCheck : IStartUpCheck
    {
        public string Name => "also-fails";
        public void Check(IServiceResolver resolver) => throw new InvalidOperationException("so is the other thing");
    }

    private static IServiceResolverFactory Factory(Action<IBenzeneServiceContainer> configure)
    {
        var services = new ServiceCollection();
        configure(new MicrosoftBenzeneServiceContainer(services));
        return new MicrosoftServiceResolverFactory(services);
    }

    [Fact]
    public void AFailingCheckFailsStartUp()
    {
        var factory = Factory(x => x.AddSingleton<IStartUpCheck, ThrowingCheck>());

        var exception = Assert.Throws<BenzeneStartUpCheckException>(() => factory.RunStartUpChecks());

        Assert.Contains("always-fails", exception.Message);
        Assert.Contains("the thing is wrong", exception.Message);
        Assert.Equal(new[] { "always-fails" }, exception.FailedChecks);
    }

    [Fact]
    public void EveryFailureIsReported_NotJustTheFirst()
    {
        // One wiring mistake often trips several checks. Fixing them one round-trip at a time is the
        // friction this phase exists to remove.
        var factory = Factory(x => x
            .AddSingleton<IStartUpCheck, ThrowingCheck>()
            .AddSingleton<IStartUpCheck, AlsoThrowingCheck>());

        var exception = Assert.Throws<BenzeneStartUpCheckException>(() => factory.RunStartUpChecks());

        Assert.Equal(new[] { "always-fails", "also-fails" }, exception.FailedChecks);
        Assert.Contains("2 wiring problem(s)", exception.Message);
    }

    [Fact]
    public void AdvisoryModeLetsStartUpContinue()
    {
        var factory = Factory(x => x
            .AddSingleton<IStartUpCheck, ThrowingCheck>()
            .AddBenzeneStartUpChecks(BenzeneStartUpCheckMode.Advisory));

        factory.RunStartUpChecks();
    }

    [Fact]
    public void DisabledModeRunsNothing()
    {
        var factory = Factory(x => x
            .AddSingleton<IStartUpCheck, ThrowingCheck>()
            .AddBenzeneStartUpChecks(BenzeneStartUpCheckMode.Disabled));

        factory.RunStartUpChecks();
    }

    [Fact]
    public void TheErrorNamesTheKillSwitch()
    {
        // A newcomer who believes a check is wrong must be able to turn the whole thing off in one
        // line, from the error itself — or they abandon Benzene rather than debug the thing that was
        // meant to help them debug it.
        var factory = Factory(x => x.AddSingleton<IStartUpCheck, ThrowingCheck>());

        var exception = Assert.Throws<BenzeneStartUpCheckException>(() => factory.RunStartUpChecks());

        Assert.Contains("AddBenzeneStartUpChecks", exception.Message);
    }

    [Fact]
    public void TwoHandlersOnOneTopicFailsStartUp()
    {
        // Registered across two finders: reflection finds ExampleMessageHandler for Defaults.Topic, and
        // this adds a second handler for the same topic explicitly. ReflectionMessageHandlersFinder
        // throws for the same collision inside its own scan, but MessageHandlerDefinitionIndex groups
        // and takes .First(), so across finders it used to pass silently and answer with one of them.
        var factory = Factory(x => x
            .AddBenzene()
            .AddMessageHandlers(new[] { typeof(ExampleMessageHandler) })
            .AddSingleton<IMessageHandlerDefinition>(_ => MessageHandlerDefinition.CreateInstance(
                Defaults.Topic, "", typeof(ExampleRequestPayload), typeof(ExampleResponsePayload), typeof(ShadowHandler))));

        var exception = Assert.Throws<BenzeneStartUpCheckException>(() => factory.RunStartUpChecks());

        Assert.Contains("duplicate-topic", exception.FailedChecks);
        Assert.Contains(Defaults.Topic, exception.Message);
        Assert.Contains("ShadowHandler", exception.Message);
    }

    [Fact]
    public void OneHandlerPerTopicPassesCleanly()
    {
        var factory = Factory(x => x
            .AddBenzene()
            .AddMessageHandlers(new[] { typeof(ExampleMessageHandler) }));

        factory.RunStartUpChecks();
    }

    private sealed class ShadowHandler : IMessageHandler<ExampleRequestPayload, ExampleResponsePayload>
    {
        public Task<IBenzeneResult<ExampleResponsePayload>> HandleAsync(ExampleRequestPayload message) =>
            throw new NotSupportedException();
    }
}
