using System.Collections.Generic;
using Benzene.Abstractions.Messages;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Core.Messages;
using Benzene.Core.MessageHandlers;
using Xunit;

namespace Benzene.Test.Core.Core.MessageHandling;

/// <summary>
/// Coverage for the version-join half of <see cref="MessageGetter{TContext}.GetTopic"/> (task #98,
/// work/bug-fix-designs-round10-2026-08.md WP-V): the facade now combines the topic getter's answer
/// with the optionally-injected <see cref="IMessageVersionGetter{TContext}"/> itself, and caches the
/// JOINED result, so every consumer of <c>IMessageGetter&lt;TContext&gt;.GetTopic</c> - not just
/// <see cref="MessageRouter{TContext}"/> - sees a version-resolved topic. Before this fix, only the
/// router performed this join (locally, discarding the result); <c>UseMeshTrace</c> and other readers
/// of <c>GetTopic</c> saw <c>Version</c> permanently empty for a header-versioned message.
/// </summary>
public class MessageGetterVersionJoinTest
{
    private class TestContext
    {
    }

    private class CountingTopicGetter : IMessageTopicGetter<TestContext>
    {
        private readonly ITopic? _topic;

        public CountingTopicGetter(ITopic? topic) => _topic = topic;

        public int Calls { get; private set; }

        public ITopic? GetTopic(TestContext context)
        {
            Calls++;
            return _topic;
        }
    }

    private class CountingVersionGetter : IMessageVersionGetter<TestContext>
    {
        private readonly string? _version;

        public CountingVersionGetter(string? version) => _version = version;

        public int Calls { get; private set; }

        public string? GetVersion(TestContext context)
        {
            Calls++;
            return _version;
        }
    }

    private class StubBodyGetter : IMessageBodyGetter<TestContext>
    {
        public string? GetBody(TestContext context) => null;
    }

    private class StubHeadersGetter : IMessageHeadersGetter<TestContext>
    {
        public IDictionary<string, string> GetHeaders(TestContext context) => new Dictionary<string, string>();
    }

    private static MessageGetter<TestContext> Getter(
        CountingTopicGetter topicGetter,
        CountingVersionGetter? versionGetter,
        ResolvedTopicCache<TestContext>? cache) =>
        new(topicGetter, new StubBodyGetter(), new StubHeadersGetter(), cache, versionGetter);

    [Fact]
    public void GetTopic_VersionGetterRegistered_JoinsTheVersionIntoTheTopic()
    {
        var topicGetter = new CountingTopicGetter(new Topic("order:create"));
        var versionGetter = new CountingVersionGetter("v3");
        var getter = Getter(topicGetter, versionGetter, new ResolvedTopicCache<TestContext>());

        var topic = getter.GetTopic(new TestContext());

        Assert.Equal("order:create", topic.Id);
        Assert.Equal("v3", topic.Version);
    }

    [Fact]
    public void GetTopic_VersionGetterRegistered_ResultIsCached_InnerGettersInvokedOnce()
    {
        var topicGetter = new CountingTopicGetter(new Topic("order:create"));
        var versionGetter = new CountingVersionGetter("v3");
        var getter = Getter(topicGetter, versionGetter, new ResolvedTopicCache<TestContext>());

        var first = getter.GetTopic(new TestContext());
        var second = getter.GetTopic(new TestContext());
        var third = getter.GetTopic(new TestContext());

        Assert.Equal(1, topicGetter.Calls);
        Assert.Equal(1, versionGetter.Calls);
        Assert.All(new[] { first, second, third }, t =>
        {
            Assert.Equal("order:create", t.Id);
            Assert.Equal("v3", t.Version);
        });
    }

    [Fact]
    public void GetTopic_NoVersionGetterRegistered_ReturnsTheVersionlessTopic_DoesNotThrow()
    {
        var topicGetter = new CountingTopicGetter(new Topic("order:create"));
        var getter = Getter(topicGetter, versionGetter: null, new ResolvedTopicCache<TestContext>());

        var topic = getter.GetTopic(new TestContext());

        Assert.Equal("order:create", topic.Id);
        Assert.Equal(string.Empty, topic.Version);
    }

    [Fact]
    public void GetTopic_TopicGetterAlreadySuppliedAVersion_PresetWinsOverVersionGetter()
    {
        // An explicit preset (e.g. UsePresetTopic(topicId, version)) is a deliberate override; the
        // message's own version signal must not replace it.
        var topicGetter = new CountingTopicGetter(new Topic("order:create", "preset-version"));
        var versionGetter = new CountingVersionGetter("v3");
        var getter = Getter(topicGetter, versionGetter, new ResolvedTopicCache<TestContext>());

        var topic = getter.GetTopic(new TestContext());

        Assert.Equal("preset-version", topic.Version);
        Assert.Equal(0, versionGetter.Calls);
    }

    [Fact]
    public void GetTopic_TopicIdMissing_NeverConsultsTheVersionGetter()
    {
        var topicGetter = new CountingTopicGetter(topic: null);
        var versionGetter = new CountingVersionGetter("v3");
        var getter = Getter(topicGetter, versionGetter, new ResolvedTopicCache<TestContext>());

        var topic = getter.GetTopic(new TestContext());

        Assert.Null(topic);
        Assert.Equal(0, versionGetter.Calls);
    }

    [Fact]
    public void GetTopic_WithoutCache_StillJoinsTheVersion_OnEveryCall()
    {
        var topicGetter = new CountingTopicGetter(new Topic("order:create"));
        var versionGetter = new CountingVersionGetter("v3");
        var getter = Getter(topicGetter, versionGetter, cache: null);

        getter.GetTopic(new TestContext());
        var topic = getter.GetTopic(new TestContext());

        Assert.Equal("v3", topic.Version);
        Assert.Equal(2, topicGetter.Calls);
        Assert.Equal(2, versionGetter.Calls);
    }
}
