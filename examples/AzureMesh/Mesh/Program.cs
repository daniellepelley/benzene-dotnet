using Benzene.AspNet.Core;
using Benzene.Examples.AzureMesh.Mesh;

// The container is told which port to listen on; everything else is BenzeneWebHost, the shorthand
// for the embedded ASP.NET triangle (see its docs for the three explicit calls it composes).
await BenzeneWebHost.RunAsync<Startup>(args, builder => builder.WebHost.UseUrls(
    $"http://0.0.0.0:{Environment.GetEnvironmentVariable("PORT") ?? "8080"}"));
