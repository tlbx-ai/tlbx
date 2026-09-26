using Ai.Tlbx.MidTerm.Services.Sessions;

namespace Ai.Tlbx.MidTerm.Services.Browser;

public sealed class BrowserPreviewOwnerService
{
    private readonly TerminalSizeControlService? _sizeControl;

    public BrowserPreviewOwnerService(TerminalSizeControlService? sizeControl = null)
    {
        _sizeControl = sizeControl;
    }

    private string? GetSizeOwnerBrowserId(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? null : _sizeControl?.GetOwnerBrowserId(sessionId);

    private readonly Lock _lock = new();
    private readonly Dictionary<PreviewKey, string> _owners = new();
    private readonly Dictionary<PreviewKey, long> _generations = new();
    private long _generation;

    public long GetGeneration(string? sessionId, string? previewName)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return 0;
        lock (_lock) return _generations.GetValueOrDefault(PreviewKey.Create(sessionId, previewName));
    }

    public bool IsCurrent(string? sessionId, string? previewName, string? browserId, long generation)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return true;
        lock (_lock)
        {
            var key = PreviewKey.Create(sessionId, previewName);
            return _generations.GetValueOrDefault(key) == generation
                && string.Equals(_owners.GetValueOrDefault(key), browserId, StringComparison.Ordinal);
        }
    }
    private readonly Dictionary<string, string> _uiConnections = new(StringComparer.Ordinal);

    public void RegisterUi(string connectionId, string browserId)
    {
        lock (_lock) _uiConnections[connectionId] = browserId;
    }

    public void UnregisterUi(string connectionId)
    {
        lock (_lock) _uiConnections.Remove(connectionId);
    }

    public string[] GetConnectedBrowserIds()
    {
        lock (_lock) return _uiConnections.Values.Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public bool IsConnected(string? browserId) => browserId is not null
        && GetConnectedBrowserIds().Contains(browserId, StringComparer.Ordinal);

    public string? ResolveOwnerBrowserId(string? sessionId, string? previewName) =>
        ResolveOwnerBrowserId(sessionId, previewName, GetConnectedBrowserIds());

    public static string UnavailableMessage(string? sessionId, string? previewName, string? owner) =>
        owner is null
            ? "Multiple tlbx browser UIs are connected. List /api/browser/ui-clients and explicitly select a tab with mt_claim_preview --browser <browserId>."
            : $"Preview '{previewName ?? "default"}' in session '{sessionId}' is owned by browser '{owner}', but that exact tab is not connected. Reconnect it or explicitly hand off with mt_claim_preview --browser <browserId> (list /api/browser/ui-clients).";

    public string? GetOwnerBrowserId(string? sessionId, string? previewName)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

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
                return currentOwner;
            }

            // Size ownership is only an initialization hint for a connected exact tab.
            var sizeOwner = GetSizeOwnerBrowserId(sessionId);
            if (sizeOwner is not null && distinctCandidates.Contains(sizeOwner, StringComparer.Ordinal))
            {
                _owners[key] = sizeOwner;
                _generations[key] = ++_generation;
                return sizeOwner;
            }

            if (distinctCandidates.Length == 1)
            {
                _owners[key] = distinctCandidates[0];
                _generations[key] = ++_generation;
                return distinctCandidates[0];
            }

            return null;
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
            _generations[key] = ++_generation;
        }
    }

    public bool Release(string? sessionId, string? previewName)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        lock (_lock)
        {
            var key = PreviewKey.Create(sessionId, previewName);
            _generations.Remove(key);
            return _owners.Remove(key);
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
                _generations.Remove(key);
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
                    : WebPreview.WebPreviewService.NormalizePreviewName(previewName));
        }
    }
}
