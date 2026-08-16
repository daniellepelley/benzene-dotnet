using System.Text.RegularExpressions;
using Benzene.Mesh.Ui;
using Xunit;

namespace Benzene.Mesh.Test;

public class MeshUiPageTest
{
    /// <summary>
    /// Isolates the opening <c>&lt;html ...&gt;</c> tag GetHtml rewrites, so a "not injected" assertion
    /// checks the tag itself rather than the whole page - the built page's own inlined JS legitimately
    /// contains the attribute names as string literals (it reads them at runtime via
    /// <c>optionsFromDocument</c>), so a blanket <c>Assert.DoesNotContain</c> over the full HTML would
    /// false-positive on that, not on whether GetHtml actually injected anything.
    /// </summary>
    private static string HtmlTag(string html) => Regex.Match(html, "<html[^>]*>").Value;

    [Fact]
    public void GetHtml_ReturnsEmbeddedPage()
    {
        var html = MeshUiPage.GetHtml();

        Assert.Contains("<title>Benzene Mesh</title>", html);
        Assert.Contains("id=\"root\"", html);
        Assert.Contains("<html lang=\"en\">", html);
    }

    [Fact]
    public void GetHtml_WithUrl_InjectsManifestUrlAttribute()
    {
        var html = MeshUiPage.GetHtml("https://example.com/manifest.json");

        Assert.Contains(
            "<html lang=\"en\" data-manifest-url=\"https://example.com/manifest.json\">",
            html);
    }

