using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Models;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services;

public sealed class MainBrowserService
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, BrowserRegistration> _browserConnections = new(StringComparer.Ordinal);
    private string? _mainBrowserId;
    private bool _hasAssignedInitialMainBrowser;

    public MainBrowserService(SettingsService settingsService)
    {
    }

    internal MainBrowserService(TimeProvider? timeProvider = null)
    {
    }

    internal MainBrowserService(SettingsService? settingsService, TimeProvider? timeProvider)
    {
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

            if (!_hasAssignedInitialMainBrowser)
            {
                // First browser ever (cold start) — auto-promote
                _mainBrowserId = browserId;
                _hasAssignedInitialMainBrowser = true;
                Log.Verbose(() => $"[MainBrowser] Initial promote {browserId[..8]}");
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
            Log.Verbose(() => $"[MainBrowser] Claimed by {browserId[..8]}");
        }
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
                .OrderByDescending(pair => string.Equals(pair.Key, _mainBrowserId, StringComparison.Ordinal))
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new BrowserSessionStatus
                {
                    BrowserId = pair.Key,
                    IsMain = string.Equals(pair.Key, _mainBrowserId, StringComparison.Ordinal),
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
}
