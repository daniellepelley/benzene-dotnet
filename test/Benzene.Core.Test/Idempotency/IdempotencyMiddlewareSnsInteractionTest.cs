using System.Linq;
using System.Threading.Tasks;
using Amazon.Lambda.SNSEvents;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Aws.Lambda.Sns;
using Benzene.Core.Exceptions;
using Benzene.Core.Middleware;
using Benzene.Idempotency;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Benzene.Test.Idempotency;

/// <summary>
/// #260: <see cref="IdempotencyMiddleware{TContext}"/>'s "null MessageResult == success" fall-through
/// directly contradicted the "null == failure, redeliver" convention SQS/DynamoDb always had and #229
/// extended to SNS/S3/EventBridge. A pipeline consisting of ONLY <see cref="IdempotencyMiddleware{TContext}"/>
/// around a handler that never sets <see cref="SnsRecordContext.MessageResult"/> proved the interaction:
/// the transport's own escalation (#229) correctly throws for redelivery on attempt 1, but the
/// idempotency claim had already been marked Completed inside that same call - so the redelivery SNS
/// was just told to perform short-circuits as a duplicate success without the handler ever re-running.
/// </summary>
public class IdempotencyMiddlewareSnsInteractionTest
{
    private class CountingHandlerMiddleware : IMiddleware<SnsRecordContext>
    {
        public int Calls { get; private set; }

        public string Name => nameof(CountingHandlerMiddleware);

        public Task HandleAsync(SnsRecordContext context, System.Func<Task> next)
        {
            Calls++;
            // Deliberately never sets context.MessageResult - the exact "non-standard pipeline that
            // omits MessageRouter or short-circuits before it runs" edge case #229's own doc comment
            // calls out.
            return Task.CompletedTask;
        }
    }

    private static SNSEvent CreateEvent(string messageId)
    {
        return new SNSEvent
        {
            Records = new[]
            {
                new SNSEvent.SNSRecord
                {
                    EventSource = "aws:sns",
                    Sns = new SNSEvent.SNSMessage { MessageId = messageId, Message = "body" }
                }
            }
        };
    }

    private static (Mock<IServiceResolver> Resolver, Mock<IServiceResolverFactory> ResolverFactory) CreateResolver()
    {
        var mockLogger = new Mock<ILogger<SnsApplication>>();
        var mockResolver = new Mock<IServiceResolver>();
        mockResolver.Setup(x => x.GetService<ISetCurrentTransport>()).Returns(Mock.Of<ISetCurrentTransport>());
        mockResolver.Setup(x => x.GetService<ILogger<SnsApplication>>()).Returns(mockLogger.Object);
        var mockResolverFactory = new Mock<IServiceResolverFactory>();
        mockResolverFactory.Setup(x => x.CreateScope()).Returns(mockResolver.Object);
        return (mockResolver, mockResolverFactory);
    }

    [Fact]
    public async Task NullResultPipeline_FirstAttemptReleasesClaim_SoRedeliveryActuallyReprocesses()
    {
        var store = new InMemoryIdempotencyStore();
        var handler = new CountingHandlerMiddleware();

        var pipeline = new MiddlewarePipeline<SnsRecordContext>(new System.Func<IServiceResolver, IMiddleware<SnsRecordContext>>[]
        {
            _ => new IdempotencyMiddleware<SnsRecordContext>(
                store,
                new FixedSnsKeyStrategy("key-1"),
                new IdempotencyOptions()),
            _ => handler,
        });

        var (_, resolverFactory) = CreateResolver();
        var application = new SnsApplication(pipeline);

        // First attempt: the transport's own #229 escalation must still see the null result and
        // demand redelivery.
        await Assert.ThrowsAsync<SnsMessageProcessingException>(
            () => application.HandleAsync(CreateEvent("msg-1"), resolverFactory.Object));

        // Before the fix: the claim was already Completed here, so the handler never runs again.
        // After the fix: the claim was released, so the redelivery gets a fresh claim.
        var reclaim = await store.TryClaimAsync("key-1");
        Assert.True(reclaim.Claimed);

        // The redelivery SNS was just told to perform: this must actually re-run the handler, not
        // short-circuit as a duplicate success.
        await store.ReleaseAsync("key-1", reclaim.ClaimToken!);
        await Assert.ThrowsAsync<SnsMessageProcessingException>(
            () => application.HandleAsync(CreateEvent("msg-1"), resolverFactory.Object));

        Assert.Equal(2, handler.Calls);
    }

    private class FixedSnsKeyStrategy : IIdempotencyKeyStrategy<SnsRecordContext>
    {
        private readonly string _key;
        public FixedSnsKeyStrategy(string key) => _key = key;
        public string? GetKey(SnsRecordContext context) => _key;
    }
}
