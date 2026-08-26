using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.MessageHandlers.Request;
using Benzene.Abstractions.Messages;
using Benzene.Core.MessageHandlers;
using Benzene.Core.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Benzene.Test.Core.Core.MessageHandling;

/// <summary>
/// Coverage for <see cref="MessageRouter{TContext}"/>'s topic-resolution contract: it routes on
/// exactly the <see cref="ITopic"/> its <see cref="IMessageGetter{TContext}"/> returns from
/// <see cref="IMessageGetter{TContext}.GetTopic"/>, with no augmentation of its own.
/// </summary>
/// <remarks>
/// Before task #98 (work/archive/bug-fix-designs-round10-2026-08.md WP-V) the router combined the topic id
/// from <see cref="IMessageTopicGetter{TContext}"/> with a separately-injected
/// <see cref="IMessageVersionGetter{TContext}"/> itself, via the shared <c>GetVersionedTopic</c>
/// helper, and threw the joined result away instead of caching it - so every other reader of the
/// topic saw a version-blind answer. That join now happens once, inside
/// <see cref="IMessageGetter{TContext}.GetTopic"/> itself (see
/// <see cref="Benzene.Test.Core.Core.MessageHandling.MessageGetterVersionJoinTest"/> for coverage of
/// the join/cache behaviour), so the router no longer takes an
/// <see cref="IMessageVersionGetter{TContext}"/> dependency at all - it just has to forward whatever
/// topic the getter hands it, unchanged, to <see cref="IMessageHandlerDefinitionLookUp.FindHandler"/>.
/// That pass-through contract is what these tests cover.
/// </remarks>
public class MessageRouterVersionWiringTest
{
    // Public (not private) because Moq needs to build a dynamic proxy for the closed generic
    // interfaces (IMessageGetter<TestContext>, etc.), which requires the type argument to be
    // accessible - see BenzeneMessageHttpMiddlewareTest for the same convention.
    public class TestContext
    {
    }

    private static (MessageRouter<TestContext> Router, Mock<IMessageHandlerDefinitionLookUp> LookUp) CreateRouter(
        ITopic? topicFromGetter)
    {
        var messageGetter = new Mock<IMessageGetter<TestContext>>();
        messageGetter.Setup(x => x.GetTopic(It.IsAny<TestContext>())).Returns(topicFromGetter);

        var lookUp = new Mock<IMessageHandlerDefinitionLookUp>();
        lookUp.Setup(x => x.FindHandler(It.IsAny<ITopic>())).Returns((IMessageHandlerDefinition?)null);

        var defaultStatuses = new Mock<IDefaultStatuses>();
        defaultStatuses.SetupGet(x => x.NotFound).Returns("not-found");
        defaultStatuses.SetupGet(x => x.ValidationError).Returns("validation-error");

        var router = new MessageRouter<TestContext>(
            Mock.Of<IMessageHandlerFactory>(),
            messageGetter.Object,
            lookUp.Object,
            Mock.Of<IRequestMapper<TestContext>>(),
            Mock.Of<IMessageHandlerResultSetter<TestContext>>(),
            defaultStatuses.Object,
            NullLogger<MessageRouter<TestContext>>.Instance);

        return (router, lookUp);
    }

    [Fact]
    public async Task HandleAsync_GetterReturnsAVersionedTopic_LooksUpExactlyThatTopic()
    {
        // The getter already did any version-joining (task #98) - the router must not re-derive or
        // discard the version.
        var (router, lookUp) = CreateRouter(new Topic("order:create", "v1"));

        await router.HandleAsync(new TestContext(), () => Task.CompletedTask);

        lookUp.Verify(x => x.FindHandler(It.Is<ITopic>(t => t.Id == "order:create" && t.Version == "v1")), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_GetterReturnsAnUnversionedTopic_LooksUpItUnversioned()
    {
        var (router, lookUp) = CreateRouter(new Topic("order:create"));

        await router.HandleAsync(new TestContext(), () => Task.CompletedTask);

        lookUp.Verify(x => x.FindHandler(It.Is<ITopic>(t => t.Id == "order:create" && t.Version == "")), Times.Once);
    }
}
