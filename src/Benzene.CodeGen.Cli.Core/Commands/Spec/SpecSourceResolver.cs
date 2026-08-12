namespace Benzene.CodeGen.Cli.Core.Commands.Spec;

/// <summary>
/// Picks the <see cref="ISpecSource"/> a <c>build</c>/<c>spec</c> invocation should use from its
/// <c>--file</c>/<c>--url</c>/<c>--mesh</c>/<c>--lambda-name</c> arguments, enforcing that exactly
/// one is given. Shared by <c>Commands.Build.BuildCommand</c> (via <c>ClientCodeBuilder</c>) and
/// <see cref="SpecCommand"/> so both commands validate and select sources identically.
/// </summary>
public static class SpecSourceResolver
{
    /// <param name="file">The <c>--file</c> value, or null/empty if not given.</param>
    /// <param name="url">The <c>--url</c> value, or null/empty if not given.</param>
    /// <param name="mesh">The <c>--mesh</c> value (manifest URL), or null/empty if not given.</param>
    /// <param name="service">The <c>--service</c> value - required alongside <paramref name="mesh"/> to pick the service entry.</param>
    /// <param name="lambdaName">The <c>--lambda-name</c> value, or null/empty if not given.</param>
    /// <param name="profile">The <c>--profile</c> value, used only with <paramref name="lambdaName"/>.</param>
    public static ISpecSource Resolve(string file, string url, string mesh, string service, string lambdaName, string profile)
    {
        var given = new List<string>();
        if (!string.IsNullOrWhiteSpace(file)) given.Add($"--{Constants.File}");
        if (!string.IsNullOrWhiteSpace(url)) given.Add($"--{Constants.Url}");
        if (!string.IsNullOrWhiteSpace(mesh)) given.Add($"--{Constants.Mesh}");
        if (!string.IsNullOrWhiteSpace(lambdaName)) given.Add($"--{Constants.LambdaName}");

        if (given.Count == 0)
        {
            throw new ArgumentException(
                $"No spec source given: pass exactly one of --{Constants.File}, --{Constants.Url}, " +
                $"--{Constants.Mesh} or --{Constants.LambdaName}.");
        }

        if (given.Count > 1)
        {
            throw new ArgumentException(
                $"Multiple spec sources given ({string.Join(", ", given)}): pass exactly one of " +
                $"--{Constants.File}, --{Constants.Url}, --{Constants.Mesh} or --{Constants.LambdaName}.");
        }

        if (!string.IsNullOrWhiteSpace(file))
        {
            return new FileSpecSource(file);
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            return new HttpSpecSource(url);
        }

        if (!string.IsNullOrWhiteSpace(mesh))
        {
            if (string.IsNullOrWhiteSpace(service))
            {
                throw new ArgumentException(
                    $"--{Constants.Mesh} requires --{Constants.Service} <name> to pick the service entry from the manifest.");
            }

            return new MeshSpecSource(mesh, service);
        }

        return new AwsLambdaSpecSource(lambdaName, profile);
    }
}
