using Benzene.Mesh.Aggregator;
using Benzene.Mesh.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Mesh.Host.Test;

/// <summary>
/// work/enterprise/slice-3-discovery.md task 3.1: <c>registryDocuments</c> lets the host read a
/// discovery job's output back through the configured artifact store and union it with the
/// hand-written <c>services</c> list. Guards the union/precedence/failure rules the brief specifies:
/// <c>services</c> always wins a name clash, a missing or unparseable document degrades (logged, not
/// fatal) as long as something else could be read, and a <c>registryDocuments</c> list that reads
/// nothing at all fails startup loudly instead of silently serving an empty dashboard.
/// </summary>
public class StartupRegistryDocumentsTest
{
    private static (Startup startup, MeshServiceRegistry registry) BuildRegistry(string meshJson, string tempDir)
    {
        var configPath = Path.Combine(tempDir, "mesh.json");
        File.WriteAllText(configPath, meshJson);

        var configuration = new ConfigurationBuilder().AddJsonFile(configPath, optional: false).Build();
        var startup = new Startup(configuration);

        var services = new ServiceCollection();
        startup.ConfigureServices(services);
        var provider = services.BuildServiceProvider();

        return (startup, provider.GetRequiredService<MeshServiceRegistry>());
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"registry-documents-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void RegistryDocument_UnionsWithServices()
    {
        var tempDir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "registry.json"), MeshRegistryJson.Serialize(
                new MeshServiceRegistry(new[] { new MeshServiceRegistryEntry("discovered-svc", "http://discovered/spec", "http://discovered/health") })));

            var (_, registry) = BuildRegistry($$"""
                {
                  "artifactRootDirectory": {{Json(tempDir)}},
                  "registryDocuments": [ "registry.json" ],
                  "services": [
                    { "name": "static-svc", "specUrl": "http://static/spec", "healthUrl": "http://static/health" }
                  ]
                }
                """, tempDir);

            Assert.Equal(2, registry.Services.Length);
            Assert.Contains(registry.Services, s => s.Name == "discovered-svc");
            Assert.Contains(registry.Services, s => s.Name == "static-svc");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void RegistryDocument_NameClashWithServices_ServicesWins()
    {
        var tempDir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "registry.json"), MeshRegistryJson.Serialize(
                new MeshServiceRegistry(new[] { new MeshServiceRegistryEntry("orders-api", "http://discovered/spec", "http://discovered/health") })));

            var (_, registry) = BuildRegistry($$"""
                {
                  "artifactRootDirectory": {{Json(tempDir)}},
                  "registryDocuments": [ "registry.json" ],
                  "services": [
                    { "name": "orders-api", "specUrl": "http://static/spec", "healthUrl": "http://static/health" }
                  ]
                }
                """, tempDir);

            var entry = Assert.Single(registry.Services);
            Assert.Equal("http://static/health", entry.HealthUrl);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void OneMissingDocumentAmongSeveral_Degrades_KeepsWhatCouldBeRead()
    {
        var tempDir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "registry.json"), MeshRegistryJson.Serialize(
                new MeshServiceRegistry(new[] { new MeshServiceRegistryEntry("discovered-svc", "http://discovered/spec", "http://discovered/health") })));

            // "missing.json" is never written - TryReadAsync returns null for it.
            var (_, registry) = BuildRegistry($$"""
                {
                  "artifactRootDirectory": {{Json(tempDir)}},
                  "registryDocuments": [ "registry.json", "missing.json" ],
                  "services": []
                }
                """, tempDir);

            var entry = Assert.Single(registry.Services);
            Assert.Equal("discovered-svc", entry.Name);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void OneUnparseableDocumentAmongSeveral_Degrades_KeepsWhatCouldBeRead()
    {
        var tempDir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "good.json"), MeshRegistryJson.Serialize(
                new MeshServiceRegistry(new[] { new MeshServiceRegistryEntry("discovered-svc", "http://discovered/spec", "http://discovered/health") })));
            File.WriteAllText(Path.Combine(tempDir, "bad.json"), "{ not valid json ");

            var (_, registry) = BuildRegistry($$"""
                {
                  "artifactRootDirectory": {{Json(tempDir)}},
                  "registryDocuments": [ "good.json", "bad.json" ],
                  "services": []
                }
                """, tempDir);

            var entry = Assert.Single(registry.Services);
            Assert.Equal("discovered-svc", entry.Name);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void AllRegistryDocumentsUnreadable_Throws()
    {
        var tempDir = TempDir();
        try
        {
            var configPath = Path.Combine(tempDir, "mesh.json");
            File.WriteAllText(configPath, $$"""
                {
                  "artifactRootDirectory": {{Json(tempDir)}},
                  "registryDocuments": [ "missing1.json", "missing2.json" ],
                  "services": []
                }
                """);

            var configuration = new ConfigurationBuilder().AddJsonFile(configPath, optional: false).Build();

            var exception = Record.Exception(() => new Startup(configuration));

            Assert.IsType<InvalidOperationException>(exception);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void NoRegistryDocumentsConfigured_ServicesAloneStillWork()
    {
        var tempDir = TempDir();
        try
        {
            var (_, registry) = BuildRegistry($$"""
                {
                  "artifactRootDirectory": {{Json(tempDir)}},
                  "services": [
                    { "name": "static-svc", "specUrl": "http://static/spec", "healthUrl": "http://static/health" }
                  ]
                }
                """, tempDir);

            var entry = Assert.Single(registry.Services);
            Assert.Equal("static-svc", entry.Name);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // Windows-style backslashes in a temp path would otherwise break the JSON string literal built
    // by string interpolation above.
    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}
