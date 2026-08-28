using System;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Clients;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Test.Clients;

public class ParallelOutboundTest
{
    private static IBenzeneMessageSender SenderFor(Action<OutboundRoutingBuilder> configure)
        => SenderFor(configure, ambientCancellationToken: null);

    /// <summary>
    /// As the parameterless overload, but also seeds a fixed <see cref="ICancellationTokenAccessor"/>
    /// singleton reporting <paramref name="ambientCancellationToken"/> - the seam
    /// <c>ParallelOutboundMiddleware</c> resolves to distinguish "the ambient token actually fired"
    /// (#269) from an unrelated <see cref="OperationCanceledException"/>.
    /// </summary>
    private static IBenzeneMessageSender SenderFor(Action<OutboundRoutingBuilder> configure, CancellationToken? ambientCancellationToken)
    {
        var services = new ServiceCollection();
        if (ambientCancellationToken is { } token)
        {
            services.AddSingleton<ICancellationTokenAccessor>(new FixedCancellationTokenAccessor(token));
        }

        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddOutboundRouting(configure);
        var resolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
        return resolver.GetService<IBenzeneMessageSender>();
    }

    private sealed class FixedCancellationTokenAccessor : ICancellationTokenAccessor
    {
        public FixedCancellationTokenAccessor(CancellationToken cancellationToken) => CancellationToken = cancellationToken;

        public CancellationToken CancellationToken { get; }
    }

    [Fact]
    public async Task UseParallel_RunsBranchesConcurrently_NotOneAfterAnother()
    {
        var live = 0;
        var maxLive = 0;
        var gate = new object();
        // A rendezvous both branches must reach before either may finish: it can only be satisfied
        // if the two branches are in flight at the same time. This proves concurrency
        // deterministically - no wall-clock timing, so it can't flake under CPU load (unlike an
        // "elapsed < Nms" assertion, where a loaded thread pool delays Task resumption). A sequential
        // run would leave the first branch waiting alone; it releases only on the bounded timeout, by
        // which point the branches never overlapped, so maxLive stays 1 and the test fails.
        using var bothInFlight = new Barrier(2);

        Func<IServiceResolver, Func<OutboundContext, Func<Task>, Task>> branch = _ => async (ctx, _) =>
        {
            lock (gate) { maxLive = Math.Max(maxLive, ++live); }
            await Task.Yield(); // ensure each branch's continuation runs on its own thread-pool thread
            bothInFlight.SignalAndWait(TimeSpan.FromSeconds(10));
            lock (gate) { live--; }
            ctx.Response = BenzeneResult.Ok<Void>();
        };

        var sender = SenderFor(routing => routing.Route("order:create", p => p.UseParallel(
            ("a", b => b.Use("a", branch)),
            ("b", b => b.Use("b", branch)))));

        var result = await sender.SendAsync<string, Void>("order:create", "payload");

        Assert.True(result.IsSuccessful);
        Assert.Equal(2, maxLive); // both branches were in flight at once (the barrier could release)
    }

    [Fact]
    public async Task UseParallel_AllBranchesSucceed_AggregatesToSuccess()
    {
        var sender = SenderFor(routing => routing.Route("order:create", p => p.UseParallel(
            ("sqs", b => b.OnRequest(ctx => ctx.Response = BenzeneResult.Ok<Void>())),
            ("sns", b => b.OnRequest(ctx => ctx.Response = BenzeneResult.Ok<Void>())))));

        var result = await sender.SendAsync<string, Void>("order:create", "payload");

        Assert.True(result.IsSuccessful);
    }

    [Fact]
    public async Task UseParallel_OneBranchThrows_FailsAndNamesIt_ButStillRunsTheOthers()
    {
        var snsRan = false;

        var sender = SenderFor(routing => routing.Route("order:create", p => p.UseParallel(
            ("sqs", b => b.Use("sqs", _ => async (ctx, _) =>
            {
                await Task.Yield();
                throw new InvalidOperationException("access denied");
            })),
            ("sns", b => b.Use("sns", _ => async (ctx, _) =>
            {
                await Task.Yield();
                snsRan = true;
                ctx.Response = BenzeneResult.Ok<Void>();
            })))));

        var result = await sender.SendAsync<string, Void>("order:create", "payload");

        Assert.False(result.IsSuccessful);
        Assert.True(snsRan); // a failing branch must not abort the fan-out
        Assert.Contains(result.Errors, e => e.Message.Contains("sqs") && e.Message.Contains("access denied"));
    }

    [Fact]
    public async Task UseParallel_OneBranchReturnsFailureResult_FailsAndNamesIt()
    {
        var sender = SenderFor(routing => routing.Route("order:create", p => p.UseParallel(
            ("sqs", b => b.OnRequest(ctx => ctx.Response = BenzeneResult.Ok<Void>())),
            ("sns", b => b.OnRequest(ctx => ctx.Response = BenzeneResult.SetFailed<Void>(BenzeneResultStatus.ServiceUnavailable))))));

        var result = await sender.SendAsync<string, Void>("order:create", "payload");

        Assert.False(result.IsSuccessful);
        Assert.Contains(result.Errors, e => e.Message.Contains("sns"));
    }

    [Fact]
    public async Task UseParallel_BranchObservesAmbientCancellation_AggregatesToDistinctCancelledOutcome()
    {
        // Simulates an Http/Grpc branch (the two clients that correctly observe
        // ICancellationTokenAccessor per WP-C's fix): its own OperationCanceledException fires
        // because the ambient token - not some branch-local timeout - was cancelled mid-flight.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sender = SenderFor(routing => routing.Route("order:create", p => p.UseParallel(
            ("grpc", b => b.Use("grpc", resolver => async (ctx, _) =>
            {
                var token = resolver.TryGetService<ICancellationTokenAccessor>()!.CancellationToken;
                await Task.Yield();
                throw new OperationCanceledException(token);
            })))), cts.Token);

        var result = await sender.SendAsync<string, Void>("order:create", "payload");

        Assert.False(result.IsSuccessful);
        // #269: the aggregate names the branch as cancelled, not folded into the generic
        // UnexpectedError/exception-type text an ordinary failure gets.
        Assert.Contains(result.Errors, e => e.Message == "grpc: Cancelled");
        Assert.DoesNotContain(result.Errors, e => e.Message.Contains(nameof(OperationCanceledException)));
    }

    [Fact]
    public async Task UseParallel_OneBranchCancelledAndOneBranchActuallyFails_DistinguishesTheTwoInTheAggregate_ButStillRunsBoth()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sqsRan = false;

        var sender = SenderFor(routing => routing.Route("order:create", p => p.UseParallel(
            ("grpc", b => b.Use("grpc", resolver => async (ctx, _) =>
            {
                var token = resolver.TryGetService<ICancellationTokenAccessor>()!.CancellationToken;
                await Task.Yield();
                throw new OperationCanceledException(token);
            })),
            ("sqs", b => b.Use("sqs", _ => async (ctx, _) =>
            {
                await Task.Yield();
                sqsRan = true;
                throw new InvalidOperationException("access denied");
            })))), cts.Token);

        var result = await sender.SendAsync<string, Void>("order:create", "payload");

        Assert.False(result.IsSuccessful);
        Assert.True(sqsRan); // the cancelled branch must not abort the fan-out for the others
        Assert.Contains(result.Errors, e => e.Message == "grpc: Cancelled");
        Assert.Contains(result.Errors, e => e.Message.Contains("sqs") && e.Message.Contains("access denied"));
    }
}
