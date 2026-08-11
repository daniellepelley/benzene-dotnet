# Self-hosted ASP.NET: one startup for HTTP + workers

Status: implemented — `UseAspNet` (`AspNetSelfHostExtensions`), `AspNetServerWorker`, and
`AspNetServerOptions` shipped in `Benzene.AspNet.Core`; `examples/K8sTransports` and the Kubernetes
guide rewritten to the one-startup shape; end-to-end coverage in
`test/Benzene.Core.Test/Hosting/UseAspNetWorkerTest.cs` (real socket, generic host, second worker
alongside). The open questions below resolved as proposed (name: `UseAspNet`; default URL:
`http://0.0.0.0:8080`, no env sniffing).

## The problem

`examples/K8sTransports` proves one process can host HTTP + SQS + Kafka, but the wiring is heavier
than the idea deserves: **two** `BenzeneStartUp` classes (`HttpStartup` + `WorkerStartup`) and two
`UseBenzene` calls in `Program.cs`, purely because `UseHttp` and `UseWorker` are platform-gated on
different builder types (`app is IAspApplicationBuilder` vs `app is WorkerApplicationBuilder`) and no
single builder satisfies both. Beyond the boilerplate, the shape is conceptually off for the case it
serves: ASP.NET Core is the *program shape* (`WebApplication.CreateBuilder` owns `Program.cs`) even
when it contributes nothing but a listening socket — no controllers, no ASP.NET middleware, just a
host for Benzene's own HTTP pipeline.

Every other transport already has the right shape for that case: `UseAwsLambda(aws => ...)`,
`worker.UseSqs(...)`, `worker.UseKafka(...)` — the transport appears *inside* the startup, and the
program shape stays neutral. HTTP is the one transport that can't be expressed that way.

## What should exist

An extension that hosts Kestrel **as a worker**, symmetric with `UseSqs`/`UseKafka`:

```csharp
public static IBenzeneWorkerStartup UseAspNet(
    this IBenzeneWorkerStartup app,
    Action<IMiddlewarePipelineBuilder<AspNetContext>> action,
    Action<AspNetServerOptions>? configure = null)
```

so the whole K8sTransports example collapses to **one** startup and a generic-host `Program.cs`:

```csharp
// Program.cs
IHost host = Host.CreateDefaultBuilder(args).UseBenzene<Startup>().Build();
await host.RunAsync();
```

```csharp
// Startup.cs - the only startup
public class Startup : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.UsingBenzene(x => x
            .AddMessageHandlers(new[] { typeof(PlaceOrderMessageHandler) })
            .AddHttpMessageHandlers());
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
        app.UseWorker(worker => worker
            .UseAspNet(
                asp => asp.UseMessageHandlers(),
                options => options.Urls = $"http://0.0.0.0:{configuration["PORT"] ?? "8080"}")
            .UseSqs(/* as today */)
            .UseKafka<Ignore, string>(/* as today */));
    }
}
```

The `configure` options action is the "pass in the URL or whatever else" seam: `Urls` covers the
common case, and an escape-hatch `Action<WebApplicationBuilder>` covers TLS/limits/logging without
this API growing a Kestrel facade.

## Why this works mechanically

The pieces already line up; nothing about the existing model has to bend.

**`AspNetApplication` is the exact seam needed.** It takes a *built*
`IMiddlewarePipeline<AspNetContext>` plus an `IServiceResolverFactory` and exposes
`SendAsync(HttpContext)`. `UseHttp` builds one and hands it to `AspApplicationBuilder.Add`, which
wires it as ASP.NET middleware. `UseAspNet` builds the identical object and instead hands it to a
new `AspNetServerWorker`, registered via `IBenzeneWorkerStartup.Add` like any other worker:

```csharp
// Inside UseAspNet - mirrors UseHttp line for line until the last step:
app.Register(x => x.AddBenzene().AddAspNetMessageHandlers());
var pipeline = app.Create<AspNetContext>();
// same SeedCancellationToken + UseBufferedRequestBody preamble as UseHttp
action(pipeline);
var builtPipeline = pipeline.Build();   // eager, same as UseHttp (Build() registers the
                                        // PipelineDescriptor into the container as a side effect)
var options = new AspNetServerOptions();
configure?.Invoke(options);
app.Add(factory => new AspNetServerWorker(new AspNetApplication(builtPipeline, factory), options));
return app;
```

**`AspNetServerWorker : IBenzeneWorker`** owns a deliberately empty Kestrel host:

```csharp
public async Task StartAsync(CancellationToken cancellationToken)
{
    var builder = WebApplication.CreateBuilder();
    _options.ConfigureBuilder?.Invoke(builder);        // escape hatch (TLS, logging, ...)
    _app = builder.Build();
    _app.Urls.Clear();
    _app.Urls.Add(_options.Urls);
    _app.Run(async context =>
    {
        await _entryPoint.SendAsync(context);
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;  // no "next" here - there
        }                                                                  // are no controllers to
    });                                                                    // fall through to
    await _app.StartAsync(cancellationToken);   // binds and RETURNS - non-blocking
}

public Task StopAsync(CancellationToken cancellationToken) => _app?.StopAsync(cancellationToken) ?? Task.CompletedTask;
```

