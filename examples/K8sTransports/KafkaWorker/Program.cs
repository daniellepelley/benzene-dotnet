using Benzene.HostedService;
using Benzene.Examples.K8sTransports.KafkaWorker;
using Microsoft.Extensions.Hosting;

IHost host = Host.CreateDefaultBuilder(args)
    .UseBenzene<Startup>()
    .Build();

await host.RunAsync();
