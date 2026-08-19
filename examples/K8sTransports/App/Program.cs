using Benzene.Examples.K8sTransports.App;
using Benzene.HostedService;

// The whole entry point. Startup wires all three transports (HTTP via UseAspNet, SQS, Kafka) as
// workers - see Startup.cs - and this file would not change if a fourth were added, which is the
// point of keeping hosting in the startup.
await BenzeneHost.RunAsync<Startup>(args);
