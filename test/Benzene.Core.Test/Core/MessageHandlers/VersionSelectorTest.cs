using System;
using System.Collections.Generic;
using System.Linq;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Core.MessageHandlers;
using Benzene.Core.Messages;
using Xunit;

namespace Benzene.Test.Core.MessageHandlers;

/// <summary>
/// The selection rule of docs/specification/core-concepts.md §2: exact version, else a handler the
/// version casts to (default wins a tie, else a documented deterministic tie-break), else not-found; no
/// version goes to the default handler (unversioned, else declared default), else not-found.
/// </summary>
public class VersionSelectorTest
{
    private class V1Request { }
    private class V2Request { }
    private class V3Request { }
    private class Handler { }

    private const string Topic = "order:create";

    private static IMessageHandlerDefinition Def(string version, Type requestType, bool isDefault = false) =>
        MessageHandlerDefinition.CreateInstance(Topic, version, requestType, typeof(object), typeof(Handler), isDefault);

    /// <summary>Casts as (from version -> request type) pairs.</summary>
    private class FakeCasts : IPayloadVersionCasts
    {
        private readonly (string From, Type To)[] _casts;

        public FakeCasts(params (string From, Type To)[] casts) => _casts = casts;

        public bool CanCast(string topicId, string fromVersion, Type requestType) =>
            topicId == Topic && _casts.Any(x => x.From == fromVersion && x.To == requestType);

        public IReadOnlyCollection<string> GetVersions(string topicId) =>
            _casts.Select(x => x.From).Distinct().ToArray();
    }

    [Fact]
    public void ExactVersion_IsSelected()
    {
        var v1 = Def("v1", typeof(V1Request));
        var v2 = Def("v2", typeof(V2Request));

        Assert.Same(v2, new VersionSelector().Select(new Topic(Topic, "v2"), new[] { v1, v2 }));
    }

    [Fact]
    public void UnknownVersion_IsNotFound_NeverTheHighestAvailable()
    {
        var v1 = Def("v1", typeof(V1Request));
        var v2 = Def("v2", typeof(V2Request));

        Assert.Null(new VersionSelector().Select(new Topic(Topic, "v9"), new[] { v1, v2 }));
    }

    [Fact]
    public void AVersion_SentToAnUnversionedOnlyTopic_IsNotFound()
    {
        // Used to route to the topic's only handler regardless of the version named.
        var unversioned = Def("", typeof(V1Request));

        Assert.Null(new VersionSelector().Select(new Topic(Topic, "v2"), new[] { unversioned }));
    }

    [Fact]
    public void NoVersion_GoesToTheUnversionedHandler()
    {
        var unversioned = Def("", typeof(V1Request));
        var v2 = Def("v2", typeof(V2Request), isDefault: true);

        Assert.Same(unversioned, new VersionSelector().Select(new Topic(Topic), new[] { v2, unversioned }));
    }

    [Fact]
    public void NoVersion_GoesToTheDeclaredDefault_WhenNoUnversionedHandler()
    {
        var v1 = Def("v1", typeof(V1Request), isDefault: true);
        var v2 = Def("v2", typeof(V2Request));

        Assert.Same(v1, new VersionSelector().Select(new Topic(Topic), new[] { v1, v2 }));
    }

    [Fact]
    public void NoVersion_NoDefault_IsNotFound()
    {
        var v1 = Def("v1", typeof(V1Request));
        var v2 = Def("v2", typeof(V2Request));

        Assert.Null(new VersionSelector().Select(new Topic(Topic), new[] { v1, v2 }));
    }

    [Fact]
    public void AVersionWithNoHandler_GoesToTheHandlerItCastsTo()
    {
        var unversioned = Def("", typeof(V3Request));
        var casts = new FakeCasts(("v1", typeof(V3Request)));

        Assert.Same(unversioned, new VersionSelector(casts).Select(new Topic(Topic, "v1"), new[] { unversioned }));
    }

    [Fact]
    public void ExactMatch_WinsOverACast()
    {
        var v1 = Def("v1", typeof(V1Request));
        var v2 = Def("v2", typeof(V2Request));
        var casts = new FakeCasts(("v1", typeof(V2Request)));

        Assert.Same(v1, new VersionSelector(casts).Select(new Topic(Topic, "v1"), new[] { v1, v2 }));
    }

    [Fact]
    public void WithoutCasts_AVersionWithNoHandlerIsNotFound()
    {
        var unversioned = Def("", typeof(V3Request));

        Assert.Null(new VersionSelector(null).Select(new Topic(Topic, "v1"), new[] { unversioned }));
    }

    [Fact]
    public void CastToSeveralHandlers_TheDefaultWins()
    {
        var v2 = Def("v2", typeof(V2Request), isDefault: true);
        var v3 = Def("v3", typeof(V3Request));
        var casts = new FakeCasts(("v1", typeof(V2Request)), ("v1", typeof(V3Request)));

        Assert.Same(v2, new VersionSelector(casts).Select(new Topic(Topic, "v1"), new[] { v3, v2 }));
    }

    [Fact]
    public void CastToSeveralHandlers_DefaultNotAmongThem_TieBreakIsTheGreatestVersion()
    {
        var unversionedDefault = Def("", typeof(V1Request));
        var v2 = Def("v2", typeof(V2Request));
        var v3 = Def("v3", typeof(V3Request));
        var casts = new FakeCasts(("v0", typeof(V2Request)), ("v0", typeof(V3Request)));

        // Deterministic whatever the registration order.
        Assert.Same(v3, new VersionSelector(casts).Select(new Topic(Topic, "v0"), new[] { v3, v2, unversionedDefault }));
        Assert.Same(v3, new VersionSelector(casts).Select(new Topic(Topic, "v0"), new[] { unversionedDefault, v2, v3 }));
    }

    [Fact]
    public void AnUnversionedHandler_IsACastTargetLikeAnyOther()
    {
        var unversioned = Def("", typeof(V2Request));
        var v3 = Def("v3", typeof(V3Request), isDefault: false);
        var casts = new FakeCasts(("v1", typeof(V2Request)));

        Assert.Same(unversioned, new VersionSelector(casts).Select(new Topic(Topic, "v1"), new[] { v3, unversioned }));
    }

    [Fact]
    public void HighestAvailable_IsAnExplicitOptIn_KeepingTheOldOrdinalFallback()
    {
        // Ordinal max of ["a","B"] is "a" (0x61 > 0x42). The default culture-sensitive comparer would
        // pick "B" under most cultures - it must not vary by machine culture.
        var a = Def("a", typeof(V1Request));
        var b = Def("B", typeof(V2Request));
        var selector = new HighestAvailableVersionSelector();

        Assert.Same(b, selector.Select(new Topic(Topic, "B"), new[] { a, b }));
        Assert.Same(a, selector.Select(new Topic(Topic, "missing"), new[] { a, b }));
        Assert.Same(a, selector.Select(new Topic(Topic), new[] { a, b }));
    }
}
