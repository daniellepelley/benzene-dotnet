using System.Net;
using System.Reflection;

namespace Benzene.Mesh.Ui;

/// <summary>
/// Provides the self-contained Benzene Mesh Explorer HTML page — a catalog viewer for a
/// service mesh's <c>manifest.json</c>/<c>services/{name}.json</c> artifacts (as published by
/// <c>Benzene.Mesh.Aggregator</c>). The page is embedded in this assembly and has no external
/// dependencies, so it can be served by any static file host, or by any Benzene transport.
/// </summary>
/// <remarks>
/// The page loads a manifest from, in order of precedence: a <c>?url=</c> query-string parameter,
/// the <c>data-manifest-url</c> attribute on the document root (see
/// <see cref="GetHtml(string)"/>), a relative fetch of <c>manifest.json</c>, or an embedded
/// sample. The primary deployment target is a plain static file host serving this page alongside
/// the aggregator's generated <c>manifest.json</c> - it does not require a Benzene pipeline at
/// all, and needs no query param or attribute either, since the relative fetch covers that case.
/// </remarks>
public static class MeshUiPage
{
    private const string ResourceName = "Benzene.Mesh.Ui.mesh-ui.html";

    private static readonly Lazy<string> LazyHtml = new(ReadResource);

    /// <summary>
    /// Gets the viewer HTML with no manifest URL injected. The page tries a <c>?url=</c>
    /// query-string parameter, then a relative fetch of <c>manifest.json</c>, before falling back
    /// to its embedded sample manifest.
    /// </summary>
    /// <returns>The complete, self-contained HTML document.</returns>
    public static string GetHtml() => LazyHtml.Value;

    /// <summary>
    /// Gets the viewer HTML with a manifest URL injected onto the document root, so the page
    /// fetches and renders that manifest on load.
    /// </summary>
    /// <param name="manifestUrl">
    /// The URL the page should fetch <c>manifest.json</c> from. If null or whitespace, this
    /// behaves like <see cref="GetHtml()"/>.
    /// </param>
    /// <returns>The complete, self-contained HTML document.</returns>
    public static string GetHtml(string manifestUrl) => GetHtml(manifestUrl, null);

    /// <summary>
    /// Gets the viewer HTML with a manifest URL and (optionally) a live fleet-envelope endpoint
    /// injected onto the document root. With <paramref name="envelopeUrl"/> set, the page's Fleet
    /// plane feature-detects (via the injected <c>data-fleet-url</c>) and enriches the catalog with
    /// live <c>mesh:query:*</c> data polled from that wire-envelope endpoint; without it the page is
    /// the static catalog viewer exactly as <see cref="GetHtml(string)"/>.
    /// </summary>
    /// <param name="manifestUrl">
    /// The URL the page should fetch <c>manifest.json</c> from. If null or whitespace, no
    /// <c>data-manifest-url</c> is injected (the page falls back to <c>?url=</c>/relative fetch).
    /// </param>
    /// <param name="envelopeUrl">
    /// The wire-envelope endpoint the Fleet plane polls (same-origin path or absolute URL). If null
    /// or whitespace, no <c>data-fleet-url</c> is injected and the Fleet plane stays dormant.
    /// </param>
    /// <returns>The complete, self-contained HTML document.</returns>
    public static string GetHtml(string? manifestUrl, string? envelopeUrl) => GetHtml(manifestUrl, envelopeUrl, null);

    /// <summary>
    /// Gets the viewer HTML with a manifest URL, a live fleet-envelope endpoint, and (optionally) a
    /// dispatch endpoint injected onto the document root. With <paramref name="dispatchUrl"/> set,
    /// the page's Test Console feature-detects (via the injected <c>data-dispatch-url</c>) and offers
    /// to send a composed test message through that wire-envelope endpoint's <c>mesh:dispatch</c>
    /// topic; without it the page renders test messages read-only (compose and copy, no send button).
    /// </summary>
    /// <param name="manifestUrl">
    /// The URL the page should fetch <c>manifest.json</c> from. If null or whitespace, no
    /// <c>data-manifest-url</c> is injected (the page falls back to <c>?url=</c>/relative fetch).
    /// </param>
    /// <param name="envelopeUrl">
    /// The wire-envelope endpoint the Fleet plane polls (same-origin path or absolute URL). If null
    /// or whitespace, no <c>data-fleet-url</c> is injected and the Fleet plane stays dormant.
    /// </param>
    /// <param name="dispatchUrl">
    /// The wire-envelope endpoint the Test Console POSTs <c>mesh:dispatch</c> messages to
    /// (same-origin path or absolute URL). This is a deliberate, separate opt-in from
    /// <paramref name="envelopeUrl"/> - a mesh host that wires <c>Benzene.Mesh.Collector</c> alone
    /// (read-only fleet queries) must not silently also expose live dispatch; the host must wire
    /// <c>Benzene.Mesh.Dispatch</c>'s <c>UseMeshDispatch()</c> on the same pipeline AND pass this
    /// explicitly (see <see cref="MeshUiExtensions"/>'s remarks). If null or whitespace, no
    /// <c>data-dispatch-url</c> is injected and the Test Console cannot send messages.
    /// </param>
    /// <returns>The complete, self-contained HTML document.</returns>
    public static string GetHtml(string? manifestUrl, string? envelopeUrl, string? dispatchUrl)
        => GetHtml(manifestUrl, envelopeUrl, dispatchUrl, null, null);

