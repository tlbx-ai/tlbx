namespace Ai.Tlbx.MidTerm.Services.Browser;

internal static class BrowserIdentity
{
    public const string TabIdHeader = "X-MidTerm-Tab-Id";
    public const string DeviceLabelHeader = "X-MidTerm-Device-Label";
    private const int MaxDeviceLabelLength = 80;

    public static string? Build(string? clientId, string? tabId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(tabId)
            ? clientId
            : $"{clientId}:{tabId}";
    }

    public static bool AreSameBrowser(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(left, right, StringComparison.Ordinal)
            || string.Equals(GetClientPart(left), GetClientPart(right), StringComparison.Ordinal);
    }

    public static string GetClientPart(string browserId)
    {
        var separatorIndex = browserId.IndexOf(':', StringComparison.Ordinal);
        return separatorIndex < 0 ? browserId : browserId[..separatorIndex];
    }

    public static string BuildFromRequest(HttpRequest request)
    {
        var clientId = request.Cookies["mt-client-id"];
        var tabId = request.Query["tabId"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(tabId))
        {
            tabId = request.Headers[TabIdHeader].FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            clientId = Guid.NewGuid().ToString("N");
        }

        return Build(clientId, tabId) ?? clientId;
    }

    public static string? TryBuildFromBrowserRequest(HttpRequest request)
    {
        var clientId = request.Cookies["mt-client-id"];
        var tabId = request.Headers[TabIdHeader].FirstOrDefault();
        return string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(tabId)
            ? null
            : Build(clientId, tabId);
    }

    public static string? GetDeviceLabel(HttpRequest request)
    {
        var label = request.Query["deviceLabel"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(label))
        {
            label = request.Headers[DeviceLabelHeader].FirstOrDefault();
            label = DecodeDeviceLabelHeader(label);
        }

        return SanitizeDeviceLabel(label);
    }

    private static string? DecodeDeviceLabelHeader(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return label;
        }

        try
        {
            return Uri.UnescapeDataString(label);
        }
        catch (UriFormatException)
        {
            return label;
        }
    }

    private static string? SanitizeDeviceLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var sanitized = new string(label
            .Where(static character => !char.IsControl(character))
            .Take(MaxDeviceLabelLength)
            .ToArray())
            .Trim();
        return sanitized.Length == 0 ? null : sanitized;
    }
}
