using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ai.Tlbx.MidTerm.Services.WebPreview;

public sealed partial class WebPreviewProxyMiddleware
{
    internal static string StripImportMapIntegrity(string html)
    {
        if (!html.Contains("importmap", StringComparison.OrdinalIgnoreCase)) return html;
        return ImportMapScriptRegex().Replace(html, match =>
        {
            if (!ImportMapTypeRegex().IsMatch(match.Groups[1].Value)) return match.Value;
            try
            {
                if (JsonNode.Parse(match.Groups[2].Value) is not JsonObject map || !map.Remove("integrity"))
                    return match.Value;
                // JavaScript bytes change in this proxy, so upstream SRI hashes cannot apply.
                // Keep module mappings, scopes and other import-map fields intact.
                return match.Groups[1].Value + map.ToJsonString() + match.Groups[3].Value;
            }
            catch (JsonException)
            {
                return match.Value;
            }
        });
    }

    [GeneratedRegex(@"(<script\b[^>]*>)([\s\S]*?)(</script\s*>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ImportMapScriptRegex();

    [GeneratedRegex("(?:^|\\s)type\\s*=\\s*(?:\"importmap\"|'importmap'|importmap(?=\\s|>))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ImportMapTypeRegex();
}