    [Fact]
    public void GetHtml_WithUrl_HtmlEncodesTheUrl()
    {
        var html = MeshUiPage.GetHtml("https://example.com/manifest.json?tenant=a&b=c");

        Assert.Contains(
            "data-manifest-url=\"https://example.com/manifest.json?tenant=a&amp;b=c\"",
            html);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetHtml_WithNullOrWhitespaceUrl_BehavesLikeGetHtml(string? manifestUrl)
    {
        var html = MeshUiPage.GetHtml(manifestUrl!);

        Assert.Equal(MeshUiPage.GetHtml(), html);
        Assert.Contains("<html lang=\"en\">", html);
    }

    [Fact]
    public void GetHtml_WithEnvelopeUrl_InjectsFleetUrlAttribute()
    {
        var html = MeshUiPage.GetHtml("manifest.json", "/benzene/invoke");

        Assert.Contains("data-manifest-url=\"manifest.json\"", html);
        Assert.Contains("data-fleet-url=\"/benzene/invoke\"", html);
        Assert.DoesNotContain("data-dispatch-url", HtmlTag(html));
    }

    [Fact]
    public void GetHtml_WithDispatchUrl_InjectsDispatchUrlAttribute()
    {
        var html = MeshUiPage.GetHtml("manifest.json", "/benzene/invoke", "/benzene/invoke");

        Assert.Contains("data-manifest-url=\"manifest.json\"", html);
        Assert.Contains("data-fleet-url=\"/benzene/invoke\"", html);
        Assert.Contains("data-dispatch-url=\"/benzene/invoke\"", html);
    }

    [Fact]
    public void GetHtml_WithDispatchUrlOnly_DoesNotInjectFleetUrl()
    {
        var html = MeshUiPage.GetHtml(null, null, "/admin/invoke");
        var tag = HtmlTag(html);

        Assert.DoesNotContain("data-manifest-url", tag);
        Assert.DoesNotContain("data-fleet-url", tag);
        Assert.Contains("data-dispatch-url=\"/admin/invoke\"", tag);
    }

    [Fact]
    public void GetHtml_WithDispatchUrl_HtmlEncodesTheUrl()
    {
        var html = MeshUiPage.GetHtml(null, null, "https://example.com/invoke?tenant=a&b=c");

        Assert.Contains(
            "data-dispatch-url=\"https://example.com/invoke?tenant=a&amp;b=c\"",
            html);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetHtml_WithNullOrWhitespaceDispatchUrl_DoesNotInjectDispatchUrlAttribute(string? dispatchUrl)
    {
        var html = MeshUiPage.GetHtml("manifest.json", "/benzene/invoke", dispatchUrl);

        Assert.Equal(MeshUiPage.GetHtml("manifest.json", "/benzene/invoke"), html);
        Assert.DoesNotContain("data-dispatch-url", HtmlTag(html));
    }

    [Fact]
    public void GetHtml_WithLogoutUrl_InjectsLogoutUrlAttribute()
    {
        var html = MeshUiPage.GetHtml("manifest.json", null, null, "/mesh/auth/logout", null);
        var tag = HtmlTag(html);

        Assert.Contains("data-logout-url=\"/mesh/auth/logout\"", tag);
        Assert.DoesNotContain("data-refresh-url", tag);
    }

    [Fact]
    public void GetHtml_WithRefreshUrl_InjectsRefreshUrlAttribute()
    {
        var html = MeshUiPage.GetHtml("manifest.json", null, null, null, "/mesh/refresh");
        var tag = HtmlTag(html);

        Assert.Contains("data-refresh-url=\"/mesh/refresh\"", tag);
        Assert.DoesNotContain("data-logout-url", tag);
    }

    [Fact]
    public void GetHtml_WithEveryUrl_InjectsAllFiveAttributes()
    {
        var html = MeshUiPage.GetHtml(
            "manifest.json", "/benzene/invoke", "/benzene/invoke", "/mesh/auth/logout", "/mesh/refresh");
        var tag = HtmlTag(html);

        Assert.Contains("data-manifest-url=\"manifest.json\"", tag);
        Assert.Contains("data-fleet-url=\"/benzene/invoke\"", tag);
        Assert.Contains("data-dispatch-url=\"/benzene/invoke\"", tag);
        Assert.Contains("data-logout-url=\"/mesh/auth/logout\"", tag);
        Assert.Contains("data-refresh-url=\"/mesh/refresh\"", tag);
    }

    [Fact]
    public void GetHtml_WithLogoutUrl_HtmlEncodesTheUrl()
    {
        var html = MeshUiPage.GetHtml(null, null, null, "/mesh/auth/logout?returnTo=a&b=c", null);

        Assert.Contains("data-logout-url=\"/mesh/auth/logout?returnTo=a&amp;b=c\"", html);
    }

    [Fact]
    public void GetHtml_WithRefreshUrl_HtmlEncodesTheUrl()
    {
        // The injected value lands inside a double-quoted attribute on the document root, so a value
        // carrying a quote must not be able to close it and start new markup.
        var html = MeshUiPage.GetHtml(null, null, null, null, "/mesh/refresh\"><script>alert(1)</script>");
        var tag = HtmlTag(html);

        Assert.Contains("data-refresh-url=\"/mesh/refresh&quot;&gt;&lt;script&gt;", tag);
        Assert.DoesNotContain("<script>alert(1)</script>", tag);
    }

    /// <summary>
    /// Both new attributes are explicit opt-ins, exactly as <c>data-dispatch-url</c> is: nothing about
    /// wiring a manifest or a fleet endpoint may cause a Sign-out or Refresh control to appear. This
    /// pins that - the three-argument overload's output must be byte-identical to the five-argument one
    /// with both new values left null/blank.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetHtml_WithNullOrWhitespaceLogoutAndRefreshUrls_InjectsNeither(string? url)
    {
        var html = MeshUiPage.GetHtml("manifest.json", "/benzene/invoke", null, url, url);
        var tag = HtmlTag(html);

        Assert.Equal(MeshUiPage.GetHtml("manifest.json", "/benzene/invoke", null), html);
        Assert.DoesNotContain("data-logout-url", tag);
        Assert.DoesNotContain("data-refresh-url", tag);
    }
}
