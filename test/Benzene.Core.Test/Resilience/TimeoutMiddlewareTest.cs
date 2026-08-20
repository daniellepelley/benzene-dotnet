using System;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Core;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Resilience;

/// <summary>
/// Coverage for <see cref="TimeoutMiddleware{TContext}"/> and <c>Extensions.UseTimeout</c>: the
/// save/restore composition over <see cref="ICancellationTokenAccessor"/> (see
/// <c>work/archive/cancellation-design-2026-08.md</c> §2.2), and the timeout-vs-cancellation semantic line (§2.4) -
/// a service-configured deadline becomes a <see cref="TimeoutException"/>, but the host's own
/// cancellation propagates as an untouched <see cref="OperationCanceledException"/> so redelivery
/// still happens.
/// </summary>
public class TimeoutMiddlewareTest
{
    // (a) The operation exceeds the deadline: the timer fires, the middleware translates the
    // resulting OperationCanceledException into a TimeoutException, and the accessor is restored.
    [Fact]
    public async Task HandleAsync_OperationExceedsDeadline_ThrowsTimeoutExceptionAndRestoresAccessor()
    {
        var accessor = new CancellationTokenAccessor();
        var middleware = new TimeoutMiddleware<object>(accessor, TimeSpan.FromMilliseconds(50));

        var thrown = await Assert.ThrowsAsync<TimeoutException>(() => middleware.HandleAsync(new object(), async () =>
        {
            // Real handler/middleware code observes the ambient token exactly like this.
            await Task.Delay(TimeSpan.FromSeconds(5), accessor.CancellationToken);
        }));

        Assert.IsAssignableFrom<OperationCanceledException>(thrown.InnerException);
        Assert.Equal(CancellationToken.None, accessor.CancellationToken);
    }

    // (b) The operation finishes in time: the result passes through untouched, no exception, and the
    // accessor is restored to what it was before the middleware ran.
    [Fact]
    public async Task HandleAsync_CompletesWithinDeadline_PassesThroughUntouched()
    {
        var accessor = new CancellationTokenAccessor();
        var middleware = new TimeoutMiddleware<object>(accessor, TimeSpan.FromSeconds(5));
        var ran = false;

        await middleware.HandleAsync(new object(), () =>
        {
            ran = true;
            // While inside next(), the accessor is wrapped even though nothing seeded it - the timer
            // is a real (if distant) source, so the token can be cancelled.
            Assert.True(accessor.CancellationToken.CanBeCanceled);
            return Task.CompletedTask;
        });

        Assert.True(ran);
        Assert.Equal(CancellationToken.None, accessor.CancellationToken);
    }

    // (c) The HOST's own token fires first (before the timeout): the original OperationCanceledException
    // must propagate untouched - NOT be converted into a timeout result - because the redelivery
    // contract (§1 of the design doc) depends on it reaching ExceptionHandlerMiddleware/MessageHandler
    // with a fired token.
    [Fact]
    public async Task HandleAsync_HostTokenFiresFirst_PropagatesOperationCanceledExceptionUntouched()
    {
        using var hostCts = new CancellationTokenSource();
        var accessor = new CancellationTokenAccessor { CancellationToken = hostCts.Token };
        // A timeout long enough that the timer cannot possibly fire during this test.
        var middleware = new TimeoutMiddleware<object>(accessor, TimeSpan.FromSeconds(30));

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.HandleAsync(new object(), () =>
        {
            // The HOST cancels - not the timer.
            hostCts.Cancel();
            // Downstream code observes the wrapped ambient token, exactly as real code would (it has
            // no way to know it's "really" the host token underneath).
            throw new OperationCanceledException(accessor.CancellationToken);
        }));

