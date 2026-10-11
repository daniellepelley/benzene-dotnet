namespace Benzene.Abstractions.MessageHandlers.Info
{
    /// <summary>
    /// Static, application-level metadata (as opposed to per-message <see cref="ITransportInfo"/>),
    /// typically surfaced for diagnostics, health checks, or included in log context.
    /// </summary>
    public interface IApplicationInfo
    {
        /// <summary>The application's name.</summary>
        string Name { get; }

        /// <summary>A human-readable description of the application.</summary>
        string Description { get; }

        /// <summary>The application's version.</summary>
        string Version { get; }

        /// <summary>
        /// The comparison scheme of <see cref="Version"/> (<c>integer</c>, <c>semver</c> or
        /// <c>lexicographic</c>, service-versions.md §2), or null when the version is not orderable
        /// or none is declared. Published as the contract document's <c>versionScheme</c>.
        /// </summary>
        string? VersionScheme => null;
    }
}
