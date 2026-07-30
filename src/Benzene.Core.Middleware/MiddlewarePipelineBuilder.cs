using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Pipelines;

namespace Benzene.Core.Middleware;

/// <summary>
/// Provides a fluent builder for constructing middleware pipelines.
/// </summary>
/// <typeparam name="TContext">The context type that the pipeline operates on.</typeparam>
/// <remarks>
/// This builder enables a fluent API for pipeline construction, allowing middleware to be added
/// in sequence and dependencies to be registered. Each builder instance maintains its own list
/// of middleware while sharing dependency registration with related builders.
/// </remarks>
public class MiddlewarePipelineBuilder<TContext> : IMiddlewarePipelineBuilder<TContext>
{
    private readonly List<Func<IServiceResolver, IMiddleware<TContext>>> _items = new();
    private readonly IRegisterDependency _registerDependency;

    /// <summary>
    /// Initializes a new instance of the <see cref="MiddlewarePipelineBuilder{TContext}"/> class.
    /// </summary>
    /// <param name="benzeneServiceContainer">The service container for dependency registration.</param>
    public MiddlewarePipelineBuilder(IBenzeneServiceContainer benzeneServiceContainer)
        :this(new RegisterDependency(benzeneServiceContainer))
    { }

    /// <summary>
    /// Initializes a new instance of the <see cref="MiddlewarePipelineBuilder{TContext}"/> class.
    /// </summary>
    /// <param name="registerDependency">The dependency registration provider.</param>
    public MiddlewarePipelineBuilder(IRegisterDependency registerDependency)
    {
        _registerDependency = registerDependency;
        registerDependency.Register(x => x.AddBenzeneMiddleware());
    }

    /// <summary>
    /// Adds middleware to the pipeline using a factory function.
    /// </summary>
    /// <param name="func">The function that creates the middleware instance from a service resolver.</param>
    /// <returns>The pipeline builder for method chaining.</returns>
    public IMiddlewarePipelineBuilder<TContext> Use(Func<IServiceResolver, IMiddleware<TContext>> func)
    {
        _items.Add(func);
        return this;
    }

    /// <summary>
    /// Registers services with the dependency injection container.
    /// </summary>
    /// <param name="action">The action that performs service registration.</param>
    public void Register(Action<IBenzeneServiceContainer> action)
    {
       _registerDependency.Register(action);
    }

    /// <summary>
    /// Gets the middleware factory functions registered with this builder.
    /// </summary>
    /// <returns>An array of middleware factory functions.</returns>
    public Func<IServiceResolver, IMiddleware<TContext>>[] GetItems() => _items.ToArray();

    /// <summary>
    /// Creates a new pipeline builder for a different context type, sharing the same dependency registration.
    /// </summary>
    /// <typeparam name="TNewContext">The context type for the new pipeline builder.</typeparam>
    /// <returns>A new pipeline builder instance.</returns>
    public IMiddlewarePipelineBuilder<TNewContext> Create<TNewContext>()
    {
        return new MiddlewarePipelineBuilder<TNewContext>(_registerDependency);
    }

    /// <summary>
    /// Builds the middleware pipeline from the registered middleware.
    /// </summary>
    /// <returns>The constructed middleware pipeline ready for execution.</returns>
    public IMiddlewarePipeline<TContext> Build()
    {
        var items = GetItems();

        // Publish the pipeline's shape into the shared container as it is built. Build() is the only
        // moment both facts are in hand — the middleware factories, and a container to put them in —
        // because the builder is discarded immediately afterwards and the returned pipeline exposes no
        // way to enumerate itself. A start-up check can then walk every pipeline in the process,
        // including each transport's per-message sub-pipeline, without changing how any of them run.
        //
        // Registering the factories does not call them: construction happens only when a check (or a
        // message) asks for it.
        _registerDependency.Register(x =>
        {
            // An outbound client pipeline is built against the null-object container out of instances
            // the client already holds. It has nothing to register into and nothing to check.
            if (!x.SupportsRegistration)
            {
                return;
            }

            x.AddSingleton(new PipelineDescriptor(
            typeof(TContext),
                items.Select(item => (Func<IServiceResolver, object>)(resolver => item(resolver))).ToArray()));
        });

        return new MiddlewarePipeline<TContext>(items);
    }

    /// <summary>
    /// Clears all middleware from the builder.
    /// </summary>
    /// <returns>The pipeline builder for method chaining.</returns>
    public IMiddlewarePipelineBuilder<TContext> Clear()
    {
        _items.Clear();
        return this;
    }
}