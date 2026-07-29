using System;
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

    public void Dispose()
    {
        // Only dispose a provider we built ourselves. Disposing runs the container's IDisposable
        // singletons' cleanup (e.g. MeshAnnouncer's announce loop, HttpMeshTraceExporter's tail-batch
        // flush), which previously leaked until process exit on the Lambda / self-host-from-
        // IServiceCollection paths - there this Dispose() was a no-op and nothing else owned the provider.
        if (_ownsServiceProvider)
        {
            (_serviceProvider as IDisposable)?.Dispose();
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
