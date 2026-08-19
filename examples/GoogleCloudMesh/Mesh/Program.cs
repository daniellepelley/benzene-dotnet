using Benzene.AspNet.Core;
using Benzene.Examples.GoogleCloudMesh.Mesh;

// Cloud Run injects the port to listen on via the PORT env var - see
// https://cloud.google.com/run/docs/container-contract#port
// Everything else (build the host, run the startup, wire Benzene into the request pipeline, run) is
// BenzeneWebHost; its docs name the three explicit calls it composes.
await BenzeneWebHost.RunAsync<Startup>(args, builder => builder.WebHost.UseUrls(
    $"http://0.0.0.0:{Environment.GetEnvironmentVariable("PORT") ?? "8080"}"));
