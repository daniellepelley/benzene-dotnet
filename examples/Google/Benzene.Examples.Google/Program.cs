using Benzene.AspNet.Core;
using Benzene.Examples.Google;

// Cloud Run injects the port to listen on via the PORT env var - see
// https://cloud.google.com/run/docs/container-contract#port
// BenzeneWebHost is the shorthand for the embedded ASP.NET triangle (CreateBuilder /
// builder.UseBenzene<Startup>() / app.UseBenzene() / Run) - see its docs for the explicit form.
await BenzeneWebHost.RunAsync<Startup>(args, builder => builder.WebHost.UseUrls(
    $"http://0.0.0.0:{Environment.GetEnvironmentVariable("PORT") ?? "8080"}"));
