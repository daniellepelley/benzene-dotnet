using System;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Microsoft.Dependencies;

public class MicrosoftServiceResolverFactory : IServiceResolverFactory, IAsyncDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly bool _ownsServiceProvider;

    public MicrosoftServiceResolverFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        // An externally-supplied provider belongs to whoever built it - do not dispose it here.
        _ownsServiceProvider = false;
    }

    public MicrosoftServiceResolverFactory(IServiceCollection container, bool validateOnBuild = false)
    {
        // ValidateOnBuild resolves every registration's constructor dependencies at container build,
        // rather than leaving a missing one to surface when a message first reaches the middleware
        // that needs it — the pipeline resolves middleware inside its per-link closure, so nothing is
        // constructed until dispatch and a missing registration is invisible until then.
        //
        // It is OPT-IN, and the reason is measured rather than assumed: switching it on by default
        // failed 67 tests across four projects, and every one was a *legitimately partial* container
        // — a test that registers only what it exercises, leaving e.g. IMessageHandlersFinder
        // unresolvable because nothing on that path asks for it. Partial composition is a supported
        // arrangement here, and a check that rejects a valid one is worse than the bug it catches.
        // Turn it on for a fully-composed application, where an unresolvable registration is a
        // genuine wiring error.
        //
        // ValidateScopes rides along with it. It catches a different and nastier class of bug — a
        // singleton capturing a scoped service, which does not fail, it just silently serves the first
        // scope's instance forever. Benzene had one (HealthCheckBuilder registering IHealthCheckFinder
        // as a singleton over scoped IDependencyHealthCheck); that is fixed, so the check no longer
        // fails Benzene's own flagship example and there is no reason to hold it back from anyone who
        // opts in.
        _serviceProvider = validateOnBuild
            ? container.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true })
            : container.BuildServiceProvider();
        // We built this provider, so we own its disposal.
        _ownsServiceProvider = true;
    }

    /// <summary>
    /// Disposes the provider we built ourselves (an externally-supplied provider's lifetime belongs to
    /// whoever built it, so this is a no-op there). Disposing runs the container's disposable
    /// singletons' cleanup (e.g. MeshAnnouncer's announce loop, HttpMeshTraceExporter's tail-batch
    /// flush), which previously leaked until process exit on the Lambda / self-host-from-
    /// IServiceCollection paths - there this Dispose() was a no-op and nothing else owned the provider.
    /// Prefers the async bridge - with an UNBOUNDED wait - over the plain <see cref="IDisposable"/>
    /// cast when the provider needs it: Microsoft.Extensions.DependencyInjection's own root provider
    /// <c>Dispose()</c> throws <see cref="InvalidOperationException"/> for a container-owned singleton
    /// that implements only <see cref="IAsyncDisposable"/> (task board #262, round 16 -
    /// <c>work/bug-fix-plan-round16-2026-08.md</c> WP-A) - this is the ONLY disposal path some hosts
    /// have at all (e.g. <c>Benzene.Aws.Lambda.Core</c>'s whole disposal chain is
    /// <see cref="IDisposable"/>-only), so silently failing to dispose such a singleton (or throwing)
    /// is not acceptable. Same rationale/pattern as <see cref="MicrosoftServiceResolverAdapter.Dispose"/>.
    /// The wait deliberately suppresses the ambient <see cref="SynchronizationContext"/> (restored in
    /// a <c>finally</c>) around the blocking call: without this, a container-owned singleton's own
    /// <c>DisposeAsync()</c> that awaits without <c>ConfigureAwait(false)</c> (ordinary application
    /// code) can deadlock this thread FOREVER under a single-thread-affinity context (WinForms/WPF/
    /// Blazor-Server-shaped) - and because this disposes the WHOLE root provider, typically once at
    /// process/host shutdown, that hang can mean the entire application never finishes shutting down
    /// (task board #289, round 17). This is the standard sync-over-async mitigation, not the
    /// bounded-5s pattern - it keeps the wait unbounded while removing the deadlock vector.
    /// </summary>
    public void Dispose()
    {
        if (!_ownsServiceProvider)
        {
            return;
        }

        if (_serviceProvider is IAsyncDisposable asyncDisposable)
        {
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }
        else if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_ownsServiceProvider)
        {
            return;
        }

        // Prefer async disposal: a singleton registered only for IAsyncDisposable (not IDisposable)
        // would throw if disposed synchronously. Microsoft's ServiceProvider implements both.
        if (_serviceProvider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    public IServiceResolver CreateScope()
    {
        return new MicrosoftServiceResolverAdapter(_serviceProvider.CreateScope());
    }
}
