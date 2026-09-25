using Ai.Tlbx.MidTerm.Services.Sessions;

namespace Ai.Tlbx.MidTerm.Services.Browser;

public sealed class BrowserPreviewOwnerService
{
    private readonly TerminalSizeControlService? _sizeControl;

    public BrowserPreviewOwnerService(TerminalSizeControlService? sizeControl = null)
    {
        _sizeControl = sizeControl;
    }

    public string? GetSizeOwnerBrowserId(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? null : _sizeControl?.GetOwnerBrowserId(sessionId);

    private readonly Lock _lock = new();
    private readonly Dictionary<PreviewKey, string> _owners = new();

    public string? GetOwnerBrowserId(string? sessionId, string? previewName)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var sizeOwner = GetSizeOwnerBrowserId(sessionId);
        if (sizeOwner is not null) return sizeOwner;

        var key = PreviewKey.Create(sessionId, previewName);
        lock (_lock)
        {
            return _owners.GetValueOrDefault(key);
        }
    }

    public string? ResolveOwnerBrowserId(
        string? sessionId,
        string? previewName,
        IEnumerable<string?> connectedBrowserIds)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var sizeOwner = GetSizeOwnerBrowserId(sessionId);
        if (sizeOwner is not null) return sizeOwner;

        var key = PreviewKey.Create(sessionId, previewName);
        var distinctCandidates = connectedBrowserIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToArray();

        lock (_lock)
        {
            if (_owners.TryGetValue(key, out var currentOwner))
            {
                if (distinctCandidates.Any(candidate => BrowserIdentity.AreSameBrowser(candidate, currentOwner)))
                {
                    return currentOwner;
                }

                if (distinctCandidates.Length == 1)
                {
                    _owners[key] = distinctCandidates[0];
                    return distinctCandidates[0];
                }

                return currentOwner;
            }

            if (distinctCandidates.Length == 1)
            {
                _owners[key] = distinctCandidates[0];
                return distinctCandidates[0];
            }

            return null;
        }
    }

    public void ClaimIfMissing(string? sessionId, string? previewName, string? browserId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(browserId))
        {
            return;
        }

        var key = PreviewKey.Create(sessionId, previewName);
        lock (_lock)
        {
            _owners.TryAdd(key, browserId);
        }
    }

    public void Claim(string? sessionId, string? previewName, string? browserId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(browserId))
        {
            return;
        }

        var key = PreviewKey.Create(sessionId, previewName);
        lock (_lock)
        {
            _owners[key] = browserId;
        }
    }

    public bool TryClaim(string? sessionId, string? previewName, string? browserId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(browserId))
        {
            return false;
        }

        Claim(sessionId, previewName, browserId);
        return true;
    }

    public bool Release(string? sessionId, string? previewName)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        lock (_lock)
        {
            return _owners.Remove(PreviewKey.Create(sessionId, previewName));
        }
    }

    public int ClearSession(string sessionId)
    {
        lock (_lock)
        {
            var keys = _owners.Keys
                .Where(key => string.Equals(key.SessionId, sessionId, StringComparison.Ordinal))
                .ToArray();
            foreach (var key in keys)
            {
                _owners.Remove(key);
            }

            return keys.Length;
        }
    }

    private readonly record struct PreviewKey(string SessionId, string PreviewName)
    {
        public static PreviewKey Create(string sessionId, string? previewName)
        {
            return new PreviewKey(
                sessionId,
                string.IsNullOrWhiteSpace(previewName)
                    ? WebPreview.WebPreviewService.DefaultPreviewName
                    : previewName);
        }
    }
}
