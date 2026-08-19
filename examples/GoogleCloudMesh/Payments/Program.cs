using Benzene.AspNet.Core;
using Benzene.Examples.GoogleCloudMesh.Payments;

// Cloud Run injects the port to listen on via the PORT env var - see
// https://cloud.google.com/run/docs/container-contract#port
await BenzeneWebHost.RunAsync<Startup>(args, builder => builder.WebHost.UseUrls(
    $"http://0.0.0.0:{Environment.GetEnvironmentVariable("PORT") ?? "8080"}"));
