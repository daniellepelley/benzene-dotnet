# Benzene.HostedService

## What this package does
Bridges a Benzene self-hosted worker (`IBenzeneWorker`, from `Benzene.SelfHost`) onto the .NET generic
host's `IHostedService`, so a Benzene worker starts/stops with the host. This is the glue between
`Benzene.SelfHost`'s worker model and `Microsoft.Extensions.Hosting`.

## Key types/interfaces
- `BenzeneHost` - static entry point: `RunAsync<TStartUp>(args, configureHost, ct)`,
  `Run<TStartUp>(...)`, `Build<TStartUp>(...)`. Builds `Host.CreateDefaultBuilder(args)`, applies
  `UseBenzene<TStartUp>()` and runs it, so a service's `Program.cs` is one line and the STARTUP is the
  only place hosting is described. `configureHost` is applied BEFORE `UseBenzene` so a caller can
  replace a `TryAdd`ed Benzene default; it does not override what the startup itself plain-`Add`s.
- `BenzeneHostedServiceAdapter : IHostedService` - wraps an `IBenzeneWorker`; `StartAsync`/`StopAsync`
  delegate straight to the worker's `StartAsync`/`StopAsync` (graceful shutdown is the worker's own
  drain logic — see `Benzene.SelfHost`). Also observes the worker's own task for an unhandled fault
  the moment it happens (not only when the host later gets around to calling `StopAsync`): with an
  optional `ILogger<BenzeneHostedServiceAdapter>` it logs the fault at `Critical`, and with an optional
  `IHostApplicationLifetime` it calls `StopApplication()` — matching `BackgroundService`'s modern
  default of stopping the whole host on an unhandled worker fault, rather than leaving the process "up"
  with a silently dead worker. Both are optional constructor parameters (default `null`); `UseBenzene`
  below wires both from DI, `BuildHostedService` below has no resolver to supply them.
- `HostBuilderExtensions.UseBenzene<TStartUp>(this IHostBuilder)` - runs a platform-neutral
  `BenzeneStartUp` as a hosted worker: builds a `WorkerApplicationBuilder`, runs `ConfigureServices`/
  `Configure`, and registers the resulting worker as a singleton `IHostedService`.
- `BenzeneWorkerExtensions.BuildHostedService(this IBenzeneWorkerBuilder)` - wraps a built worker in a
  `BenzeneHostedServiceAdapter` directly.

## When to use this package
- When processing background messages in ASP.NET Core
- For queue consumers as hosted services
- For scheduled tasks
- For long-running background operations

## Dependencies on other Benzene packages
- **Benzene.SelfHost** - `IBenzeneWorker`, `WorkerApplicationBuilder`, `IBenzeneWorkerBuilder`
- **Benzene.Microsoft.Dependencies** - `MicrosoftBenzeneServiceContainer`/`MicrosoftServiceResolverFactory`, `BenzeneStartUp`
- **Benzene.Core** / **Benzene.Core.Middleware**
- **Microsoft.Extensions.Hosting.Abstractions** - `IHostedService`, `IHostBuilder`
- **Microsoft.Extensions.Hosting** - `Host.CreateDefaultBuilder`, for `BenzeneHost`. Floor is 6.0.0
  (the lowest with that API); the Abstractions floor was raised to match, since Hosting 6.0.0 requires
  it anyway.

## Important conventions
- `BenzeneHost` owns the host, so it is for services whose host is Benzene's to own. A larger ASP.NET
  Core app embeds Benzene instead (`WebApplicationBuilder.UseBenzene<TStartUp>()` + `app.UseBenzene()`).
- Registered as a singleton `IHostedService`, so it starts/stops with the generic host.
- Graceful shutdown is delegated to the wrapped `IBenzeneWorker.StopAsync` (bounded drain).
- Suitable for queue/stream consumers (e.g. `Benzene.Kafka.Core`) and self-hosted HTTP workers.
