using System.Diagnostics;
using System.Threading;
using Benzene.Abstractions.DI;
using Benzene.Core.Exceptions;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Microsoft.Dependencies;

public sealed class MicrosoftServiceResolverAdapter : IServiceResolver
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IServiceScope? _scope;
    private MicrosoftServiceResolverFactory? _serviceResolverFactory;

    // Return a stable factory instance for the adapter's lifetime rather than allocating a fresh one
    // on every IServiceResolverFactory resolution - matching the Autofac adapter, which returns its
    // stored factory. Both GetService and TryGetService go through here so they can't diverge.
    private MicrosoftServiceResolverFactory ResolverFactory
        => _serviceResolverFactory ??= new MicrosoftServiceResolverFactory(_serviceProvider);

    public MicrosoftServiceResolverAdapter(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Wraps a scope created via <see cref="MicrosoftServiceResolverFactory.CreateScope"/>. Unlike
    /// the <see cref="IServiceProvider"/> constructor - where the provider's lifetime is owned by
    /// whoever passed it in (e.g. Microsoft.Extensions.DependencyInjection's own scope management,
    /// or ASP.NET Core's per-request provider) - this adapter owns <paramref name="scope"/> and
    /// disposes it, so the scoped services resolved through it are actually released.
    /// </summary>
    public MicrosoftServiceResolverAdapter(IServiceScope scope)
    {
        _scope = scope;
        _serviceProvider = scope.ServiceProvider;
    }

    public T GetService<T>() where T : class
    {
        if (typeof(T) == typeof(IServiceResolver))
        {
            return this as T ?? throw new InvalidOperationException();
        }

        if (typeof(T) == typeof(IServiceResolverFactory))
        {
            return ResolverFactory as T ?? throw new InvalidOperationException();
        }

        try
        {
            return _serviceProvider.GetRequiredService<T>();
        }
        catch (Exception ex)
        {
            // Enrich from the requested type first (always known, so this works on any container) and
            // fall back to scanning the exception; Describe never throws, so it can't mask the real
            // failure, which is preserved as the InnerException either way.
            var hint = RegistrationErrorHandler.Describe(typeof(T), ex);
            Debug.WriteLine($"Unable to resolve type {typeof(T).FullName}{hint}, Exception: {ex}");
            throw new BenzeneResolutionException($"Unable to resolve type {typeof(T).FullName}{hint}", ex);
        }
    }

    public T? TryGetService<T>() where T : class
    {
        if (typeof(T) == typeof(IServiceResolver))
        {
            return this as T;
        }

        if (typeof(T) == typeof(IServiceResolverFactory))
        {
            return ResolverFactory as T;
        }

        try
        {
            // GetService (not GetRequiredService) returns null for an UNREGISTERED service without
            // throwing - so the common "optional feature is off" check (run per request/per event
            // across the framework) no longer raises and catches a first-chance exception every time.
            // The try/catch now only guards the rare registered-but-throws-on-construction case,
            // preserving the previous "TryGetService never propagates" behavior.
            return _serviceProvider.GetService<T>();
        }
        catch
        {
            return default;
        }
    }

    public IEnumerable<T> GetServices<T>() where T : class
    {
        return _serviceProvider.GetServices<T>();
    }

    /// <summary>
    /// Disposes the wrapped scope. Bridges to <see cref="IAsyncDisposable.DisposeAsync"/> - with an
    /// UNBOUNDED wait - when the scope needs it, rather than calling <see cref="IDisposable.Dispose"/>
    /// directly: Microsoft.Extensions.DependencyInjection's own scope <c>Dispose()</c> throws
    /// <see cref="InvalidOperationException"/> the moment it has to tear down a resolved instance that
    /// implements only <see cref="IAsyncDisposable"/> (task board #266, round 16 -
    /// <c>work/bug-fix-plan-round16-2026-08.md</c> WP-A) - an entirely ordinary shape for a
    /// user-registered async-native client/connection. This is the systemic fix: every transport built
    /// on <c>Benzene.Core.Middleware</c> tears its per-message scope down through exactly this method
    /// (<c>MiddlewareApplication.HandleAsync</c>'s <c>using var serviceResolver = ...</c>), so without
    /// this bridge, resolving such a service crashed AND leaked (its own <c>DisposeAsync</c> never ran)
    /// on every single message. The wait is deliberately unbounded, unlike the bounded-5s pattern used
    /// for best-effort telemetry flushes elsewhere (<c>MeshAnnouncer</c>) - abandoning a user's own
    /// scope disposal mid-way would silently leak their resources by design, and Autofac's own
    /// <see cref="Autofac.ILifetimeScope.Dispose"/> already blocks unboundedly for the identical shape,
    /// so this restores parity rather than introducing new blocking behavior. The wait deliberately
    /// suppresses the ambient <see cref="SynchronizationContext"/> (restored in a <c>finally</c>)
    /// around the blocking call: without this, a scope's own <c>DisposeAsync()</c> that awaits
    /// without <c>ConfigureAwait(false)</c> (ordinary application code) can deadlock this thread
    /// FOREVER under a single-thread-affinity context (WinForms/WPF/Blazor-Server-shaped) - the
    /// posted continuation could only ever run on the very thread now blocked waiting for it (task
    /// board #289, round 17). This is the standard sync-over-async mitigation, not the bounded-5s
    /// pattern - it keeps the wait unbounded (matching Autofac) while removing the deadlock vector.
    /// </summary>
    public void Dispose()
    {
        if (_scope is IAsyncDisposable asyncDisposableScope)
        {
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                asyncDisposableScope.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }
        else
        {
            _scope?.Dispose();
        }
    }
}