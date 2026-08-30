using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Core.Core.DI;

/// <summary>
/// Task board #289 (round 17, WP-A, work/bug-fix-plan-round17-2026-08.md): round 16's #266 fix
/// bridges <see cref="MicrosoftServiceResolverAdapter.Dispose"/> and
/// <see cref="MicrosoftServiceResolverFactory.Dispose"/> to a container-owned service's
/// <c>DisposeAsync()</c> via a deliberately unbounded
/// <c>DisposeAsync().AsTask().GetAwaiter().GetResult()</c>. That bridge deadlocks the calling
/// thread FOREVER - not just for a long time - when the disposed service's own
/// <c>DisposeAsync()</c> awaits without <c>ConfigureAwait(false)</c> (ordinary application code)
/// under an ambient single-thread-affinity <see cref="SynchronizationContext"/> (the same shape as
/// WinForms'/WPF's message-loop context or Blazor Server's per-circuit renderer context): the
/// posted continuation can only run on the very thread that is blocked waiting for it.
///
/// Green requires Dispose() to prevent the blocking call from observing the ambient
/// SynchronizationContext (so the continuation runs on a thread-pool thread instead), restoring the
/// original context afterward - Dispose() must return within a few seconds and the async disposal
/// must actually have run.
/// </summary>
public class MicrosoftServiceResolverAdapterSyncContextDeadlockTest
{
    // Single-thread-affinity SynchronizationContext, the same shape (not implementation) as
    // WinForms'/WPF's message-loop context and Blazor Server's per-circuit renderer context:
    // Post() just enqueues - nothing ever dequeues unless the owning thread explicitly pumps.
    private sealed class SingleThreadAffinitySynchronizationContext : SynchronizationContext
    {
        public readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> Queue = new();
        public override void Post(SendOrPostCallback d, object? state) => Queue.Add((d, state));
    }

    // Bypasses the real MS DI container to isolate exactly the branch under test.
    private sealed class FakeAsyncDisposableScope : IServiceScope, IAsyncDisposable
    {
        public bool DisposedAsync { get; private set; }
        public IServiceProvider ServiceProvider { get; } = new ServiceCollection().BuildServiceProvider();

        public async ValueTask DisposeAsync()
        {
            await Task.Delay(20); // no ConfigureAwait(false) - deliberately ordinary application code
            DisposedAsync = true;
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void Dispose_ScopeDisposeAsyncCapturesAmbientSyncContext_ReturnsAndActuallyDisposes()
    {
        Exception? threadException = null;
        var disposeReturned = false;
        FakeAsyncDisposableScope? scope = null;

        var thread = new Thread(() =>
        {
            try
            {
                var syncContext = new SingleThreadAffinitySynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(syncContext);
                scope = new FakeAsyncDisposableScope();
                var adapter = new MicrosoftServiceResolverAdapter(scope);
                adapter.Dispose(); // the call under test
                disposeReturned = true;
            }
            catch (Exception ex)
            {
                threadException = ex;
            }
        });
        thread.IsBackground = true; // don't block the test process from exiting if this still hangs
        thread.Start();

        var joined = thread.Join(TimeSpan.FromSeconds(10));

        Assert.Null(threadException);
        Assert.True(joined, "Dispose() did not return within 10s - it deadlocked under the ambient SynchronizationContext");
        Assert.True(disposeReturned);
        Assert.NotNull(scope);
        Assert.True(scope!.DisposedAsync, "the scope's DisposeAsync() must actually have run, not merely returned");
    }

    private sealed class FakeAsyncDisposableProvider : IServiceProvider, IAsyncDisposable
    {
        public bool DisposedAsync { get; private set; }

        public object? GetService(Type serviceType) => null;

        public async ValueTask DisposeAsync()
        {
            await Task.Delay(20); // no ConfigureAwait(false) - deliberately ordinary application code
            DisposedAsync = true;
        }
    }

    [Fact]
    public void FactoryDispose_ProviderDisposeAsyncCapturesAmbientSyncContext_ReturnsAndActuallyDisposes()
    {
        Exception? threadException = null;
        var disposeReturned = false;

        var thread = new Thread(() =>
        {
            try
            {
                var syncContext = new SingleThreadAffinitySynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(syncContext);

                // Exercise the code path through a real container holding an IAsyncDisposable-only
                // singleton, matching AsyncOnlyDisposableParityTest's shape but under the
                // single-thread-affinity context.
                var realServices = new ServiceCollection();
                realServices.AddSingleton<FakeAsyncDisposableSingleton>();
                var realFactory = new MicrosoftServiceResolverFactory(realServices);
                using (var scope = realFactory.CreateScope())
                {
                    scope.GetService<FakeAsyncDisposableSingleton>();
                }

                realFactory.Dispose(); // the call under test
                disposeReturned = true;
            }
            catch (Exception ex)
            {
                threadException = ex;
            }
        });
        thread.IsBackground = true;
        thread.Start();

        var joined = thread.Join(TimeSpan.FromSeconds(10));

        Assert.Null(threadException);
        Assert.True(joined, "MicrosoftServiceResolverFactory.Dispose() did not return within 10s - it deadlocked under the ambient SynchronizationContext");
        Assert.True(disposeReturned);
    }

    private sealed class FakeAsyncDisposableSingleton : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Task.Delay(20); // no ConfigureAwait(false) - deliberately ordinary application code
        }
    }
}
