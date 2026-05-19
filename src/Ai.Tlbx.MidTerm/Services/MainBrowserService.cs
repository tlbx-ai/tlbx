using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Models;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services;

public sealed class MainBrowserService
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, BrowserRegistration> _browserConnections = new(StringComparer.Ordinal);
    private readonly SettingsService? _settingsService;
    private string? _mainBrowserId;
    private bool _hasAssignedInitialMainBrowser;

    public MainBrowserService(SettingsService settingsService)
        : this(settingsService, null)
    {
    }

    internal MainBrowserService(TimeProvider? timeProvider = null)
        : this(null, timeProvider)
    {
    }

    internal MainBrowserService(SettingsService? settingsService, TimeProvider? timeProvider)
    {
        _settingsService = settingsService;

        var stickyMainBrowserId = NormalizeBrowserId(settingsService?.Load().StickyMainBrowserId);
        if (stickyMainBrowserId is not null)
        {
            _mainBrowserId = stickyMainBrowserId;
            _hasAssignedInitialMainBrowser = true;
        }
    }

    public event Action? OnMainBrowserChanged;

    public bool HasMultipleClients
    {
        get
        {
            lock (_lock)
            {
                return _browserConnections.Count >= 2;
            }
        }
    }

    public void Register(string browserId, object connectionToken)
    {
        bool notify;
        lock (_lock)
        {
            if (!_browserConnections.TryGetValue(browserId, out var registration))
            {
                registration = new BrowserRegistration();
                _browserConnections[browserId] = registration;
            }

            registration.ConnectionTokens.Add(connectionToken);

            if (IsStickyMainBrowserReconnectLocked(browserId))
            {
                _mainBrowserId = browserId;
                _hasAssignedInitialMainBrowser = true;
                Log.Verbose(() => $"[MainBrowser] Restored sticky leading browser {GetLogPrefix(browserId)}");
                notify = true;
            }
            else if (!_hasAssignedInitialMainBrowser)
            {
                // First browser ever (cold start) — auto-promote
                _mainBrowserId = browserId;
                _hasAssignedInitialMainBrowser = true;
                Log.Verbose(() => $"[MainBrowser] Initial promote {GetLogPrefix(browserId)}");
                notify = true;
            }
            else if (_mainBrowserId == browserId)
            {
                // Main browser reconnected — notify so it gets fresh status
                notify = true;
            }
            else
            {
                // Another browser connected — notify if this is the 2nd unique browser
                notify = _browserConnections.Count == 2;
            }
        }
        if (notify) OnMainBrowserChanged?.Invoke();
    }

    public void Unregister(string browserId, object connectionToken)
    {
        bool changed;
        lock (_lock)
        {
            if (!_browserConnections.TryGetValue(browserId, out var registration))
                return;

            registration.ConnectionTokens.Remove(connectionToken);
            registration.ActiveConnectionTokens.Remove(connectionToken);

            if (registration.ConnectionTokens.Count == 0)
            {
                _browserConnections.Remove(browserId);
            }

            // _mainBrowserId is NOT cleared when the main browser disconnects.
            // It stays set so the browser retains main status when it reconnects.
            // Only Claim() from another browser can override it.

            // Notify if multi-client count changed (affects showButton for remaining clients)
            changed = !_browserConnections.ContainsKey(browserId);
        }
        if (changed) OnMainBrowserChanged?.Invoke();
    }

    public void UpdateActivity(
        string browserId,
        object connectionToken,
        bool isActive,
        string? activeSessionId = null,
        string? activeSurface = null)
    {
        bool changed = false;
        lock (_lock)
        {
            if (!_browserConnections.TryGetValue(browserId, out var registration))
            {
                return;
            }

            if (isActive)
            {
                if (!registration.ConnectionTokens.Contains(connectionToken))
                {
                    registration.ConnectionTokens.Add(connectionToken);
                }
                registration.ActiveConnectionTokens.Add(connectionToken);
            }
            else
            {
                registration.ActiveConnectionTokens.Remove(connectionToken);
            }

            var normalizedSessionId = string.IsNullOrWhiteSpace(activeSessionId) ? null : activeSessionId;
            var normalizedSurface = string.IsNullOrWhiteSpace(activeSurface) ? null : activeSurface;
            var isActiveNow = registration.ActiveConnectionTokens.Count > 0;
            changed = registration.IsActive != isActiveNow
                || !string.Equals(registration.ActiveSessionId, normalizedSessionId, StringComparison.Ordinal)
                || !string.Equals(registration.ActiveSurface, normalizedSurface, StringComparison.Ordinal);
            registration.IsActive = isActiveNow;
            registration.ActiveSessionId = normalizedSessionId;
            registration.ActiveSurface = normalizedSurface;
        }
        if (changed) OnMainBrowserChanged?.Invoke();
    }

    public void Claim(string browserId)
    {
        lock (_lock)
        {
            _mainBrowserId = browserId;
            Log.Verbose(() => $"[MainBrowser] Claimed by {GetLogPrefix(browserId)}");
        }
        PersistStickyMainBrowserId(browserId);
        OnMainBrowserChanged?.Invoke();
    }

    public void Release(string browserId)
    {
        bool changed;
        lock (_lock)
        {
            changed = _mainBrowserId == browserId;
            if (changed) _mainBrowserId = null;
        }
        if (changed) PersistStickyMainBrowserId(null);
        if (changed) OnMainBrowserChanged?.Invoke();
    }

    public bool IsMain(string browserId)
    {
        lock (_lock)
        {
            return _mainBrowserId == browserId;
        }
    }

    public string? GetMainBrowserId()
    {
        lock (_lock)
        {
            return _mainBrowserId;
        }
    }

    public List<BrowserSessionStatus> GetBrowserStatuses()
    {
        lock (_lock)
        {
            return _browserConnections
                .OrderByDescending(pair => IsMainLocked(pair.Key))
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new BrowserSessionStatus
                {
                    BrowserId = pair.Key,
                    IsMain = IsMainLocked(pair.Key),
                    IsActive = pair.Value.IsActive,
                    ConnectionCount = pair.Value.ConnectionTokens.Count,
                    ActiveConnectionCount = pair.Value.ActiveConnectionTokens.Count,
                    ActiveSessionId = pair.Value.ActiveSessionId,
                    ActiveSurface = pair.Value.ActiveSurface
                })
                .ToList();
        }
    }

    /// <summary>
    /// Whether the main browser button should be visible for this browser.
    /// True when 2+ browsers are connected, or when main is set to a different
    /// (possibly offline) browser so this one can claim.
    /// </summary>
    public bool ShouldShowButton(string browserId)
    {
        lock (_lock)
        {
            return _browserConnections.Count >= 2
                || (_mainBrowserId is not null && _mainBrowserId != browserId);
        }
    }

    private sealed class BrowserRegistration
    {
        public HashSet<object> ConnectionTokens { get; } = new(ReferenceEqualityComparer.Instance);
        public HashSet<object> ActiveConnectionTokens { get; } = new(ReferenceEqualityComparer.Instance);
        public bool IsActive { get; set; }
        public string? ActiveSessionId { get; set; }
        public string? ActiveSurface { get; set; }
    }

    private bool IsStickyMainBrowserReconnectLocked(string browserId)
    {
        if (string.IsNullOrWhiteSpace(_mainBrowserId))
        {
            return false;
        }

        if (string.Equals(browserId, _mainBrowserId, StringComparison.Ordinal))
        {
            return true;
        }

        return !_browserConnections.ContainsKey(_mainBrowserId)
            && BrowserIdentity.AreSameBrowser(browserId, _mainBrowserId);
    }

    private bool IsMainLocked(string browserId)
    {
        return string.Equals(browserId, _mainBrowserId, StringComparison.Ordinal);
    }

    private void PersistStickyMainBrowserId(string? browserId)
    {
        if (_settingsService is null)
        {
            return;
        }

        try
        {
            var settings = _settingsService.Load();
            settings.StickyMainBrowserId = browserId ?? "";
            _settingsService.Save(settings);
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"[MainBrowser] Failed to persist sticky leading browser: {ex.Message}");
        }
    }

    private static string? NormalizeBrowserId(string? browserId)
    {
        return string.IsNullOrWhiteSpace(browserId) ? null : browserId.Trim();
    }

    private static string GetLogPrefix(string browserId)
    {
        return browserId.Length <= 8 ? browserId : browserId[..8];
    }
}
