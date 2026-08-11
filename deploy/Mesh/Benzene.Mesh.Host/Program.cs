using Benzene.Mesh.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((_, config) =>
    {
        // MESH_CONFIG_PATH points at a bind-mounted mesh.json (see deploy/Mesh/README.md) - the
        // primary config path for a multi-service registry. Individual scalars (ArtifactRootDirectory,
        // PollIntervalSeconds) can also be overridden via plain environment variables, since
        // Host.CreateDefaultBuilder already adds those; only the JSON file needs wiring explicitly here.
        MeshConfigLoader.ConfigureMeshConfig(config, Environment.GetEnvironmentVariable("MESH_CONFIG_PATH"));
    })
    .ConfigureWebHost(webBuilder => webBuilder
        .UseKestrel()
        .UseStartup<Startup>())
    .Build()
    .Run();
