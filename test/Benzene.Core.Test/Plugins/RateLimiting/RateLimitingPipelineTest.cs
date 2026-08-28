using System;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.BenzeneMessage;
using Benzene.Core.MessageHandlers.TestHelpers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.RateLimiting;
using Benzene.Results;
using Benzene.Test.Examples;
using Benzene.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Plugins.RateLimiting;

public class RateLimitingPipelineTest
{
    private static (BenzeneMessageApplication App, MicrosoftServiceResolverFactory Resolver) CreateApp(
        Action<MiddlewarePipelineBuilder<BenzeneMessageContext>> configurePipeline)
    {
        var serviceCollection = ServiceResolverMother.CreateServiceCollection();
        serviceCollection.UsingBenzene(x => x.AddBenzeneMessage());

        var pipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(
            new MicrosoftBenzeneServiceContainer(serviceCollection));
        configurePipeline(pipeline);
        pipeline.UseMessageHandlers();

        return (new BenzeneMessageApplication(pipeline.Build()),
            new MicrosoftServiceResolverFactory(serviceCollection.BuildServiceProvider()));
    }

    private static BenzeneMessageRequest CreateRequest(string name = "foo")
    {
        return MessageBuilder.Create(Defaults.Topic, new ExampleRequestPayload
        {
            Id = 42,
            Name = name,
            Mapped = "some-value"
        }).AsBenzeneMessage();
    }

    private static BenzeneMessageRequest CreateRequestWithContentLength(int contentLength)
    {
        return MessageBuilder.Create(Defaults.Topic, new ExampleRequestPayload
            {
                Id = 42,
                Name = "foo",
                Mapped = "some-value"
            })
            .WithHeader("Content-Length", contentLength.ToString())
            .AsBenzeneMessage();
    }

    [Fact]
    public async Task UnderTheLimit_MessagesPassThrough()
    {
        var (app, resolver) = CreateApp(p => p.UseFixedWindowRateLimiting(10, TimeSpan.FromMinutes(1)));

        var response = await app.HandleAsync(CreateRequest(), resolver);

        Assert.Equal(BenzeneResultStatus.Ok, response.StatusCode);
    }

    [Fact]
    public async Task OverTheLimit_ShortCircuitsWithTooManyRequests()
    {
        var (app, resolver) = CreateApp(p => p.UseFixedWindowRateLimiting(1, TimeSpan.FromMinutes(1)));

        var first = await app.HandleAsync(CreateRequest(), resolver);
        var second = await app.HandleAsync(CreateRequest(), resolver);

        Assert.Equal(BenzeneResultStatus.Ok, first.StatusCode);
        Assert.Equal(BenzeneResultStatus.TooManyRequests, second.StatusCode);
        Assert.Contains("Rate limit exceeded", second.Body);
    }

    [Fact]
    public async Task OverTheLimit_SetsRetryAfterHeaderFromTheLease()
    {
        // #137: the limiter supplies RETRY_AFTER metadata on the rejected lease even without
        // queuing - the response header must reflect it, not just the error message text.
        var (app, resolver) = CreateApp(p => p.UseFixedWindowRateLimiting(1, TimeSpan.FromMinutes(1)));

        await app.HandleAsync(CreateRequest(), resolver);
        var second = await app.HandleAsync(CreateRequest(), resolver);

        Assert.Equal(BenzeneResultStatus.TooManyRequests, second.StatusCode);
        Assert.True(second.Headers.TryGetValue("Retry-After", out var retryAfter));
        Assert.True(int.Parse(retryAfter) > 0);
    }

    [Fact]
    public async Task PayloadSizeLimiting_RejectsAPayloadLargerThanTheBucket()
    {
        // The bucket admits at most 32 bytes at once; this payload alone is far bigger, so it can
        // never be granted - rejected outright rather than erroring.
        var (app, resolver) = CreateApp(p => p.UsePayloadSizeRateLimiting(32, 32, TimeSpan.FromMinutes(1)));

        var response = await app.HandleAsync(CreateRequest(new string('x', 200)), resolver);

        Assert.Equal(BenzeneResultStatus.TooManyRequests, response.StatusCode);
        // #142: distinguishable from a normal over-the-limit throttle, not a bare "Rate limit exceeded".
        Assert.Contains("exceeds the limiter's capacity", response.Body);
    }

