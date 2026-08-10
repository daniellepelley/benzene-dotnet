using Benzene.Abstractions.DI;
using Benzene.Abstractions.Hosting;
using Benzene.Abstractions.Middleware;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.AspNet.Core;

/// <summary>
/// Default implementation of <see cref="IAspApplicationBuilder"/>. Wires entry point applications
/// directly into the ASP.NET Core request pipeline via <see cref="IApplicationBuilder.Use(System.Func{RequestDelegate,RequestDelegate})"/>.
/// Also implements the platform-neutral <see cref="IBenzeneApplicationBuilder"/> so a <see cref="BenzeneStartUp"/>
/// can be configured identically whether hosted on ASP.NET Core or any other Benzene host.
/// </summary>
public class AspApplicationBuilder : IAspApplicationBuilder, IBenzeneApplicationBuilder
{
    /// <summary>The platform identifier reported by <see cref="Platform"/>.</summary>
    public const string PlatformName = "AspNet";

    /// <inheritdoc />
    public string Platform => PlatformName;

    // Deferred entry-point factories recorded by Add() during the pre-Build registration phase (see
    // the IServiceCollection constructor below) - there's no live IApplicationBuilder or correct root
    // IServiceProvider to wire them against yet. Finish() drains this once the application is built.
    private readonly List<Func<IServiceResolverFactory, IEntryPointMiddlewareApplication<HttpContext>>> _pendingEntryPoints = new();
    private IApplicationBuilder? _applicationBuilder;
    private readonly IBenzeneServiceContainer _benzeneServiceContainer;

    // True for the IServiceCollection constructor's pre-Build/Finish() lifecycle, false for the
    // legacy IApplicationBuilder constructor's single-call lifecycle - see Add()'s remarks for why
    // this changes how it resolves the service resolver factory.
    private readonly bool _isRegistrationPhase;

