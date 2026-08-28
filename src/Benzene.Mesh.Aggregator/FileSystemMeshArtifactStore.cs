namespace Benzene.Mesh.Aggregator;

/// <summary>
/// An <see cref="IMeshArtifactStore"/> that publishes artifacts to a directory on local disk.
/// </summary>
public class FileSystemMeshArtifactStore : IMeshArtifactStore
{
    private readonly string _rootDirectory;

    /// <summary>Initializes a new instance of the <see cref="FileSystemMeshArtifactStore"/> class.</summary>
    /// <param name="rootDirectory">The directory artifacts are written under. Created on first write if it doesn't exist.</param>
    public FileSystemMeshArtifactStore(string rootDirectory)
    {
        _rootDirectory = rootDirectory;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Writes to a temporary file in the same directory, then <see cref="File.Move(string,string,bool)"/>s
    /// it into place, rather than truncating <paramref name="relativePath"/> in place with
    /// <c>File.WriteAllTextAsync</c>. A rename is atomic on both POSIX and Windows - a concurrent
    /// <see cref="TryReadAsync"/> either sees the old complete content or the new complete content,
    /// never a torn read, which a truncate-then-write is exposed to (this store is the shipped Mesh
    /// Host's default, read by the very same process that writes it on a poll timer).
    /// </remarks>
    public async Task PublishAsync(string relativePath, string content)
    {
        var fullPath = ResolveWithinRoot(relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // The temp file must live in the same directory as the target so the later Move is a same-volume
        // rename (atomic) rather than a cross-volume copy+delete (not atomic, and could fail partway).
        var tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, content);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            // Best-effort cleanup of the temp file on a failed write/move - never let a half-written
            // scratch file accumulate next to the real artifacts.
            try { File.Delete(tempPath); } catch { /* nothing more useful to do here */ }
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<string?> TryReadAsync(string relativePath)
    {
        var fullPath = ResolveWithinRoot(relativePath);
        return File.Exists(fullPath) ? await File.ReadAllTextAsync(fullPath) : null;
    }

    /// <summary>
    /// Resolves <paramref name="relativePath"/> against the store root and asserts the result stays
    /// inside it. The relative path can carry a service name that originated in an untrusted push
    /// report (<c>services/{report.Name}.json</c>), so a value like <c>"../../etc/passwd"</c> or a
    /// rooted path would otherwise let <see cref="Path.Combine(string,string)"/> escape the root and
    /// read or overwrite an arbitrary file. Resolving to a full path and checking containment closes
    /// that traversal at the storage boundary, protecting every caller.
    /// </summary>
    /// <remarks>
    /// #242: root-containment alone is too coarse. <see cref="Path.Combine(string, string)"/> plus
    /// <see cref="Path.GetFullPath(string)"/> happily normalize <c>"services/../manifest.json"</c>
    /// down to <c>"{root}/manifest.json"</c> - still inside the root, so a caller that only ever
    /// meant to touch the <c>services/</c> subtree (<see cref="ArtifactStoreMeshReportPublisher"/>,
    /// keying on an untrusted report name) could overwrite any sibling top-level artifact
    /// (<c>manifest.json</c>, <c>topics.json</c>, ...). Rejecting any literal <c>"."</c>/<c>".."</c>
    /// path segment up front, before any combining or normalizing happens, closes that regardless of
    /// which subtree (if any) the caller intended - a resolved path can now never land outside the
    /// directory its own literal segments name. Kept on top of (not instead of) the root-containment
    /// check below, which still catches a rooted/absolute <paramref name="relativePath"/>.
    /// </remarks>
    private string ResolveWithinRoot(string relativePath)
    {
        foreach (var segment in relativePath.Split('/', '\\'))
        {
            if (segment is "." or "..")
            {
                throw new System.UnauthorizedAccessException(
                    $"The artifact path '{relativePath}' contains a '.' or '..' segment and was rejected.");
            }
        }

        var rootFull = Path.GetFullPath(_rootDirectory);
        var combined = Path.GetFullPath(Path.Combine(rootFull, relativePath));

        var rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (!string.Equals(combined, rootFull, StringComparison.Ordinal) &&
            !combined.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new System.UnauthorizedAccessException(
                $"The artifact path '{relativePath}' resolves outside the store root and was rejected.");
        }

        return combined;
    }
}
