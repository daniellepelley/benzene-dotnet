using Benzene.AspNet.Core;
using Benzene.Example.Asp.Minimal;
using Microsoft.AspNetCore.Builder;

// DELIBERATELY the explicit form: this example exists to show what the embedded ASP.NET path
// actually does, call by call. The one-line shorthand over exactly these calls is
// `await BenzeneWebHost.RunAsync<StartUp>(args);` - see BenzeneWebHost's docs, and
// examples/Cloudflare for it in use.
var builder = WebApplication.CreateBuilder(args);

// Run StartUp.GetConfiguration + StartUp.ConfigureServices, stashing StartUp for the call below.
builder.UseBenzene<StartUp>();

var app = builder.Build();

// Run StartUp.Configure against the built pipeline, wiring Benzene into the request pipeline.
app.UseBenzene();

app.Run();