        Assert.IsNotType<TimeoutException>(thrown);
        // Restored to the (now-cancelled) host token, not left on the by-then-disposed linked token.
        Assert.Equal(hostCts.Token, accessor.CancellationToken);
    }

    // (d) Nested UseTimeout: the inner deadline wins while inside it, and the outer's accessor state
    // is correctly restored afterward - proving the save/restore composes across layers.
    [Fact]
    public async Task HandleAsync_NestedUseTimeout_InnerDeadlineWinsAndOuterRestoresAfter()
    {
        var rootToken = CancellationToken.None;
        var accessor = new CancellationTokenAccessor { CancellationToken = rootToken };
        var outer = new TimeoutMiddleware<object>(accessor, TimeSpan.FromSeconds(30));
        var inner = new TimeoutMiddleware<object>(accessor, TimeSpan.FromMilliseconds(50));

        var observedInnerToken = default(CancellationToken);

        await Assert.ThrowsAsync<TimeoutException>(() => outer.HandleAsync(new object(), () =>
            inner.HandleAsync(new object(), async () =>
            {
                observedInnerToken = accessor.CancellationToken;
                await Task.Delay(TimeSpan.FromSeconds(5), accessor.CancellationToken);
            })));

        // The innermost wrap governed while inside it - a distinct token from the (unset) root.
        Assert.NotEqual(rootToken, observedInnerToken);
        // Both layers unwound cleanly: the accessor is back to the value it held before outer ran.
        Assert.Equal(rootToken, accessor.CancellationToken);
    }

    // (e) No CancellationTokenSource/token leak on the success path: the linked CTS backing the
    // wrapped token must be disposed once HandleAsync returns, not only on an exception path.
    [Fact]
    public async Task HandleAsync_SuccessPath_DisposesTheLinkedCancellationTokenSource()
    {
        var accessor = new CancellationTokenAccessor();
        var middleware = new TimeoutMiddleware<object>(accessor, TimeSpan.FromSeconds(5));
        var observedToken = default(CancellationToken);

        await middleware.HandleAsync(new object(), () =>
        {
            observedToken = accessor.CancellationToken;
            return Task.CompletedTask;
        });

        // Once its CancellationTokenSource is disposed, CancellationToken.WaitHandle throws
        // ObjectDisposedException - the externally-observable proof of disposal from outside the
        // middleware, since the CTS itself is a local `using` variable and never itself gets
        // cancelled here (Register() alone is not proof: since .NET Core 3.0 it silently no-ops on an
        // already-disposed, never-cancelled source instead of throwing).
        Assert.Throws<ObjectDisposedException>(() => observedToken.WaitHandle);
    }

    // (f) An unseeded host (accessor still at its default CancellationToken.None): UseTimeout must
    // still work correctly, with the timer as the only possible source - CreateLinkedTokenSource(None)
    // is valid, and (per §2.4) original.IsCancellationRequested can never be true for None, so a
    // timeout always translates cleanly.
    [Fact]
    public async Task HandleAsync_UnseededHost_TimerIsTheOnlyPossibleSourceAndStillTranslatesCleanly()
    {
        var accessor = new CancellationTokenAccessor(); // never seeded - default CancellationToken.None
        var middleware = new TimeoutMiddleware<object>(accessor, TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() => middleware.HandleAsync(new object(), async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), accessor.CancellationToken);
        }));

        Assert.Equal(CancellationToken.None, accessor.CancellationToken);
    }

    [Fact]
    public async Task HandleAsync_UnseededHost_CompletesInTime_StillPassesThroughUntouched()
    {
        var accessor = new CancellationTokenAccessor();
        var middleware = new TimeoutMiddleware<object>(accessor, TimeSpan.FromSeconds(5));
        var ran = false;

        await middleware.HandleAsync(new object(), () =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        Assert.True(ran);
        Assert.Equal(CancellationToken.None, accessor.CancellationToken);
    }

    // --- Extensions.UseTimeout: proves the pipeline-builder wiring, not just direct construction ---

    private sealed class SlowMiddleware : IMiddleware<string>
    {
        private readonly ICancellationTokenAccessor _accessor;

        public SlowMiddleware(ICancellationTokenAccessor accessor)
        {
            _accessor = accessor;
        }

        public string Name => "slow";

        public async Task HandleAsync(string context, Func<Task> next)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), _accessor.CancellationToken);
            await next();
        }
    }

    [Fact]
    public async Task UseTimeout_ResolvesTheAccessorPerInvocation_AndAppliesTheConfiguredDeadline()
    {
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddBenzene();

        var builder = new MiddlewarePipelineBuilder<string>(container);
        builder.UseTimeout(TimeSpan.FromMilliseconds(50));
        builder.Use(resolver => new SlowMiddleware(resolver.GetService<ICancellationTokenAccessor>()));
        var pipeline = builder.Build();

        using var scope = new MicrosoftServiceResolverFactory(services).CreateScope();

        await Assert.ThrowsAsync<TimeoutException>(() => pipeline.HandleAsync("context", scope));
    }
}
