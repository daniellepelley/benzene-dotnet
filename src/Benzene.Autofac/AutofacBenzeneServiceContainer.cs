using Autofac;
using Benzene.Abstractions.DI;

namespace Benzene.Autofac;

public class AutofacBenzeneServiceContainer : IBenzeneServiceContainer
{
    private readonly ContainerBuilder _containerBuilder;

    // Autofac's ComponentRegistryBuilder isn't populated until ContainerBuilder.Build() runs, but
    // IsTypeRegistered is called during registration (well before Build()) by every TryAdd* extension
    // method - so checking the registry builder there always reports false, silently turning every
    // TryAdd* into an unconditional last-write-wins Add*. Track registered service types explicitly
    // here instead, updated by every AddXxx/AddServiceResolver call as registrations happen - mirroring
    // MicrosoftBenzeneServiceContainer, which checks its live, always-current IServiceCollection.
    private readonly HashSet<Type> _registeredTypes = [];

    public AutofacBenzeneServiceContainer(ContainerBuilder containerBuilder)
    {
        _containerBuilder = containerBuilder;
    }

    public bool IsTypeRegistered<TService>()
    {
        return IsTypeRegistered(typeof(TService));
    }

    public bool IsTypeRegistered(Type type)
    {
        return _registeredTypes.Contains(type);
    }

    public IBenzeneServiceContainer AddScoped(Type type)
    {
        if (type.IsGenericType)
        {
            _containerBuilder.RegisterGeneric(type).InstancePerLifetimeScope();
        }
        else
        {
            _containerBuilder.RegisterType(type).InstancePerLifetimeScope();
        }

        _registeredTypes.Add(type);
        return this;
    }

    public IBenzeneServiceContainer AddScoped(Type serviceType, Type implementationType)
    {
        if (implementationType.IsGenericType)
        {
            _containerBuilder.RegisterGeneric(implementationType).As(serviceType).InstancePerLifetimeScope();
        }
        else
        {
            _containerBuilder.RegisterType(implementationType).As(serviceType).InstancePerLifetimeScope();
        }

        _registeredTypes.Add(serviceType);
        return this;
    }

    public IBenzeneServiceContainer AddScoped<TImplementation>(TImplementation implementation) where TImplementation : class
    {
        // Register the supplied instance, not a freshly-constructed TImplementation (RegisterType),
        // to honour the "using an existing instance" contract and match the Microsoft adapter.
        _containerBuilder.Register(_ => implementation).InstancePerLifetimeScope();
        _registeredTypes.Add(typeof(TImplementation));
        return this;
    }

    public IBenzeneServiceContainer AddScoped<TImplementation>() where TImplementation : class
    {
        _containerBuilder.RegisterType<TImplementation>().InstancePerLifetimeScope();
        _registeredTypes.Add(typeof(TImplementation));
        return this;
    }

    public IBenzeneServiceContainer AddScoped<TService, TImplementation>()
        where TService : class where TImplementation : class, TService
    {
        _containerBuilder.RegisterType<TImplementation>().As<TService>().InstancePerLifetimeScope();
        _registeredTypes.Add(typeof(TService));
        return this;
    }

    public IBenzeneServiceContainer AddScoped<TImplementation>(Func<IServiceResolver, TImplementation> func)
        where TImplementation : class
    {
        _containerBuilder
            .Register<TImplementation>(x => func(new AutofacServiceResolverAdapter(x.Resolve<IComponentContext>())))
            .InstancePerLifetimeScope();
        _registeredTypes.Add(typeof(TImplementation));
        return this;
    }

    public IBenzeneServiceContainer AddTransient<TImplementation>() where TImplementation : class
    {
        _containerBuilder.RegisterType<TImplementation>().InstancePerDependency();
        _registeredTypes.Add(typeof(TImplementation));
        return this;
    }

    public IBenzeneServiceContainer AddTransient<TService, TImplementation>() where TService : class where TImplementation : class, TService
    {
        _containerBuilder.RegisterType<TImplementation>().As<TService>().InstancePerDependency();
        _registeredTypes.Add(typeof(TService));
        return this;
    }

