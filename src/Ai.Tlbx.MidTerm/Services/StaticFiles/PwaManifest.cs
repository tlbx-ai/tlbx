using System.Text.Json.Nodes;

namespace Ai.Tlbx.MidTerm.Services.StaticFiles;

public static class PwaManifest
{
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