    /// <summary>
    /// Initializes a new instance of the <see cref="AspApplicationBuilder"/> class for the pre-Build
    /// registration phase, wrapping the host's own <see cref="IServiceCollection"/> directly.
    /// </summary>
    /// <param name="services">
    /// The <c>WebApplicationBuilder</c>'s own service collection - not yet sealed, since
    /// <c>Build()</c> hasn't run - so registrations land in the exact same collection the host's
    /// eventual root provider is built from. This is what lets <see cref="Finish"/> resolve entry
    /// points from that single, real root provider instead of a second one built separately from a
    /// clone: registering everything before <c>Build()</c> removes the need for a clone at all. See
    /// <see cref="Finish"/> for the other half of this two-phase lifecycle.
    /// </param>
    public AspApplicationBuilder(IServiceCollection services)
    {
        _benzeneServiceContainer = new MicrosoftBenzeneServiceContainer(services);
        _isRegistrationPhase = true;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AspApplicationBuilder"/> class for the legacy,
    /// single-call, post-Build-only usage (<see cref="BenzeneExtensions.UseBenzene(IApplicationBuilder,Action{IAspApplicationBuilder})"/>),
    /// reopening the <see cref="MicrosoftBenzeneServiceContainer"/> resolved from the application's
    /// root service provider so further registrations can be added to it.
    /// </summary>
    /// <param name="applicationBuilder">The ASP.NET Core application builder to wire entry point applications into.</param>
    /// <remarks>
    /// This path has no pre-Build phase to register into instead (the caller only ever has an already-
    /// built <see cref="IApplicationBuilder"/> to hand it), so it still resolves message handlers
    /// through a second <see cref="Microsoft.Extensions.DependencyInjection.IServiceProvider"/> built
    /// from a clone of the registrations - meaning a singleton registered outside this container
    /// (e.g. a plain <c>IHostedService</c>) is a different instance from the one this container
    /// resolves. The primary <see cref="BenzeneStartUp"/>-based path
    /// (<see cref="BenzeneExtensions.UseBenzene{TStartUp}"/> + <see cref="BenzeneExtensions.UseBenzene(IApplicationBuilder)"/>)
    /// does not have this limitation - prefer it when that matters.
    /// </remarks>
    public AspApplicationBuilder(IApplicationBuilder applicationBuilder)
    {
        _applicationBuilder = applicationBuilder;

        var microsoftBenzeneServiceContainer =
            new MicrosoftServiceResolverFactory(applicationBuilder.ApplicationServices)
                    .CreateScope()
                    .GetService<IBenzeneServiceContainer>() as
                MicrosoftBenzeneServiceContainer;

        microsoftBenzeneServiceContainer.Reopen();
        _benzeneServiceContainer = microsoftBenzeneServiceContainer;
    }

    /// <summary>
    /// Registers a factory for an entry point application, adding ASP.NET Core middleware that runs it
    /// against each incoming request.
    /// </summary>
    /// <param name="func">A factory that creates the entry point application given the current invocation's service resolver factory.</param>
    /// <remarks>
    /// The registered middleware calls <c>next()</c> to continue the ASP.NET Core pipeline only if the
    /// response hasn't already started, allowing subsequent middleware (e.g. MVC endpoints) to still
    /// handle requests this entry point application doesn't respond to. During the pre-Build
    /// registration phase (see the <see cref="IServiceCollection"/> constructor), <paramref name="func"/>
    /// is only recorded - <see cref="Finish"/> is what actually invokes it and wires the result.
    /// </remarks>
    public void Add(Func<IServiceResolverFactory, IEntryPointMiddlewareApplication<HttpContext>> func)
    {
        if (_applicationBuilder is not { } applicationBuilder)
        {
            _pendingEntryPoints.Add(func);
            return;
        }

        // For the pre-Build/Finish() lifecycle, resolve directly against the host's own real root
        // provider instead of _benzeneServiceContainer.CreateServiceResolverFactory() - that method
        // always builds its own separate provider from the underlying IServiceCollection (see
        // MicrosoftBenzeneServiceContainer.CreateServiceResolverFactory()), which would reintroduce
        // the exact singleton-split bug this two-phase lifecycle exists to avoid, even though
        // registrations now correctly land in one collection. The legacy single-call lifecycle has no
        // such root provider available any earlier than this, so it keeps the old (imperfect)
        // behavior - see that constructor's remarks.
        var serviceResolverFactory = _isRegistrationPhase
            ? new MicrosoftServiceResolverFactory(applicationBuilder.ApplicationServices)
            : _benzeneServiceContainer.CreateServiceResolverFactory();
        var entryPoint = func(serviceResolverFactory);

        applicationBuilder.Use(async (context, next) =>
        {
            await entryPoint.SendAsync(context);
            if (!context.Response.HasStarted)
            {
                await next();
            }
        });
    }

    /// <summary>
    /// Completes the pre-Build registration phase against the now-built application: wires every
    /// entry point <see cref="Add"/> deferred, using the host's real root
    /// <see cref="IApplicationBuilder.ApplicationServices"/> to resolve message handlers - the same
    /// provider anything else registered before <c>Build()</c> (a plain <c>IHostedService</c>, a
    /// controller, ...) resolves from, so singletons are never split across two providers. A no-op if
    /// this instance was constructed for the post-Build-only path instead (see the
    /// <see cref="IApplicationBuilder"/> constructor's remarks).
    /// </summary>
    /// <param name="applicationBuilder">The now-built ASP.NET Core application builder.</param>
    public void Finish(IApplicationBuilder applicationBuilder)
    {
        if (_applicationBuilder is not null)
        {
            return;
        }

        _applicationBuilder = applicationBuilder;
        foreach (var func in _pendingEntryPoints)
        {
            Add(func);
        }
        _pendingEntryPoints.Clear();
    }

    /// <summary>
    /// Registers services with the underlying service container.
    /// </summary>
    /// <param name="action">The action that performs the registration.</param>
    public void Register(Action<IBenzeneServiceContainer> action)
    {
        action(_benzeneServiceContainer);
    }

    /// <summary>
    /// Creates a new middleware pipeline builder for a given context type, sharing this builder's
    /// underlying service container.
    /// </summary>
    /// <typeparam name="TNewContext">The context type the pipeline operates on.</typeparam>
    /// <returns>The created pipeline builder.</returns>
    public IMiddlewarePipelineBuilder<TNewContext> Create<TNewContext>()
    {
        return new MiddlewarePipelineBuilder<TNewContext>(_benzeneServiceContainer);
    }
}