- `StartAsync` returns once the socket is bound, so it composes with `CompositeBenzeneWorker`'s
  parallel start/rollback and the (recently fixed) `BenzeneHostedServiceAdapter` with no special
  casing. A bind failure throws → the composite rolls back the already-started workers → host start
  fails loudly.
- `StopAsync` → `WebApplication.StopAsync` gives Kestrel's normal graceful drain.

**DI is correct by construction, not by care.** The inner `WebApplication` resolves *nothing* — the
pipeline and the resolver factory both come from the outer generic host, whose `IServiceCollection`
is the one `ConfigureServices` populated (that's how `Benzene.HostedService`'s
`IHostBuilder.UseBenzene<T>` already wires `WorkerApplicationBuilder`). The inner app is a socket
plus one delegate. This sidesteps the entire singleton-split bug class that
`AspApplicationBuilder`'s two-phase `IServiceCollection`/`Finish()` lifecycle exists to prevent —
there is no second provider for anything Benzene resolves. Start-up checks also come for free:
`HostBuilderExtensions.UseBenzene` already runs `RunStartUpChecks()` in its hosted-service factory,
which now covers the HTTP pipeline's registrations too.

**Packaging.** `UseAspNet`, `AspNetServerWorker`, and `AspNetServerOptions` live in
`Benzene.AspNet.Core` (it already has the `Microsoft.AspNetCore.App` framework reference), which
gains a `ProjectReference` to `Benzene.SelfHost` — the same dependency direction every worker
transport already has (`Benzene.Aws.Sqs` → `SelfHost`, `Benzene.Kafka.Core` → `SelfHost`). No cycle:
`SelfHost` references only abstractions/core.

## What stays exactly as it is

- **Embedded mode** — `WebApplicationBuilder.UseBenzene<TStartUp>()` + `app.UseBenzene()`, Benzene
  as part of a larger ASP.NET pipeline alongside controllers/minimal APIs (the strangler-fig story).
  Untouched; still the right choice whenever the process genuinely *is* an ASP.NET app. Its
  `Add`-as-middleware path, the two-phase lifecycle, and `UseHttp` keep their exact contracts.
- **The two-startup combination** (`builder.UseBenzene<HttpStartup>()` +
  `builder.Host.UseBenzene<WorkerStartup>()`) remains valid — it becomes the documented shape for
  "controllers **and** workers in one process," rather than the only way to get HTTP + workers at
  all. Docs and `examples/K8sTransports` switch to `UseAspNet` as the primary shape.

## Alternatives considered

- **Widen `UseWorker` to match the ASP.NET builder** (give `AspApplicationBuilder` a `Workers`
  surface behind a shared interface, have `WebApplicationBuilder.UseBenzene<T>` also register the
  composite worker as an `IHostedService`). Smaller diff and it does collapse the two startups — but
  `Program.cs` stays ASP.NET-shaped, HTTP stays privileged over the other transports, and the
  UseAwsLambda-symmetry ask isn't met. Possible later addition for the "controllers + workers, one
  startup" niche; not this change.
- **A dependency-free HTTP listener host** (HttpListener/managed socket, skipping ASP.NET
  entirely). Rejected: that's a server surface to own (TLS, HTTP/2, graceful drain, header limits)
  that Kestrel already ships, for the benefit of dropping a framework reference the package already
  carries.

## Open questions

1. **Naming.** `UseAspNet` (proposed — names the host technology, like `UseSqs`/`UseKafka` name
   theirs) vs `UseAspNetServer` vs `UseHttpServer`. `UseHttp` is taken by the pipeline-level verb
   and must keep meaning "the HTTP pipeline," not "the HTTP server."
2. **Default URL.** Proposed: explicit-only via `options.Urls`, defaulting to `http://0.0.0.0:8080`
   — no `PORT` env sniffing inside the framework (the example passes `PORT` through, as today).
3. **Cross-port note.** Go/Python/TypeScript already have this shape natively (goroutine /
   `asyncio.gather` / fire-and-forget around their standard HTTP servers) — this brings .NET to
   parity. Composition-root wiring is per-port idiom, so no spec change; worth a porting-guide
   sentence once implemented.

## Estimated shape of the change

Three new files in `Benzene.AspNet.Core` (`AspNetServerOptions`, `AspNetServerWorker`, the
`UseAspNet` extension — the pipeline preamble refactored to be shared with `UseHttp` rather than
duplicated), one csproj reference, tests (worker start/stop lifecycle against a real socket; a
`UnifiedStartUpTest`-style test proving one startup wires all three transports), then the
`examples/K8sTransports` + `docs/getting-started-kubernetes.md` rewrite down to one startup. No
existing public contract changes.
