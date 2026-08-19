using Benzene.Example.Azure.Worker;
using Benzene.HostedService;

// A plain .NET Worker Service - no Azure Functions runtime. Benzene owns the process and hosts the
// Service Bus and Event Hub consumers as background IHostedServices (see StartUp.Configure).
//
// BenzeneHost.RunAsync is Benzene.HostedService's generic-host entry point; using it also removes the
// old hazard here, where a stray `using Benzene.Azure.Function.Core;` would silently bind
// IHostBuilder.UseBenzene<StartUp>() to the Azure Functions extension of the same name instead.
await BenzeneHost.RunAsync<StartUp>(args);
