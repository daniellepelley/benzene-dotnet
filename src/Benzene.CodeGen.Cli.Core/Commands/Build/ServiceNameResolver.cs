using Benzene.Schema.OpenApi.EventService;

namespace Benzene.CodeGen.Cli.Core.Commands.Build;

/// <summary>
/// Resolves the service name <see cref="CodeBuilderFactory"/> derives the generated namespace/type
/// names from, for a <c>build</c> invocation that may not have a <c>--lambda-name</c> to derive it
/// from. An explicit <c>--service-name</c> always wins; otherwise the default follows the source:
/// <c>--mesh</c> uses the mesh service name, <c>--file</c> uses the spec document's own title (else
/// the file's stem), <c>--url</c> uses the host's first label, and <c>--lambda-name</c> returns
/// null so <see cref="CodeBuilderFactory"/> keeps its original, unchanged <see cref="LambdaNameParser"/>
/// derivation.
/// </summary>
public static class ServiceNameResolver
{
    public static string Resolve(BuildPayload payload, EventServiceDocument document)
    {
        if (!string.IsNullOrWhiteSpace(payload.ServiceName))
        {
            return payload.ServiceName;
        }

        if (!string.IsNullOrWhiteSpace(payload.Mesh))
        {
            return payload.Service;
        }

        if (!string.IsNullOrWhiteSpace(payload.File))
        {
            var title = document.Info?.Title;
            return !string.IsNullOrWhiteSpace(title) ? title : FileStem(payload.File);
        }

        if (!string.IsNullOrWhiteSpace(payload.Url))
        {
            return HostSegment(payload.Url);
        }

        // --lambda-name source: leave null, CodeBuilderFactory's original LambdaNameParser
        // derivation is unchanged.
        return null;
    }

    private static string FileStem(string path)
    {
        // "Orders.spec.json" -> "Orders.spec" -> "Orders": strip the ".spec" suffix Phase 1's
        // artifact convention adds, so it doesn't leak into a generated namespace.
        var stem = Path.GetFileNameWithoutExtension(path);
        return stem.EndsWith(".spec", StringComparison.OrdinalIgnoreCase) ? stem[..^5] : stem;
    }

    private static string HostSegment(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var firstLabel = uri.Host.Split('.').FirstOrDefault();
            return string.IsNullOrEmpty(firstLabel) ? uri.Host : firstLabel;
        }

        return url;
    }
}
