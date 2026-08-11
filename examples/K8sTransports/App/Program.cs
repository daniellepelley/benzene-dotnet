using Benzene.AspNet.Core;
using Benzene.Examples.K8sTransports.App;
using Benzene.HostedService;

var builder = WebApplication.CreateBuilder(args);

// Listen on the port Kubernetes gives the container (the readinessProbe and Service target this).
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Two BenzeneStartUps, one WebApplicationBuilder, one process: WebApplicationBuilder.UseBenzene<T>
// (Benzene.AspNet.Core) and IHostBuilder.UseBenzene<T> (Benzene.HostedService, reached via
// builder.Host - the same IHostBuilder a Generic Host would use) both register against this one
// builder.Services. HttpStartup wires POST /orders onto Kestrel; WorkerStartup wires the SQS and
// Kafka consumers as ASP.NET Core IHostedServices that start/stop alongside it - see each Startup's
// own comment for why a single BenzeneStartUp can't do both (app.UseHttp/app.UseWorker no-op on the
// platform they don't own).
builder.UseBenzene<HttpStartup>();
builder.Host.UseBenzene<WorkerStartup>();

var app = builder.Build();
app.UseBenzene();
app.Run();