    [Fact]
    public async Task PayloadSizeLimiting_DeclaredContentLengthOverTheBucket_RejectsWithoutReadingTheBody()
    {
        // #135 partial mitigation: a declared Content-Length over the bucket rejects on the
        // declared size alone - proven here by a request whose ACTUAL body is small (would pass)
        // but whose declared Content-Length is not.
        var (app, resolver) = CreateApp(p => p.UsePayloadSizeRateLimiting(32, 32, TimeSpan.FromMinutes(1)));

        var response = await app.HandleAsync(CreateRequestWithContentLength(999), resolver);

        Assert.Equal(BenzeneResultStatus.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task PayloadSizeLimiting_SpendsTheByteBudget()
    {
        // Budget covers one ~44-byte payload per window but not two: first passes, second rejected.
        var (app, resolver) = CreateApp(p => p.UsePayloadSizeRateLimiting(60, 60, TimeSpan.FromMinutes(1)));

        var first = await app.HandleAsync(CreateRequest(), resolver);
        var second = await app.HandleAsync(CreateRequest(), resolver);

        Assert.Equal(BenzeneResultStatus.Ok, first.StatusCode);
        Assert.Equal(BenzeneResultStatus.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task BringYourOwnLimiter_LeaseIsReleasedAfterEachMessage()
    {
        // A concurrency limiter with a single permit: if the middleware failed to dispose the
        // lease after next(), the second sequential message would be rejected.
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = 1,
            QueueLimit = 0,
        });
        var (app, resolver) = CreateApp(p => p.UseRateLimiting(limiter));

        var first = await app.HandleAsync(CreateRequest(), resolver);
        var second = await app.HandleAsync(CreateRequest(), resolver);

        Assert.Equal(BenzeneResultStatus.Ok, first.StatusCode);
        Assert.Equal(BenzeneResultStatus.Ok, second.StatusCode);
    }

    [Fact]
    public async Task BringYourOwnCost_IsUsedForAcquisition()
    {
        // Every message costs 5 permits against a 9-permit window: the second must be rejected.
        var (app, resolver) = CreateApp(p => p.UseRateLimiting(
            new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = 9,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }),
            (_, _) => 5));

        var first = await app.HandleAsync(CreateRequest(), resolver);
        var second = await app.HandleAsync(CreateRequest(), resolver);

        Assert.Equal(BenzeneResultStatus.Ok, first.StatusCode);
        Assert.Equal(BenzeneResultStatus.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task BringYourOwnCost_NegativeCost_IsRejectedRatherThanSilentlyGranted()
    {
        // #143: a negative cost from a buggy caller-supplied delegate used to be clamped to 0
        // (Math.Max(0, cost)) and always succeed, hiding the bug. It must now be treated as an
        // invalid request and rejected, not silently granted.
        var (app, resolver) = CreateApp(p => p.UseRateLimiting(
            new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }),
            (_, _) => -1));

        var response = await app.HandleAsync(CreateRequest(), resolver);

        Assert.Equal(BenzeneResultStatus.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task BringYourOwnCost_ThrowingDelegate_PropagatesRatherThanBypassingTheLimiter()
    {
        // #143: a cost delegate that throws for reasons other than an invalid/negative cost is a
        // genuine bug, not a rate-limit decision - it must propagate (so the app's own exception
        // handling sees it), not be silently swallowed into a 429 or bypass the limiter entirely.
        var (app, resolver) = CreateApp(p => p.UseRateLimiting(
            new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }),
            (Func<Benzene.Abstractions.DI.IServiceResolver, BenzeneMessageContext, int>)((_, _) =>
                throw new InvalidOperationException("boom"))));

        await Assert.ThrowsAsync<InvalidOperationException>(() => app.HandleAsync(CreateRequest(), resolver));
    }

