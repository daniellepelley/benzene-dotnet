using Benzene.AspNet.Core;
using Benzene.Examples.K8sMesh.Mesh;

// Listen on the port the container is given (Kubernetes probes/Service target this); everything else
// is BenzeneWebHost, the shorthand for the embedded ASP.NET triangle.
await BenzeneWebHost.RunAsync<Startup>(args, builder => builder.WebHost.UseUrls(
    $"http://0.0.0.0:{Environment.GetEnvironmentVariable("PORT") ?? "8080"}"));
