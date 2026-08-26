# Benzene.Autofac

## What this package does
Adapts Benzene's DI abstractions onto Autofac. It is a **thin adapter**: `UsingBenzene(...)` on your own
Autofac `ContainerBuilder` gives Benzene an `IBenzeneServiceContainer` view over it, so Benzene's
registrations land in your Autofac container and Benzene resolves services through Autofac's
`ILifetimeScope`.

## Key types/interfaces
- `Extensions.UsingBenzene(this ContainerBuilder)` / `UsingBenzene(this ContainerBuilder, Action<IBenzeneServiceContainer>)` -
  the entry point.
- `AutofacBenzeneServiceContainer : IBenzeneServiceContainer` - maps Benzene lifetimes to Autofac:
  `AddScoped` → `InstancePerLifetimeScope`, `AddSingleton` → `SingleInstance`, `AddTransient` →
  `InstancePerDependency` (open generics via `RegisterGeneric`).
  - `IsTypeRegistered` is backed by an explicit `HashSet<Type>` maintained by every `AddXxx`/
    `AddServiceResolver` call, **not** Autofac's `ComponentRegistryBuilder`, which stays empty until
    `ContainerBuilder.Build()` runs - reading it pre-`Build()` (as this method used to) always reports
    `false`, silently turning every `TryAdd*` into an unconditional last-write-wins `Add*`. This mirrors
    `MicrosoftBenzeneServiceContainer`, which checks its live, always-current `IServiceCollection`.
  - `CreateServiceResolverFactory()` builds the underlying `IContainer` **once, lazily**, on its first
    call (`ContainerBuilder.Build()` can only run once per builder - a second call throws), then hands
    out cheap, non-owning `AutofacServiceResolverFactory` instances wrapping that already-built container
    on every call including the first - safe to call repeatedly, matching the Microsoft adapter. This is
    what makes `Benzene.Grpc.AspNet`'s `UseGrpc()` work with Autofac: `GrpcMethodHandlerFactory.Create()`
    calls `CreateServiceResolverFactory()` on every request.
- `AutofacServiceResolverAdapter : IServiceResolver` - resolves from an Autofac `IComponentContext`/scope.
  On a failed `GetService<T>()` it wraps Autofac's exception in a `BenzeneException` enriched with a
  missing-registration hint via `RegistrationErrorHandler.Describe(typeof(T), ex)` - keyed on the
  requested type (not by parsing Autofac's message text), and throw-safe, so the original error is always
  preserved as `InnerException`. The diagnostic logic is shared with the Microsoft adapter in
  `Benzene.Core.DI.RegistrationCheck`; this package no longer matches Autofac-specific message wording.
  - The single-`IComponentContext`-arg constructor (used by `AddServiceResolver()`'s registration, and by
    every `AddScoped/AddTransient/AddSingleton(Func<IServiceResolver,T>)` overload) builds its
    `IServiceResolverFactory` **lazily** the first time one is asked for, wrapping the ambient
    `IComponentContext` (always the current `ILifetimeScope` in practice, when resolved through Autofac's
    own machinery) - mirrors `MicrosoftServiceResolverAdapter.ResolverFactory`'s `??=` pattern. A
    constructor-injected `IServiceResolver` can therefore always resolve its own
    `IServiceResolverFactory` (e.g. to open a nested scope) instead of hitting a null field.
- `AutofacServiceResolverFactory : IServiceResolverFactory, IAsyncDisposable` - wraps an Autofac
  `ILifetimeScope` (a freshly-built root `IContainer`, or any already-open scope) and opens a nested
  lifetime scope per Benzene scope. Two constructors: `(ContainerBuilder)` builds and **owns** a new
  `IContainer` (registers the logging fallbacks below, then `Build()`s - use this at most once per
  builder); `(ILifetimeScope)` wraps an existing, already-open scope **without** owning it - `Dispose()`/
  `DisposeAsync()` are no-ops there, since the scope's lifetime belongs to whoever supplied it (this is
  what `AutofacBenzeneServiceContainer.CreateServiceResolverFactory()` and the lazy `IServiceResolverFactory`
  fallback on `AutofacServiceResolverAdapter` both use, so neither can double-`Build()` or dispose a
  container something else still needs).

## When to use this package
- When your application already uses Autofac and you want Benzene to register/resolve through it.

## Deliberate boundaries
- This package adds **no** Autofac module/decorator/interceptor wrappers of its own. Because
  `UsingBenzene` operates on your real `ContainerBuilder`, you keep full access to Autofac's native
  features (modules, decorators, interceptors) and use them directly — Benzene neither hides nor
  re-exposes them.

## Dependencies on other Benzene packages
- **Benzene.Abstractions** - the DI abstractions (`IBenzeneServiceContainer`, `IServiceResolver`, factories)
- **Benzene.Core**
- **Autofac** (6.5.0)

## Important conventions
- Benzene `AddScoped`/`AddSingleton`/`AddTransient` map to Autofac lifetimes as above.
- `AutofacServiceResolverFactory` registers `NullLoggerFactory`/open-generic `Logger<>` fallbacks
  (via `IfNotRegistered`) so `ILogger<T>` always resolves; register your own `ILoggerFactory`
  instance (e.g. `LoggerFactory.Create(x => x.AddConsole())`) to enable real logging — user
  registrations always win over the fallbacks.