    /// <summary>
    /// Gets the viewer HTML with a manifest URL, a live fleet-envelope endpoint, a dispatch endpoint,
    /// a sign-out URL, and a refresh URL injected onto the document root. The page feature-detects each
    /// injected attribute independently: with <paramref name="logoutUrl"/> it renders a Sign-out
    /// control, and with <paramref name="refreshUrl"/> a Refresh control (plus a self-service empty
    /// state offering to run the first pass); without either, those controls simply don't appear.
    /// </summary>
    /// <param name="manifestUrl">
    /// The URL the page should fetch <c>manifest.json</c> from. If null or whitespace, no
    /// <c>data-manifest-url</c> is injected (the page falls back to <c>?url=</c>/relative fetch).
    /// </param>
    /// <param name="envelopeUrl">
    /// The wire-envelope endpoint the Fleet plane polls (same-origin path or absolute URL). If null
    /// or whitespace, no <c>data-fleet-url</c> is injected and the Fleet plane stays dormant.
    /// </param>
    /// <param name="dispatchUrl">
    /// The wire-envelope endpoint the Test Console POSTs <c>mesh:dispatch</c> messages to. See the
    /// three-argument overload for why this is a separate opt-in. If null or whitespace, no
    /// <c>data-dispatch-url</c> is injected and the Test Console cannot send messages.
    /// </param>
    /// <param name="logoutUrl">
    /// The URL the page's Sign-out control navigates to - the host's OIDC logout route (for example
    /// <c>{MeshOidcOptions.BasePath}/logout</c>). If null or whitespace, no <c>data-logout-url</c> is
    /// injected and no Sign-out control is rendered - which is the right default for an ungated host,
    /// where a page nobody had to log into has nothing to sign out of.
    /// </param>
    /// <param name="refreshUrl">
    /// The endpoint the page's Refresh control POSTs to, to trigger a discovery/aggregation pass (for
    /// example <c>/mesh/refresh</c>). Like <paramref name="dispatchUrl"/> this is a <b>deliberate,
    /// separate opt-in</b> and is never inferred from the host happening to have wired auth or an
    /// aggregator: it turns a read-only catalog viewer into a page with a button that fans out to every
    /// service and rewrites the catalog on each click, so the host has to say so explicitly. If null or
    /// whitespace, no <c>data-refresh-url</c> is injected and the page stays read-only. The page sends
    /// the <c>X-Benzene-Refresh</c> header on that POST; the server side of that contract is
    /// <c>Benzene.Mesh.Artifacts</c>'s <c>UseMeshRefreshGuard()</c>.
    /// </param>
    /// <returns>The complete, self-contained HTML document.</returns>
    public static string GetHtml(
        string? manifestUrl, string? envelopeUrl, string? dispatchUrl, string? logoutUrl, string? refreshUrl)
    {
        var attribute = string.Empty;
        if (!string.IsNullOrWhiteSpace(manifestUrl))
        {
            attribute += $" data-manifest-url=\"{WebUtility.HtmlEncode(manifestUrl)}\"";
        }

        if (!string.IsNullOrWhiteSpace(envelopeUrl))
        {
            attribute += $" data-fleet-url=\"{WebUtility.HtmlEncode(envelopeUrl)}\"";
        }

        if (!string.IsNullOrWhiteSpace(dispatchUrl))
        {
            attribute += $" data-dispatch-url=\"{WebUtility.HtmlEncode(dispatchUrl)}\"";
        }

        if (!string.IsNullOrWhiteSpace(logoutUrl))
        {
            attribute += $" data-logout-url=\"{WebUtility.HtmlEncode(logoutUrl)}\"";
        }

        if (!string.IsNullOrWhiteSpace(refreshUrl))
        {
            attribute += $" data-refresh-url=\"{WebUtility.HtmlEncode(refreshUrl)}\"";
        }

        if (attribute.Length == 0)
        {
            return LazyHtml.Value;
        }

        return LazyHtml.Value.Replace("<html lang=\"en\">", $"<html lang=\"en\"{attribute}>");
    }

    private static string ReadResource()
    {
        var assembly = typeof(MeshUiPage).GetTypeInfo().Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' was not found in assembly '{assembly.FullName}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
