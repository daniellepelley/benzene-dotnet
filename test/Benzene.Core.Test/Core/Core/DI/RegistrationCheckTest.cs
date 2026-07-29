using System;
using Benzene.Core.DI;
using Benzene.Core.MessageHandlers;
using Xunit;

namespace Benzene.Test.Core.Core.DI;

public class RegistrationCheckTest
{
    // The two packages involved in the reported failure: .AddBenzene() supplies IDefaultStatuses, and
    // .AddMessageHandlers(<assemblies>) supplies MessageRouter<> — the call the developer had already
    // made and was told to make again.
    private static RegistrationCheck Create() => RegistrationCheck.Create(
        typeof(CoreRegistrations),
        typeof(Benzene.Aws.Lambda.Core.AwsRegistrations));

    private static Exception ResolveFailure() => new(
        "Unable to resolve type MessageRouter",
        new InvalidOperationException(
            $"Unable to resolve service for type '{typeof(IDefaultStatuses).FullName}' while attempting to activate 'MessageHandlerFactory'."));

    [Fact]
    public void RegistrationChecksDeduplicate()
    {
        var result = RegistrationCheck.Create(typeof(Benzene.Azure.Function.Kafka.KafkaRegistrations), typeof(Benzene.Azure.Function.Kafka.KafkaRegistrations));
        Assert.NotNull(result);
    }

    [Fact]
    public void TheHintNamesTheTypeAndThePackageTheRightWayRound()
    {
        // These used to be transposed, so the message read
        //   "Benzene.Core.MessageHandlers, Version=0.0.2.0, Culture=... is registered in .AddBenzene()
        //    from Benzene.Core.MessageHandlers.IDefaultStatuses"
        // — the assembly announced as the missing type, and the missing type announced as the package.
        var result = Create().CheckType(typeof(IDefaultStatuses).FullName!);

        Assert.Contains($"{typeof(IDefaultStatuses).FullName} is registered in .AddBenzene() from Benzene.Core.MessageHandlers", result);
        Assert.DoesNotContain("Version=", result);
    }

    [Fact]
    public void TheRootCauseWinsOverTheTypeThatWasAskedFor()
    {
        // The failure the maintainer hit: MessageRouter<> resolves as far as its constructor, then
        // IDefaultStatuses turns out to be missing. Preferring the requested type told them to add a
        // call they had already made, and hid the one that would have fixed it.
        var result = Create().Describe(typeof(MessageRouter<object>), ResolveFailure());

        Assert.StartsWith($"{Environment.NewLine}{typeof(IDefaultStatuses).FullName} is registered in .AddBenzene()", result);
        Assert.Contains(".UsingBenzene(x => x.AddBenzene())", result);

        // and the hint that would have sent them in circles is no longer the headline
        Assert.True(
            result.IndexOf(".AddBenzene()", StringComparison.Ordinal)
            < result.IndexOf(".AddMessageHandlers(<assemblies>)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRequestedTypesOwnRegistrationSurvivesAsSecondaryContext()
    {
        // Kept, not dropped: the root-cause branch parses a container's message, and a container whose
        // wording nothing recognises would otherwise leave the developer with no hint at all.
        var result = Create().Describe(typeof(MessageRouter<object>), ResolveFailure());

        var rootCauseAt = result.IndexOf(".AddBenzene()", StringComparison.Ordinal);
        var secondaryAt = result.IndexOf("If that call is already there", StringComparison.Ordinal);

        Assert.True(rootCauseAt >= 0, "the root cause must be reported");
        Assert.True(secondaryAt > rootCauseAt, "the weaker hint must come after the root cause, never before it");
    }

    [Fact]
    public void WithNothingRecognisableInTheChainTheRequestedTypeStillAnswers()
    {
        var result = Create().Describe(typeof(IDefaultStatuses), new Exception("something a container we have never seen said"));

        Assert.Contains(".UsingBenzene(x => x.AddBenzene())", result);
        Assert.DoesNotContain("If that call is already there", result);
    }

    [Fact]
    public void NothingRecognisedAnywhereYieldsNoHintAtAll()
    {
        var result = Create().Describe(typeof(RegistrationCheckTest), new Exception("something a container we have never seen said"));

        Assert.Equal(string.Empty, result);
    }
}
