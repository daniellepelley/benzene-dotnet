using Benzene.AspNet.Core;
using Benzene.Examples.K8sTransports.Api;

var builder = WebApplication.CreateBuilder(args);

// Listen on the port Kubernetes gives the container (the readinessProbe and Service target this).
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.UseBenzene<Startup>();

var app = builder.Build();
app.UseBenzene();
app.Run();
