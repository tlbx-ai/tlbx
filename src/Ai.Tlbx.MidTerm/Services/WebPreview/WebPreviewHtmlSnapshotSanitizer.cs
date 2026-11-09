using System.Text.RegularExpressions;

namespace Ai.Tlbx.MidTerm.Services.WebPreview;

internal static partial class WebPreviewHtmlSnapshotSanitizer
{
    public static string StripProxyArtifacts(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        html = BaseTagRegex().Replace(html, "");
        html = ProxyScriptRegex().Replace(html, "");
        html = BlobScriptRegex().Replace(html, "");
        return html;
    }

    public static string DecodeExtUrls(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        return ExtUrlRegex().Replace(html, static match =>
        {
            try { return Uri.UnescapeDataString(match.Groups[1].Value); }
            catch { return match.Value; }
        });
    }

    [GeneratedRegex(@"<base\s[^>]*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex BaseTagRegex();

    [GeneratedRegex(@"<script(?:\s+data-mt-proxy-route=""[^""]*"")?>\(function(?:\s+[A-Za-z_$][\w$]*)?\(\)\{.*?window\.__mtProxy.*?</script>", RegexOptions.Singleline, 1000)]
    private static partial Regex ProxyScriptRegex();

    [GeneratedRegex(@"<script[^>]+src=[""']blob:[^""'>]+[""'][^>]*></script>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1000)]
    private static partial Regex BlobScriptRegex();

    [GeneratedRegex(@"/_ext\?u=([^&""'\s>]+)", RegexOptions.None, 1000)]
    private static partial Regex ExtUrlRegex();
}
