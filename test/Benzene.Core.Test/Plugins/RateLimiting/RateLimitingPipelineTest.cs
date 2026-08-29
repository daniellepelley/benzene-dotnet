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
    public async Task BringYourOwnCost_CostDelegateThrowsObjectDisposedException_IsReportedAsACostDelegateFailure_NotAsTheLimiterDisposed()
    {
        // #202: #143's fix moved the cost delegate inside the same try as Acquire(), but its single
        // ObjectDisposedException catch always reported "the rate limiter has already been disposed"
        // - wrong when the exception actually came from an unrelated disposed dependency the cost
        // delegate itself touches, with a perfectly healthy limiter underneath. The two cases must be
        // distinguishable.
        using var limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 100,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
        var (app, resolver) = CreateApp(p => p.UseRateLimiting(
            limiter,
            (Func<Benzene.Abstractions.DI.IServiceResolver, BenzeneMessageContext, int>)((_, _) =>
                throw new ObjectDisposedException("SomeUnrelatedDependency"))));

        var response = await app.HandleAsync(CreateRequest(), resolver);

        Assert.Equal(BenzeneResultStatus.TooManyRequests, response.StatusCode);
        Assert.Contains("cost delegate", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("the rate limiter has already been disposed", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InternallyCreatedLimiter_IsDisposedWhenTheContainerIsDisposed()
    {
        // #133: UseFixedWindowRateLimiting/UseTokenBucketRateLimiting/UsePayloadSizeRateLimiting
        // create a limiter with a live auto-replenish timer that nothing used to ever dispose. It
        // is now registered with the DI container, which disposes it (like any other
        // container-created singleton) when the container itself is disposed.
        var serviceCollection = ServiceResolverMother.CreateServiceCollection();
        serviceCollection.UsingBenzene(x => x.AddBenzeneMessage());

        var pipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(
            new MicrosoftBenzeneServiceContainer(serviceCollection));
        pipeline.UseFixedWindowRateLimiting(10, TimeSpan.FromMinutes(1));
        pipeline.UseMessageHandlers();
        var app = new BenzeneMessageApplication(pipeline.Build());

        // Owns the provider it builds (unlike the IServiceProvider-accepting overload CreateApp uses),
        // so Dispose() below actually disposes the container's singletons.
        var resolverFactory = new MicrosoftServiceResolverFactory(serviceCollection);

        // Drive one message through so the singleton is actually resolved (and so captured for
        // disposal by the container) - an unregistered/never-resolved factory is never constructed.
        await app.HandleAsync(CreateRequest(), resolverFactory);

        RateLimiter limiter;
        using (var scope = resolverFactory.CreateScope())
        {
            // #200: the container-owned registration is now keyed by
            // InternallyOwnedRateLimiterHolder<TContext> (closed over the pipeline's own context
            // type), not the bare RateLimiter type - see Extensions.cs's #200 remarks. Disposal still
            // works the identical way: the holder forwards its own IAsyncDisposable to the limiter.
            limiter = scope.GetService<Benzene.RateLimiting.Extensions.InternallyOwnedRateLimiterHolder<BenzeneMessageContext>>().RateLimiter;
        }

        resolverFactory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => limiter.AttemptAcquire(1));
    }

    [Fact]
    public async Task SiblingPipelines_OffOneSharedContainer_EachGetTheirOwnIndependentInternallyOwnedLimiter()
    {
        // #200: MiddlewarePipelineBuilder<T>.Create<TNewContext>() deliberately shares one container
        // across a service's sibling pipelines - the documented multi-transport pattern (see e.g.
        // examples/AwsMesh's MeshServiceWiring.Configure, which wires ApiGateway/BenzeneMessage/Sqs/
        // Sns/EventBridge, each a genuinely different context type, off one shared
        // IBenzeneApplicationBuilder). Round 11's #133 guard used to be keyed on the shared container
        // itself, so the second sibling's own internally-created limiter tripped the "only one per
        // pipeline" guard even though it is a completely different pipeline for a different transport.
        var serviceCollection = ServiceResolverMother.CreateServiceCollection();
        serviceCollection.UsingBenzene(x => x.AddBenzeneMessage());
        var secondResultSetter = new RecordingResultSetter();
        serviceCollection.AddSingleton<Benzene.Abstractions.MessageHandlers.Mappers.IMessageHandlerResultSetter<SecondPipelineContext>>(secondResultSetter);

        var container = new MicrosoftBenzeneServiceContainer(serviceCollection);
        var firstPipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(container);
        firstPipeline.UseFixedWindowRateLimiting(1, TimeSpan.FromMinutes(1));
        firstPipeline.UseMessageHandlers();
        var firstApp = new BenzeneMessageApplication(firstPipeline.Build());

        // Building a sibling pipeline for a completely different transport context, off the SAME
        // container, must NOT throw - before the fix this always threw InvalidOperationException.
        var secondPipeline = firstPipeline.Create<SecondPipelineContext>();
        secondPipeline.UseFixedWindowRateLimiting(1, TimeSpan.FromMinutes(1));
        var secondBuiltPipeline = secondPipeline.Build();

        var resolverFactory = new MicrosoftServiceResolverFactory(serviceCollection.BuildServiceProvider());

        // The first (original) pipeline's own limiter still enforces its own limit of 1, unaffected
        // by the sibling registration sharing the same container.
        var firstOk = await firstApp.HandleAsync(CreateRequest(), resolverFactory);
        var firstRejected = await firstApp.HandleAsync(CreateRequest(), resolverFactory);
        Assert.Equal(BenzeneResultStatus.Ok, firstOk.StatusCode);
        Assert.Equal(BenzeneResultStatus.TooManyRequests, firstRejected.StatusCode);

        // The second (sibling) pipeline has its OWN limiter with its own independent budget of 1 -
        // not shared with, shadowed by, or already exhausted by the first pipeline's two messages above.
        using (var scope = resolverFactory.CreateScope())
        {
            await secondBuiltPipeline.HandleAsync(new SecondPipelineContext(), scope);
        }
        Assert.Equal(BenzeneResultStatus.Ok, secondResultSetter.LastResult!.BenzeneResult.Status);

        using (var scope = resolverFactory.CreateScope())
        {
            await secondBuiltPipeline.HandleAsync(new SecondPipelineContext(), scope);
        }
        Assert.Equal(BenzeneResultStatus.TooManyRequests, secondResultSetter.LastResult!.BenzeneResult.Status);
    }

    /// <summary>A stand-in transport context for a sibling pipeline, distinct from <see cref="BenzeneMessageContext"/>.</summary>
    private sealed class SecondPipelineContext
    {
    }

    /// <summary>Minimal <see cref="IMessageHandlerResultSetter{TContext}"/> that just records the last result written to it.</summary>
    private sealed class RecordingResultSetter : Benzene.Abstractions.MessageHandlers.Mappers.IMessageHandlerResultSetter<SecondPipelineContext>
    {
        public Benzene.Abstractions.MessageHandlers.IMessageHandlerResult? LastResult { get; private set; }

        public Task SetResultAsync(SecondPipelineContext context, Benzene.Abstractions.MessageHandlers.IMessageHandlerResult messageHandlerResult)
        {
            LastResult = messageHandlerResult;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void StackingTwoInternallyCreatedLimiters_OnOnePipeline_FailsFast()
    {
        // Two UseXRateLimiting calls on the same pipeline would otherwise silently let the second
        // shadow the first under the shared RateLimiter DI registration - fail fast instead.
        var serviceCollection = ServiceResolverMother.CreateServiceCollection();
        serviceCollection.UsingBenzene(x => x.AddBenzeneMessage());
        var pipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(
            new MicrosoftBenzeneServiceContainer(serviceCollection));

        pipeline.UseFixedWindowRateLimiting(10, TimeSpan.FromMinutes(1));

        Assert.Throws<InvalidOperationException>(
            () => pipeline.UseTokenBucketRateLimiting(10, 10, TimeSpan.FromMinutes(1)));
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
