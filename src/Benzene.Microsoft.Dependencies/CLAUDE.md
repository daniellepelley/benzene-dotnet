# Benzene.Microsoft.Dependencies

## What this package does
Microsoft.Extensions.DependencyInjection integration for Benzene. Adapts Benzene's DI abstractions to Microsoft's IServiceCollection and IServiceProvider, enabling use of Microsoft's built-in DI container.

## Key types/interfaces
- `Extensions.UsingBenzene(this IServiceCollection)` / `UsingBenzene(this IServiceCollection, Action<IBenzeneServiceContainer>)` -
  the entry point (also calls `services.AddLogging()`).
- `MicrosoftBenzeneServiceContainer : IBenzeneServiceContainer` - a Benzene container view over an
  `IServiceCollection` (Benzene scoped/singleton/transient map directly to MEL lifetimes).
- `MicrosoftServiceResolverAdapter : IServiceResolver` - resolves from an `IServiceProvider` scope. On a
  failed `GetService<T>()` it wraps the container's exception in a `BenzeneException` enriched with a
  missing-registration hint via `RegistrationErrorHandler.Describe(typeof(T), ex)` - keyed on the
  requested type (not by parsing MEL's message text), and throw-safe, so the original error is always
  preserved as `InnerException` and never masked. See `Benzene.Core`'s `RegistrationCheck`.
- `RegistrationErrorHandler` - thin, cached (`Lazy<RegistrationCheck>`) entry point for the above;
  delegates to `Benzene.Core.DI.RegistrationCheck`. No longer matches on MEL-specific message wording.
- `MicrosoftServiceResolverFactory : IServiceResolverFactory` - builds the provider and opens a MEL scope
  (`IServiceProvider.CreateScope()`) per Benzene scope. Both this type's `Dispose()` (root
  provider/container) and `MicrosoftServiceResolverAdapter`'s `Dispose()` (per-message/per-scope) bridge
  to the wrapped provider/scope's own `DisposeAsync()` - with an unbounded wait - whenever it's
  available, rather than always calling its synchronous `Dispose()` (#266, round 16). This matters for
  YOUR registrations: Microsoft.Extensions.DependencyInjection's synchronous scope/provider `Dispose()`
  throws `InvalidOperationException` the moment it has to tear down a container-owned instance
  (scoped, transient, or singleton) that implements only `IAsyncDisposable` - an entirely ordinary
  shape for an async-native client/connection - which used to crash (and leak - the resource's own
  `DisposeAsync` never ran) every message through `MiddlewareApplication.HandleAsync`'s per-message
  scope teardown. The bridge is unbounded (unlike the bounded-5s pattern Benzene uses for best-effort
  telemetry flushes) because it's awaiting the caller's OWN disposal code, not a network flush -
  abandoning it early would silently leak resources by design. `Benzene.Abstractions.DI.IServiceResolver`
  itself is still `IDisposable`-only (no `await using` support) - see the `[OPEN]` entry in
  `work/outstanding-bugs.md` about whether that should eventually change.
- `BenzeneStartUp` - abstract `IStartUp<IServiceCollection, IConfiguration, IBenzeneApplicationBuilder>`
  base for platform-neutral startup classes.

## When to use this package
- When using Benzene with ASP.NET Core
- For .NET Core/5+ applications
- When you want Microsoft's built-in DI container
- Standard choice for modern .NET applications

## Dependencies on other Benzene packages
- **Benzene.Abstractions** - Core abstractions (DI)
- **Microsoft.Extensions.DependencyInjection** - Microsoft DI

## Important conventions
- Register Benzene services in `IServiceCollection`
- Scoped, Singleton, Transient lifetimes mapped
- Works seamlessly with ASP.NET Core DI
- No additional DI container needed
- `UsingBenzene` calls `services.AddLogging()` so `ILogger<T>`/`ILoggerFactory` always resolve;
  host logging configuration (before or after `UsingBenzene`) is respected because MEL's
  registration is TryAdd-based and composable
