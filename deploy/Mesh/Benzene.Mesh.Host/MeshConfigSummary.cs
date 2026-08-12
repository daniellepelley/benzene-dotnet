namespace Benzene.Mesh.Host;

/// <summary>
/// Formats the resolved <see cref="MeshHostConfig"/> as a human-readable, secret-redacted summary -
/// logged once at startup, before the first poll (see <c>Program.cs</c>). This is the single
/// highest-value support tool in the package: "it isn't picking up my Tempo URL" is otherwise
/// unanswerable without a debugger.
/// </summary>
/// <remarks>
/// Redaction is by key name, not by section: any option key containing <c>password</c>,
/// <c>secret</c>, <c>token</c>, <c>key</c>, <c>credential</c>, or <c>connectionstring</c>
/// (case-insensitive) prints its value as <c>***</c> - the key itself is always printed, never
/// dropped, so an operator can see that a value was supplied even when they can't see what it was.
/// Credentials should never reach <c>mesh.json</c> in the first place (see
/// <c>work/enterprise/README.md</c>'s house rule), but this is the second line of defence for a
/// value someone pasted in against that advice, or a connection string that embeds one.
/// </remarks>
public static class MeshConfigSummary
{
    private static readonly string[] SecretKeyFragments =
        { "password", "secret", "token", "key", "credential", "connectionstring" };

    /// <summary>Builds the multi-line summary for <paramref name="config"/>.</summary>
    public static string Format(MeshHostConfig config)
    {
        var lines = new List<string>
        {
            "Effective mesh configuration:",
            $"  artifactRootDirectory: {config.ArtifactRootDirectory}",
            $"  pollIntervalSeconds: {config.PollIntervalSeconds}",
            $"  artifactStore: type={config.ArtifactStore.Type}{FormatOptions(config.ArtifactStore.Options)}",
            $"  services: {config.Services.Length}",
        };

        foreach (var service in config.Services)
        {
            lines.Add($"    - {service.Name}: source={service.Source}{FormatOptions(service.SourceOptions)}");
        }

        lines.Add(config.RegistryDocuments.Length == 0
            ? "  registryDocuments: (none)"
            : $"  registryDocuments: {string.Join(", ", config.RegistryDocuments)}");

        lines.Add(config.Usage.Length == 0 ? "  usage: (none)" : "  usage:");
        foreach (var usage in config.Usage)
        {
            lines.Add($"    - source={usage.Source}{FormatOptions(usage.Options)}");
        }

        lines.Add($"  fleet: source={config.Fleet.Source}{FormatOptions(config.Fleet.Options)}");
        lines.Add($"  topology: source={config.Topology.Source}{FormatOptions(config.Topology.Options)}");
        lines.Add($"  dispatch: enabled={config.Dispatch.Enabled}, allowInProduction={config.Dispatch.AllowInProduction}");

        var authLine = $"  auth: mode={config.Auth.Mode}, ingestion={config.Auth.Ingestion.Mode}";
        if (config.Auth.AllowedEmailDomains.Length > 0)
        {
            authLine += $", allowedEmailDomains={string.Join(",", config.Auth.AllowedEmailDomains)}";
        }

        if (config.Auth.RequiredGroups.Length > 0)
        {
            authLine += $", requiredGroups={string.Join(",", config.Auth.RequiredGroups)}";
        }

        lines.Add(authLine);

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatOptions(Dictionary<string, string>? options)
    {
        if (options == null || options.Count == 0)
        {
            return string.Empty;
        }

        var formatted = options.Select(kv => $"{kv.Key}={(IsSecretKey(kv.Key) ? "***" : kv.Value)}");
        return $" [{string.Join(", ", formatted)}]";
    }

    /// <summary>
    /// Whether <paramref name="key"/> looks like it names a secret - see the class remarks for the
    /// exact fragment list. Internal, not private, so <c>MeshConfigSummaryTest</c> can assert the
    /// rule directly, not just its effect on one example key.
    /// </summary>
    internal static bool IsSecretKey(string key) =>
        SecretKeyFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