    [Fact]
    public async Task BringYourOwnLimiter_AlreadyDisposed_FailsClosedInsteadOfCrashing()
    {
        // #134: a caller-disposed BYO limiter must not turn every subsequent message into an
        // unhandled ObjectDisposedException - it fails closed (429), the same as any other denial.
        var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = 1,
            QueueLimit = 0,
        });
        var (app, resolver) = CreateApp(p => p.UseRateLimiting(limiter));
        limiter.Dispose();

        var response = await app.HandleAsync(CreateRequest(), resolver);

        Assert.Equal(BenzeneResultStatus.TooManyRequests, response.StatusCode);
        Assert.Contains("disposed", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BringYourOwnLimiter_AlreadyDisposed_MessageNamesTheLimiterNotTheCostDelegate()
    {
        // #202: before this fix, the SAME message ("the rate limiter has already been disposed")
        // covered both this case (the limiter itself is disposed) and a disposed dependency the cost
        // delegate itself relies on (below) - the two are diagnostically different and must not read
        // the same in a log/response.
        var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = 1,
            QueueLimit = 0,
        });
        var (app, resolver) = CreateApp(p => p.UseRateLimiting(limiter));
        limiter.Dispose();

        var response = await app.HandleAsync(CreateRequest(), resolver);

        Assert.Equal(BenzeneResultStatus.TooManyRequests, response.StatusCode);
        Assert.Contains("the rate limiter has already been disposed", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("permit-cost delegate", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BringYourOwnCost_DelegateThrowsObjectDisposedException_MessageNamesTheCostDelegateNotTheLimiter()
    {
        // #202: the mirror case - a dependency the cost delegate itself depends on (e.g. a scoped
        // resource resolved earlier in the pipeline) being disposed is NOT the same failure as the
        // limiter's own disposal (previous test), and the rejection message must say so, distinctly,
        // rather than misattributing the disposal to the limiter.
        var (app, resolver) = CreateApp(p => p.UseRateLimiting(
            new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }),
            (Func<Benzene.Abstractions.DI.IServiceResolver, BenzeneMessageContext, int>)((_, _) =>
                throw new ObjectDisposedException(nameof(RateLimitingPipelineTest)))));

        var response = await app.HandleAsync(CreateRequest(), resolver);

        Assert.Equal(BenzeneResultStatus.TooManyRequests, response.StatusCode);
        Assert.Contains("permit-cost delegate", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("the rate limiter has already been disposed", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InternallyCreatedLimiter_ReachableViaPublicApi_IsDisposedWhenTheContainerIsDisposed()
    {
        // #249: #200's fix captured the limiter directly in the middleware's closure and made the
        // middleware's own DisposeAsync (ownsLimiter: true) the only disposal path - but
        // MiddlewarePipeline<TContext> builds a fresh middleware instance from the factory on EVERY
        // message and never retains one, and none of the three public UseXRateLimiting methods
        // return any handle to the middleware or the limiter, so no caller using only the documented
        // public API could ever reach that DisposeAsync. This test proves disposal is reachable
        // again, entirely through the public API: build a pipeline via UseFixedWindowRateLimiting
        // alone (no pipeline.GetItems(), no reaching into the builder anywhere below), dispose the DI
        // container/service provider - the caller's ordinary shutdown path, e.g. an ASP.NET Core host
        // disposing its root IServiceProvider - and prove the limiter's Timer is actually torn down.
        //
        // Verification technique mirrors BringYourOwnLimiter_AlreadyDisposed_FailsClosedInsteadOfCrashing
        // above: a disposed RateLimiter throws ObjectDisposedException from AttemptAcquire, which
        // #202's catch turns into a fail-CLOSED TooManyRequests naming the limiter, not a crash and
        // not silent continued acceptance. The two HandleAsync calls share ONE resolver scope opened
        // before the container is disposed (a scope's own lifetime is independent of the root
        // provider it was created from), because the pipeline's own scope-per-message plumbing
        // (BenzeneMessageApplication.HandleAsync) would otherwise open a NEW scope for the second
        // message from an already-disposed root provider and throw ObjectDisposedException there
        // instead - a real host doesn't do that (it disposes the container once, at shutdown, after
        // which it stops sending it new messages entirely), and that's not what this test is
        // measuring: it measures whether the LIMITER a message-processing scope already holds a
        // reference to gets disposed, not whether a brand-new scope can still be opened afterwards.
        var serviceCollection = ServiceResolverMother.CreateServiceCollection();
        serviceCollection.UsingBenzene(x => x.AddBenzeneMessage());

        var pipelineBuilder = new MiddlewarePipelineBuilder<BenzeneMessageContext>(
            new MicrosoftBenzeneServiceContainer(serviceCollection));
        pipelineBuilder.UseFixedWindowRateLimiting(10, TimeSpan.FromMinutes(1));
        pipelineBuilder.UseMessageHandlers();
        var builtPipeline = pipelineBuilder.Build();

        var provider = serviceCollection.BuildServiceProvider();
        var resolverFactory = new MicrosoftServiceResolverFactory(provider);

        // Opened while the container is alive - this is what forces the OwnedRateLimiter factory
        // singleton to actually be constructed (and disposal-tracked) the first time a message flows
        // through (Extensions.cs's UseInternallyOwnedRateLimiting), and it's kept open across the
        // container's disposal below so a second message can still be dispatched afterwards.
        using var scope = resolverFactory.CreateScope();

        var firstContext = new BenzeneMessageContext(CreateRequest());
        await builtPipeline.HandleAsync(firstContext, scope);
        Assert.Equal(BenzeneResultStatus.Ok, firstContext.BenzeneMessageResponse.StatusCode);

        // The caller's ordinary shutdown path. This is the ONLY trigger for disposal in this test -
        // reachable entirely through UseFixedWindowRateLimiting (public API) plus disposing the
        // provider it was registered on.
        await provider.DisposeAsync();

        var secondContext = new BenzeneMessageContext(CreateRequest());
        await builtPipeline.HandleAsync(secondContext, scope);

        Assert.Equal(BenzeneResultStatus.TooManyRequests, secondContext.BenzeneMessageResponse.StatusCode);
        Assert.Contains("the rate limiter has already been disposed", secondContext.BenzeneMessageResponse.Body,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BringYourOwnLimiter_DisposingTheMiddlewareInstance_LeavesTheCallerSuppliedLimiterUntouched()
    {
        // #200's mirror case: a BYO limiter is never owned by the middleware (ownsLimiter: false),
        // so disposing the middleware instance must be a no-op for it - the caller's limiter keeps
        // working, and later messages on the pipeline are unaffected.
        using var limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
        var (app, resolver) = CreateApp(p => p.UseRateLimiting(limiter));

        var first = await app.HandleAsync(CreateRequest(), resolver);
        Assert.Equal(BenzeneResultStatus.Ok, first.StatusCode);

        using (var scope = resolver.CreateScope())
        {
            var middleware = new RateLimitingMiddleware<BenzeneMessageContext>(
                limiter, (_, _) => 1, scope, ownsLimiter: false);
            await middleware.DisposeAsync();
        }

        // Still usable - the middleware never disposed it - and the pipeline still enforces the
        // caller's own limit normally.
        var lease = limiter.AttemptAcquire(1);
        Assert.True(lease.IsAcquired);
        lease.Dispose();

        var second = await app.HandleAsync(CreateRequest(), resolver);
        Assert.Equal(BenzeneResultStatus.Ok, second.StatusCode);
    }

    [Fact]
    public async Task StackingTwoInternallyCreatedLimiters_OnOnePipeline_IsNowLegalAndEachEnforcesItsOwnBudget()
    {
        // #200: two UseXRateLimiting calls on the same pipeline used to fail fast, because both
        // resolved the SAME shared RateLimiter DI registration - the second silently shadowed the
        // first for every message. Direct closure capture makes that structurally impossible: each
        // middleware instance only ever sees the exact limiter its own call created, so stacking is
        // legal, and each limiter enforces its own independent budget.
        var (app, resolver) = CreateApp(p =>
        {
            p.UseFixedWindowRateLimiting(1, TimeSpan.FromMinutes(1)); // outer: 1 message per window
            p.UseTokenBucketRateLimiting(100, 100, TimeSpan.FromMinutes(1)); // inner: generous
        });

        var first = await app.HandleAsync(CreateRequest(), resolver);
        var second = await app.HandleAsync(CreateRequest(), resolver);

        Assert.Equal(BenzeneResultStatus.Ok, first.StatusCode);
        // Rejected by the FIRST (fixed-window, limit 1) limiter, not shadowed by the second.
        Assert.Equal(BenzeneResultStatus.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task SiblingPipelinesSharingOneContainer_CanEachCallUseFixedWindowRateLimiting_Independently()
    {
        // #200: two sibling pipelines built off the SAME IBenzeneServiceContainer (this framework's
        // supported multi-transport pattern - several transport pipelines sharing one container) used
        // to collide under the old DI-registration-keyed-on-RateLimiter approach, even though they
        // are two entirely separate pipelines with no reason to share a budget. Proven here: each
        // gets its own independent limiter, so exhausting one's budget never affects the other's.
        var serviceCollection = ServiceResolverMother.CreateServiceCollection();
        serviceCollection.UsingBenzene(x => x.AddBenzeneMessage());
        var container = new MicrosoftBenzeneServiceContainer(serviceCollection);

        var pipelineA = new MiddlewarePipelineBuilder<BenzeneMessageContext>(container);
        pipelineA.UseFixedWindowRateLimiting(1, TimeSpan.FromMinutes(1));
        pipelineA.UseMessageHandlers();
        var appA = new BenzeneMessageApplication(pipelineA.Build());

        var pipelineB = pipelineA.Create<BenzeneMessageContext>();
        pipelineB.UseFixedWindowRateLimiting(1, TimeSpan.FromMinutes(1));
        pipelineB.UseMessageHandlers();
        var appB = new BenzeneMessageApplication(pipelineB.Build());

        var resolverFactory = new MicrosoftServiceResolverFactory(serviceCollection.BuildServiceProvider());

        var firstA = await appA.HandleAsync(CreateRequest(), resolverFactory);
        var secondA = await appA.HandleAsync(CreateRequest(), resolverFactory);
        var firstB = await appB.HandleAsync(CreateRequest(), resolverFactory);

        Assert.Equal(BenzeneResultStatus.Ok, firstA.StatusCode);
        Assert.Equal(BenzeneResultStatus.TooManyRequests, secondA.StatusCode); // A's own budget exhausted
        Assert.Equal(BenzeneResultStatus.Ok, firstB.StatusCode); // B has its own, unaffected budget
    }

    [Fact]
    public async Task PartitionedLimiter_OneAbusivePartition_DoesNotStarveTheOther()
    {
        // #136: partitioned rate limiting so one caller can't exhaust the whole budget for every
        // other caller. Partition key here is the message's Name field, standing in for e.g. a
        // tenant/API-key claim in a real transport.
        var partitioned = PartitionedRateLimiter.Create<BenzeneMessageContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                GetPartitionKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 1,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));

        var (app, resolver) = CreateApp(p => p.UsePartitionedRateLimiting(partitioned, GetPartitionKey));

        var abuserFirst = await app.HandleAsync(CreateRequestWithPartitionKey("abuser"), resolver);
        var abuserSecond = await app.HandleAsync(CreateRequestWithPartitionKey("abuser"), resolver);
        var victim = await app.HandleAsync(CreateRequestWithPartitionKey("victim"), resolver);

        Assert.Equal(BenzeneResultStatus.Ok, abuserFirst.StatusCode);
        Assert.Equal(BenzeneResultStatus.TooManyRequests, abuserSecond.StatusCode);
        Assert.Equal(BenzeneResultStatus.Ok, victim.StatusCode);

        partitioned.Dispose();
    }

    private static BenzeneMessageRequest CreateRequestWithPartitionKey(string partitionKey)
    {
        return MessageBuilder.Create(Defaults.Topic, new ExampleRequestPayload
            {
                Id = 42,
                Name = "foo",
                Mapped = "some-value"
            })
            .WithHeader("X-Partition-Key", partitionKey)
            .AsBenzeneMessage();
    }

    private static string GetPartitionKey(BenzeneMessageContext context)
    {
        return context.BenzeneMessageRequest.Headers.TryGetValue("X-Partition-Key", out var key) ? key : "default";
    }
}
