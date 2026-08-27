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
    public async Task InternallyCreatedLimiter_DisposingTheMiddlewareInstance_DisposesTheLimiter()
    {
        // #200: the internally-created limiter is no longer registered with the DI container - that
        // registration is exactly what collided across sibling pipelines sharing one container (see
        // Extensions.cs's UseInternallyOwnedRateLimiting for the full story), so it was removed.
        // Disposal ownership now lives on the middleware itself (ownsLimiter: true) - proven here by
        // disposing the middleware instance directly (the same thing a caller managing its own
        // lifetime would do) and observing the next message fail CLOSED with the disposed-limiter
        // message (#202), exactly like a caller-disposed BYO limiter already does.
        var serviceCollection = ServiceResolverMother.CreateServiceCollection();
        serviceCollection.UsingBenzene(x => x.AddBenzeneMessage());

        var pipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(
            new MicrosoftBenzeneServiceContainer(serviceCollection));
        pipeline.UseFixedWindowRateLimiting(10, TimeSpan.FromMinutes(1));
        pipeline.UseMessageHandlers();
        var app = new BenzeneMessageApplication(pipeline.Build());
        var resolverFactory = new MicrosoftServiceResolverFactory(serviceCollection.BuildServiceProvider());

        var first = await app.HandleAsync(CreateRequest(), resolverFactory);
        Assert.Equal(BenzeneResultStatus.Ok, first.StatusCode);

        // The rate-limiting middleware is the first item the pipeline builder holds - construct the
        // exact same instance the pipeline would for a message, and dispose it directly, the way a
        // caller managing a RateLimitingMiddleware<TContext> instance's own lifetime would.
        using (var scope = resolverFactory.CreateScope())
        {
            var middlewareFactory = pipeline.GetItems()[0];
            var middleware = Assert.IsType<RateLimitingMiddleware<BenzeneMessageContext>>(middlewareFactory(scope));
            await middleware.DisposeAsync();
        }

        var second = await app.HandleAsync(CreateRequest(), resolverFactory);

        Assert.Equal(BenzeneResultStatus.TooManyRequests, second.StatusCode);
        Assert.Contains("the rate limiter has already been disposed", second.Body, StringComparison.OrdinalIgnoreCase);
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
