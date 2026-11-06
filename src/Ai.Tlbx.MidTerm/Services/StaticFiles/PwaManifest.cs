using System.Text.Json.Nodes;
using System.Net;

namespace Ai.Tlbx.MidTerm.Services.StaticFiles;

public static class PwaManifest
{
    public static string WithServerNameHtml(string html, string serverName) =>
        html.Replace("name=\"apple-mobile-web-app-title\" content=\"tlbx\"",
            $"name=\"apple-mobile-web-app-title\" content=\"{WebUtility.HtmlEncode($"tlbx {serverName}")}\"",
            StringComparison.Ordinal);

    public static string WithServerName(string template, string serverName)
    {
        var manifest = JsonNode.Parse(template)?.AsObject()
            ?? throw new InvalidOperationException("The PWA manifest is empty.");
        var appName = $"tlbx {serverName}";
        manifest["name"] = appName;
        manifest["short_name"] = appName;
        return manifest.ToJsonString();
    }
}
