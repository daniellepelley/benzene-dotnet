using Benzene.Examples.K8sTransports.App;
using Benzene.HostedService;
using Microsoft.Extensions.Hosting;

// The plain generic host - nothing ASP.NET-shaped here. Startup wires all three transports (HTTP
// via UseAspNet, SQS, Kafka) as workers; see Startup.cs.
IHost host = Host.CreateDefaultBuilder(args)
    .UseBenzene<Startup>()
    .Build();

await host.RunAsync();
