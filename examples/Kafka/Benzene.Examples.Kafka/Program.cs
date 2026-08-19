using Benzene.Examples.Kafka;
using Benzene.HostedService;

// The whole entry point. StartUp declares the Kafka consumer (and would declare any other transport)
// in Configure, so this file does not change when the service grows one.
await BenzeneHost.RunAsync<StartUp>(args);
