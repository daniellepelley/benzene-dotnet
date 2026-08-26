using Autofac;
using Benzene.Abstractions.DI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Benzene.Autofac;

public class AutofacServiceResolverFactory : IServiceResolverFactory, IAsyncDisposable
{
    // ILifetimeScope (not IContainer) so this factory can wrap either a freshly-built root IContainer
    // (IContainer : ILifetimeScope) or an arbitrary already-open scope - e.g. the ambient scope an
    // AutofacServiceResolverAdapter lazily builds its own IServiceResolverFactory from (see
    // AutofacServiceResolverAdapter.ResolverFactory), without needing to build/own anything new.
    private readonly ILifetimeScope _scope;
    private readonly bool _ownsScope;

    /// <summary>
    /// Builds a brand-new <see cref="IContainer"/> from <paramref name="containerBuilder"/> and owns
    /// it. <see cref="ContainerBuilder.Build"/> can only run once per builder, so use this constructor
    /// at most once per <see cref="ContainerBuilder"/> - <see cref="AutofacBenzeneServiceContainer"/>
    /// uses it exactly once, lazily, to build its underlying container; every other resolver-factory
    /// creation on that container should go through the <see cref="AutofacServiceResolverFactory(ILifetimeScope)"/>
    /// overload instead so it doesn't try to build twice.
    /// </summary>
    public AutofacServiceResolverFactory(ContainerBuilder containerBuilder)
    {
        _scope = BuildOwnedContainer(containerBuilder);
        _ownsScope = true;
    }

    /// <summary>
    /// Wraps an already-open <see cref="ILifetimeScope"/> (typically an already-built root
    /// <see cref="IContainer"/>, or a scope obtained from Autofac's own resolution machinery) without
    /// building or owning it - mirrors <see cref="Benzene.Microsoft.Dependencies.MicrosoftServiceResolverFactory"/>'s
    /// <c>IServiceProvider</c> constructor overload, which likewise never disposes an externally
    /// supplied provider. <see cref="Dispose"/>/<see cref="DisposeAsync"/> are no-ops here - the scope's
    /// lifetime is owned by whoever supplied it.
    /// </summary>
    public AutofacServiceResolverFactory(ILifetimeScope scope)
    {
        _scope = scope;
        _ownsScope = false;
    }

    /// <summary>
    /// Registers the Benzene logging fallbacks (<see cref="NullLoggerFactory"/> / open-generic
    /// <c>Logger&lt;&gt;</c>, both <c>IfNotRegistered</c> so a real registration always wins) and builds
    /// the container. Shared by the owning <see cref="AutofacServiceResolverFactory(ContainerBuilder)"/>
    /// constructor and by <see cref="AutofacBenzeneServiceContainer"/>'s own lazy, build-once container
    /// construction, so the two paths can't drift apart.
    /// </summary>
    internal static IContainer BuildOwnedContainer(ContainerBuilder containerBuilder)
    {
        containerBuilder.RegisterInstance(NullLoggerFactory.Instance).As<ILoggerFactory>()
            .IfNotRegistered(typeof(ILoggerFactory));
        containerBuilder.RegisterGeneric(typeof(Logger<>)).As(typeof(ILogger<>)).SingleInstance()
            .IfNotRegistered(typeof(ILogger<>));
        return containerBuilder.Build();
    }

    public void Dispose()
    {
        // Only dispose a scope we built ourselves - a non-owning wrapper (see the ILifetimeScope
        // constructor) shares state with other factories/adapters still using it, and disposing it
        // out from under them would be a correctness bug, not cleanup. Disposing runs the container's
        // IDisposable singletons' cleanup - previously this was a no-op and they leaked until process
        // exit, mirroring the bug already fixed on the Microsoft.Extensions.DependencyInjection adapter
        // (see MicrosoftServiceResolverFactory.Dispose).
        if (_ownsScope)
        {
            _scope.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_ownsScope)
        {
            return;
        }

        // Prefer async disposal for the same reason as MicrosoftServiceResolverFactory.DisposeAsync: a
        // singleton registered only for IAsyncDisposable (not IDisposable) would throw if disposed
        // synchronously. Autofac's ILifetimeScope/IContainer implement both.
        await _scope.DisposeAsync();
    }

    public IServiceResolver CreateScope()
    {
        return new AutofacServiceResolverAdapter(_scope.BeginLifetimeScope(), this);
    }
}