using Microsoft.Extensions.Hosting;

Host.CreateDefaultBuilder(args)
    .ConfigureWebHost(webBuilder => webBuilder
        .UseKestrel()
        .UseStartup<Startup>())
    .Build()
    .Run();