    public IBenzeneServiceContainer AddTransient(Type type)
    {
        if (type.IsGenericType)
        {
            _containerBuilder.RegisterGeneric(type).InstancePerDependency();
        }
        else
        {
            _containerBuilder.RegisterType(type).InstancePerDependency();
        }

        _registeredTypes.Add(type);
        return this;
    }

    public IBenzeneServiceContainer AddTransient(Type serviceType, Type implementationType)
    {
        if (implementationType.IsGenericType)
        {
            _containerBuilder.RegisterGeneric(implementationType).As(serviceType).InstancePerDependency();
        }
        else
        {
            _containerBuilder.RegisterType(implementationType).As(serviceType).InstancePerDependency();
        }

        _registeredTypes.Add(serviceType);
        return this;
    }

    public IBenzeneServiceContainer AddTransient<TImplementation>(TImplementation implementation) where TImplementation : class
    {
        // Register the supplied instance, not a freshly-constructed TImplementation (RegisterType),
        // to honour the "using an existing instance" contract and match the Microsoft adapter.
        _containerBuilder.Register(_ => implementation).InstancePerDependency();
        _registeredTypes.Add(typeof(TImplementation));
        return this;
    }

    public IBenzeneServiceContainer AddTransient<TImplementation>(Func<IServiceResolver, TImplementation> func) where TImplementation : class
    {
        _containerBuilder
            .Register<TImplementation>(x => func(new AutofacServiceResolverAdapter(x.Resolve<IComponentContext>())))
            .InstancePerDependency();
        _registeredTypes.Add(typeof(TImplementation));
        return this;
    }

    public IBenzeneServiceContainer AddSingleton<TImplementation>() where TImplementation : class
    {
        _containerBuilder.RegisterType<TImplementation>().SingleInstance();
        _registeredTypes.Add(typeof(TImplementation));
        return this;
    }

    public IBenzeneServiceContainer AddSingleton<TService, TImplementation>()
        where TService : class
        where TImplementation : class, TService
    {
        _containerBuilder.RegisterType<TImplementation>().As<TService>().SingleInstance();
        _registeredTypes.Add(typeof(TService));
        return this;
    }

    public IBenzeneServiceContainer AddSingleton(Type type)
    {
        if (type.IsGenericType)
        {
            _containerBuilder.RegisterGeneric(type).SingleInstance();
        }
        else
        {
            _containerBuilder.RegisterType(type).SingleInstance();
        }

        _registeredTypes.Add(type);
        return this;
    }

    public IBenzeneServiceContainer AddSingleton(Type serviceType, Type implementationType)
    {
        if (implementationType.IsGenericType)
        {
            _containerBuilder.RegisterGeneric(implementationType).As(serviceType).SingleInstance();
        }
        else
        {
            _containerBuilder.RegisterType(implementationType).As(serviceType).SingleInstance();
        }

        _registeredTypes.Add(serviceType);
        return this;
    }

    public IBenzeneServiceContainer AddSingleton<TImplementation>(Func<IServiceResolver, TImplementation> func)
        where TImplementation : class
    {
        _containerBuilder
            .Register<TImplementation>(x => func(new AutofacServiceResolverAdapter(x.Resolve<IComponentContext>())))
            .SingleInstance();
        _registeredTypes.Add(typeof(TImplementation));
        return this;
    }

    public IServiceResolverFactory CreateServiceResolverFactory()
    {
        return new AutofacServiceResolverFactory(_containerBuilder);
    }

    public IBenzeneServiceContainer AddSingleton<TImplementation>(TImplementation implementation)
        where TImplementation : class
    {
        _containerBuilder.RegisterInstance(implementation).SingleInstance();
        _registeredTypes.Add(typeof(TImplementation));
        return this;
    }

    public IBenzeneServiceContainer AddServiceResolver()
    {
        _containerBuilder
            .Register<IServiceResolver>(x => new AutofacServiceResolverAdapter(x.Resolve<IComponentContext>()))
            .InstancePerLifetimeScope();
        _registeredTypes.Add(typeof(IServiceResolver));
        return this;
    }
}
