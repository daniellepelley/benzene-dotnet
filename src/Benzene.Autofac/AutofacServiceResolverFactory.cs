using Autofac;
using Benzene.Abstractions.DI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Benzene.Autofac;

public class AutofacServiceResolverFactory : IServiceResolverFactory
{
    private readonly IContainer _container;

    public AutofacServiceResolverFactory(ContainerBuilder containerBuilder)
    {
        containerBuilder.RegisterInstance(NullLoggerFactory.Instance).As<ILoggerFactory>()
            .IfNotRegistered(typeof(ILoggerFactory));
        containerBuilder.RegisterGeneric(typeof(Logger<>)).As(typeof(ILogger<>)).SingleInstance()
            .IfNotRegistered(typeof(ILogger<>));
        _container = containerBuilder.Build();
    }

    public void Dispose()
    {
        // The factory built this container (the only constructor takes a ContainerBuilder), so it
        // owns disposal. Disposing runs the container's IDisposable singletons' cleanup - previously
        // this was a no-op and they leaked until process exit, mirroring the bug already fixed on the
        // Microsoft.Extensions.DependencyInjection adapter (see MicrosoftServiceResolverFactory.Dispose).
        _container.Dispose();
    }

    public IServiceResolver CreateScope()
    {
        return new AutofacServiceResolverAdapter(_container.BeginLifetimeScope(), this);
    }
}