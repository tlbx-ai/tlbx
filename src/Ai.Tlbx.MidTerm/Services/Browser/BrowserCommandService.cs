using System.Collections.Concurrent;
using System.Globalization;
using Ai.Tlbx.MidTerm.Models.Browser;

namespace Ai.Tlbx.MidTerm.Services.Browser;

public sealed class BrowserCommandService
{
    private const int DefaultCommandTimeoutSeconds = 10;
    private const int DefaultScreenshotTimeoutSeconds = 30;
    private readonly Lock _clientGate = new();
    private readonly ConcurrentDictionary<string, PendingCommand> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BrowserClient> _clients = new(StringComparer.Ordinal);
    private readonly MainBrowserService? _mainBrowserService;
    private readonly BrowserPreviewOwnerService? _previewOwnerService;

    public BrowserCommandService(
        MainBrowserService? mainBrowserService = null,
        BrowserPreviewOwnerService? previewOwnerService = null)
    {
        _mainBrowserService = mainBrowserService;
        _previewOwnerService = previewOwnerService;
    }

    public bool HasConnectedClient => !_clients.IsEmpty;

    public int ConnectedClientCount => _clients.Count;

    public bool TryRegisterClient(
        string connectionId,
        string? sessionId,
        string? previewName,
        string? previewId,
        Action<BrowserWsMessage> listener,
        string? browserId = null,
        bool isVisible = false,
        bool hasFocus = false,
        bool isTopLevel = false)
    {
        var replacedConnectionIds = Array.Empty<string>();
        lock (_clientGate)
        {
            if (!string.IsNullOrWhiteSpace(previewId))
            {
                replacedConnectionIds = _clients.Values
                    .Where(c => string.Equals(c.PreviewId, previewId, StringComparison.Ordinal))
                    .Select(c => c.ConnectionId)
                    .ToArray();

                foreach (var duplicateConnectionId in replacedConnectionIds)
                {
                    _clients.TryRemove(duplicateConnectionId, out _);
                }
            }

            _clients[connectionId] = new BrowserClient
            {
                ConnectionId = connectionId,
                SessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
                PreviewName = string.IsNullOrWhiteSpace(previewName) ? null : previewName,
                PreviewId = string.IsNullOrWhiteSpace(previewId) ? null : previewId,
                BrowserId = string.IsNullOrWhiteSpace(browserId) ? null : browserId,
                IsVisible = isVisible,
                HasFocus = hasFocus,
                IsTopLevel = isTopLevel,
                Listener = listener,
                ConnectedAtUtc = DateTimeOffset.UtcNow
            };
        }

        foreach (var replacedConnectionId in replacedConnectionIds)
        {
            CancelPendingForClient(replacedConnectionId);
        }

        return true;
    }

    public void UnregisterClient(string connectionId)
    {
        BrowserClient? client = null;
        lock (_clientGate)
        {
            _clients.TryRemove(connectionId, out client);
        }

        if (client is not null)
        {
            CancelPendingForClient(client.ConnectionId);
        }
    }

    public async Task<BrowserWsResult> ExecuteCommandAsync(BrowserCommandRequest request, CancellationToken ct)
    {
        if (!TryResolveClient(request, out var client, out var error))
        {
            return new BrowserWsResult
            {
                Success = false,
                Error = error
            };
        }

        var id = Guid.NewGuid().ToString("N")[..12];
        var tcs = new TaskCompletionSource<BrowserWsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new PendingCommand
        {
            ConnectionId = client.ConnectionId,
            PreviewId = client.PreviewId,
            CompletionSource = tcs
        };

        var message = new BrowserWsMessage
        {
            Id = id,
            Command = request.Command,
            Selector = request.Selector,
            Value = request.Value,
            MaxDepth = request.MaxDepth,
            TextOnly = request.TextOnly,
            Timeout = request.Timeout,
            SessionId = client.SessionId,
            PreviewName = client.PreviewName,
            PreviewId = client.PreviewId
        };

        BrowserLog.Command(request.Command, request.Selector ?? request.Value);

        try
        {
            client.Listener(message);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            BrowserLog.Error($"Failed to send command: {ex.Message}");
            return new BrowserWsResult
            {
                Success = false,
                Error = $"Failed to send command to browser: {ex.Message}"
            };
        }

        var timeoutSeconds = ResolveTimeoutSeconds(request);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var result = await tcs.Task.WaitAsync(cts.Token);
            BrowserLog.Result(request.Command, result.Success, result.Result ?? result.Error ?? "");
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _pending.TryRemove(id, out _);
            BrowserLog.Result(request.Command, false, string.Create(CultureInfo.InvariantCulture, $"Timed out after {timeoutSeconds}s"));
            return new BrowserWsResult
            {
                Success = false,
                Error = string.Create(CultureInfo.InvariantCulture, $"Command timed out after {timeoutSeconds} seconds.")
            };
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    public void ReceiveResult(BrowserWsResult result)
    {
        if (!_pending.TryRemove(result.Id, out var pending))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(pending.PreviewId)
            && !string.IsNullOrWhiteSpace(result.PreviewId)
            && !string.Equals(pending.PreviewId, result.PreviewId, StringComparison.Ordinal))
        {
            pending.CompletionSource.TrySetResult(new BrowserWsResult
            {
                Id = result.Id,
                Success = false,
                Error = "Browser preview mismatch."
            });
            return;
        }

        pending.CompletionSource.TrySetResult(result);
    }

    public void CancelAllPending()
    {
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var pending))
            {
                pending.CompletionSource.TrySetResult(new BrowserWsResult
                {
                    Id = kvp.Key,
                    Success = false,
                    Error = "Browser disconnected."
                });
            }
        }
    }

    public string GetStatusText(
        string? targetUrl,
        string? sessionId = null,
        string? previewName = null,
        string? previewId = null,
        int connectedUiClientCount = 0)
    {
        var snapshot = GetStatusSnapshot(
            targetUrl,
            sessionId,
            previewName,
            previewId,
            connectedUiClientCount);
        var status = snapshot.Response;
        var headline = status.State switch
        {
            "ready" => "connected",
            "ambiguous" => "connected (ambiguous)",
            _ => "disconnected"
        };
        var clientLabel = status.IsScoped ? "selected" : "default";
        var lines = new List<string>
        {
            headline,
            $"state: {status.State}",
            $"bridge phase: {status.BridgePhase}",
            $"controllable: {(status.Controllable ? "yes" : "no")}",
            $"scope: {status.ScopeDescription ?? "(global)"}",
            $"target configured: {(status.HasTarget ? "yes" : "no")}",
            $"target: {status.TargetUrl ?? "(none)"}",
            $"control owner: {status.OwnerBrowserId ?? "(none)"}",
            $"owner connected: {(status.OwnerConnected ? "yes" : "no")}",
            string.Create(CultureInfo.InvariantCulture, $"ui clients: {status.ConnectedUiClientCount}"),
            string.Create(CultureInfo.InvariantCulture, $"matching browser clients: {status.ConnectedClientCount}"),
            string.Create(CultureInfo.InvariantCulture, $"browser clients total: {status.TotalConnectedClientCount}")
        };

        if (status.DefaultClient is { } client)
        {
            lines.Add($"{clientLabel} preview: {client.PreviewId ?? "(anonymous)"}");
            lines.Add($"{clientLabel} preview name: {client.PreviewName ?? "(default)"}");
            lines.Add($"{clientLabel} session: {client.SessionId ?? "(none)"}");
            lines.Add($"{clientLabel} browser: {client.BrowserId ?? "(none)"}");
            lines.Add($"{clientLabel} visible: {(client.IsVisible ? "yes" : "no")}");
            lines.Add($"{clientLabel} focused: {(client.HasFocus ? "yes" : "no")}");
            lines.Add($"{clientLabel} top level: {(client.IsTopLevel ? "yes" : "no")}");
        }
        else
        {
            lines.Add($"{clientLabel} preview: ambiguous");
        }

        if (!string.IsNullOrWhiteSpace(status.StatusMessage))
        {
            lines.Add($"reason: {status.StatusMessage}");
        }

        if (!string.IsNullOrWhiteSpace(status.RecoveryHint))
        {
            lines.Add($"recovery: {status.RecoveryHint}");
        }

        if (!status.HasUiClient)
        {
            lines.Add("hint: No MidTerm browser UI is attached to /ws/state. The dev browser cannot work until the owning MidTerm browser tab is open.");
        }

        if (status.State == "waiting" && status.HasTarget)
        {
            lines.Add("hint: The preview target is set, but no controllable browser has attached yet. Open the preview panel in MidTerm or wait for it to finish docking.");
        }

        if (status.Controllable && status.DefaultClient?.IsVisible == false)
        {
            lines.Add("hint: The selected preview bridge is controllable, but it is attached from a hidden frame. Re-run mt_open so MidTerm docks and refreshes the visible dev browser frame.");
        }

        if (status.State == "waiting" && !string.IsNullOrWhiteSpace(status.OwnerBrowserId) && !status.OwnerConnected)
        {
            lines.Add($"hint: Preview control is currently owned by browser '{status.OwnerBrowserId}', but that browser is not attached right now.");
        }

        if (status.State == "waiting" && !status.HasTarget)
        {
            lines.Add("hint: No preview target is configured yet. Use mt_open <url> first, then wait for the preview to become controllable.");
        }

        if (status.State == "ambiguous")
        {
            lines.Add("hint: Narrow the scope with --session, --preview, or --preview-id so MidTerm can pick a single browser preview.");
        }

        return string.Join('\n', lines) + "\n";
    }

    internal static int ResolveTimeoutSeconds(BrowserCommandRequest request)
    {
        if (request.Timeout is > 0)
        {
            return request.Timeout.Value;
        }

        return string.Equals(request.Command, "screenshot", StringComparison.OrdinalIgnoreCase)
            ? DefaultScreenshotTimeoutSeconds
            : DefaultCommandTimeoutSeconds;
    }

    public BrowserStatusResponse GetStatus(
        string? targetUrl,
        string? sessionId = null,
        string? previewName = null,
        string? previewId = null,
        int connectedUiClientCount = 0)
    {
        return GetStatusSnapshot(
            targetUrl,
            sessionId,
            previewName,
            previewId,
            connectedUiClientCount).Response;
    }

    public BrowserWsResult ClaimMainBrowser(BrowserCommandRequest request)
    {
        if (_mainBrowserService is null)
        {
            return new BrowserWsResult
            {
                Success = false,
                Error = "Main browser service is not available."
            };
        }

        BrowserClient client;
        if (!string.IsNullOrWhiteSpace(request.Value))
        {
            var browserId = request.Value.Trim();
            var matches = FilterClients(
                    _clients.Values.ToArray(),
                    request.SessionId,
                    request.PreviewName,
                    request.PreviewId)
                .Where(c => BrowserIdentity.AreSameBrowser(c.BrowserId, browserId))
                .OrderByDescending(c => string.Equals(c.BrowserId, browserId, StringComparison.Ordinal))
                .ThenByDescending(c => c.ConnectedAtUtc)
                .ToArray();

            if (matches.Length == 0)
            {
                return new BrowserWsResult
                {
                    Success = false,
                    Error = $"No connected browser client matches '{browserId}'."
                };
            }

            client = matches[0];
        }
        else if (!TryResolveClient(request, out client, out var error))
        {
            return new BrowserWsResult
            {
                Success = false,
                Error = error
            };
        }

        if (string.IsNullOrWhiteSpace(client.BrowserId))
        {
            return new BrowserWsResult
            {
                Success = false,
                Error = "Resolved browser client does not have a browser id."
            };
        }

        _mainBrowserService.Claim(client.BrowserId);
        return new BrowserWsResult
        {
            Success = true,
            Result = $"claimed leading browser {client.BrowserId}"
        };
    }

    public async Task<BrowserStatusResponse> WaitForControllableAsync(
        string? targetUrl,
        string? sessionId = null,
        string? previewName = null,
        string? previewId = null,
        DateTimeOffset? requireClientConnectedAfterUtc = null,
        bool requireVisibleClient = false,
        Func<int>? connectedUiClientCountProvider = null,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(8);
        var effectivePollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200);
        var deadline = DateTimeOffset.UtcNow + effectiveTimeout;
        BrowserStatusResponse? latest = null;

        while (true)
        {
            var connectedUiClientCount = connectedUiClientCountProvider?.Invoke() ?? 0;
            var snapshot = GetStatusSnapshot(
                targetUrl,
                sessionId,
                previewName,
                previewId,
                connectedUiClientCount);
            latest = snapshot.Response;

            var hasFreshClient = requireClientConnectedAfterUtc is null
                || (snapshot.DefaultClientConnectedAtUtc is { } connectedAt
                    && connectedAt >= requireClientConnectedAfterUtc.Value);
            var hasVisibleClient = !requireVisibleClient
                || latest.DefaultClient?.IsVisible == true;

            if (latest.Controllable && hasFreshClient && hasVisibleClient)
            {
                return latest;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return latest;
            }

            await Task.Delay(effectivePollInterval, cancellationToken);
        }
    }

    private BrowserStatusSnapshot GetStatusSnapshot(
        string? targetUrl,
        string? sessionId,
        string? previewName,
        string? previewId,
        int connectedUiClientCount)
    {
        var clients = _clients.Values
            .OrderByDescending(c => c.ConnectedAtUtc)
            .ToArray();
        var isScoped = HasStatusScope(sessionId, previewName, previewId);
        var hasTarget = !string.IsNullOrWhiteSpace(targetUrl);
        var hasUiClient = connectedUiClientCount > 0;
        var scopeDescription = BuildScopeDescription(sessionId, previewName, previewId);
        var ownerBrowserId = string.IsNullOrWhiteSpace(previewId)
            ? _previewOwnerService?.GetOwnerBrowserId(sessionId, previewName)
            : null;

        if (clients.Length == 0)
        {
            var message = BuildUnavailableStatusMessage(
                sessionId,
                previewName,
                previewId,
                ownerBrowserId,
                hasTarget,
                hasUiClient);
            return new BrowserStatusSnapshot
            {
                IsScoped = isScoped,
                Response = new BrowserStatusResponse
                {
                    Connected = false,
                    Controllable = false,
                    HasTarget = hasTarget,
                    HasUiClient = hasUiClient,
                    IsScoped = isScoped,
                    State = ResolveState(
                        connected: false,
                        controllable: false,
                        hasTarget: hasTarget,
                        hasUiClient: hasUiClient,
                        ambiguous: false),
                    BridgePhase = ResolveBridgePhase(
                        connected: false,
                        controllable: false,
                        hasTarget: hasTarget,
                        hasUiClient: hasUiClient,
                        ambiguous: false,
                        ownerBrowserId: ownerBrowserId,
                        ownerConnected: false),
                    ScopeDescription = scopeDescription,
                    StatusMessage = message,
                    RecoveryHint = BuildRecoveryHint(
                        hasTarget: hasTarget,
                        hasUiClient: hasUiClient,
                        connected: false,
                        ambiguous: false,
                        ownerBrowserId: ownerBrowserId,
                        ownerConnected: false),
                    ConnectedClientCount = 0,
                    TotalConnectedClientCount = 0,
                    ConnectedUiClientCount = connectedUiClientCount,
                    TargetUrl = targetUrl,
                    OwnerBrowserId = ownerBrowserId,
                    OwnerConnected = false
                }
            };
        }

        var matches = FilterClients(clients, sessionId, previewName, previewId);
        var resolvedOwnerBrowserId = string.IsNullOrWhiteSpace(previewId)
            ? _previewOwnerService?.ResolveOwnerBrowserId(
                sessionId,
                previewName,
                matches.Select(client => client.BrowserId))
            : null;
        ownerBrowserId = resolvedOwnerBrowserId ?? ownerBrowserId;
        var ownerConnected = !string.IsNullOrWhiteSpace(ownerBrowserId)
            && matches.Any(client => BrowserIdentity.AreSameBrowser(client.BrowserId, ownerBrowserId));
        var mainBrowserId = _mainBrowserService?.GetMainBrowserId();
        if (!ownerConnected
            && !string.IsNullOrWhiteSpace(ownerBrowserId)
            && TrySelectMainBrowserClient(matches, mainBrowserId, out var mainOwnedClient)
            && !string.IsNullOrWhiteSpace(mainOwnedClient.BrowserId))
        {
            _previewOwnerService?.Claim(sessionId, previewName, mainOwnedClient.BrowserId);
            ownerBrowserId = mainOwnedClient.BrowserId;
            ownerConnected = true;
        }

        if (matches.Length == 0)
        {
            var message = BuildUnavailableStatusMessage(
                sessionId,
                previewName,
                previewId,
                ownerBrowserId,
                hasTarget,
                hasUiClient);
            return new BrowserStatusSnapshot
            {
                IsScoped = isScoped,
                Response = new BrowserStatusResponse
                {
                    Connected = false,
                    Controllable = false,
                    HasTarget = hasTarget,
                    HasUiClient = hasUiClient,
                    IsScoped = isScoped,
                    State = ResolveState(
                        connected: false,
                        controllable: false,
                        hasTarget: hasTarget,
                        hasUiClient: hasUiClient,
                        ambiguous: false),
                    BridgePhase = ResolveBridgePhase(
                        connected: false,
                        controllable: false,
                        hasTarget: hasTarget,
                        hasUiClient: hasUiClient,
                        ambiguous: false,
                        ownerBrowserId: ownerBrowserId,
                        ownerConnected: false),
                    ScopeDescription = scopeDescription,
                    StatusMessage = message,
                    RecoveryHint = BuildRecoveryHint(
                        hasTarget: hasTarget,
                        hasUiClient: hasUiClient,
                        connected: false,
                        ambiguous: false,
                        ownerBrowserId: ownerBrowserId,
                        ownerConnected: false),
                    ConnectedClientCount = 0,
                    TotalConnectedClientCount = clients.Length,
                    ConnectedUiClientCount = connectedUiClientCount,
                    TargetUrl = targetUrl,
                    OwnerBrowserId = ownerBrowserId,
                    OwnerConnected = false
                }
            };
        }

        var resolutionCandidates = SelectResolutionCandidates(matches, ownerBrowserId);
        var resolved = TryResolveDefaultClient(resolutionCandidates, out var client);
        var isAmbiguous = !resolved && string.IsNullOrWhiteSpace(ownerBrowserId);
        var statusMessage = resolved
            ? null
            : !string.IsNullOrWhiteSpace(ownerBrowserId)
                ? BuildOwnerUnavailableStatusMessage(sessionId, previewName, ownerBrowserId)
                : BuildAmbiguousStatusMessage(sessionId, previewName, previewId);
        return new BrowserStatusSnapshot
        {
            IsScoped = isScoped,
            DefaultClientConnectedAtUtc = resolved ? client.ConnectedAtUtc : null,
            Response = new BrowserStatusResponse
            {
                Connected = true,
                Controllable = resolved,
                HasTarget = hasTarget,
                HasUiClient = hasUiClient,
                IsScoped = isScoped,
                State = ResolveState(
                    connected: true,
                    controllable: resolved,
                    hasTarget: hasTarget,
                    hasUiClient: hasUiClient,
                    ambiguous: isAmbiguous),
                BridgePhase = ResolveBridgePhase(
                    connected: true,
                    controllable: resolved,
                    hasTarget: hasTarget,
                    hasUiClient: hasUiClient,
                    ambiguous: isAmbiguous,
                    ownerBrowserId: ownerBrowserId,
                    ownerConnected: ownerConnected),
                ScopeDescription = scopeDescription,
                StatusMessage = statusMessage,
                RecoveryHint = BuildRecoveryHint(
                    hasTarget: hasTarget,
                    hasUiClient: hasUiClient,
                    connected: true,
                    ambiguous: isAmbiguous,
                    ownerBrowserId: ownerBrowserId,
                    ownerConnected: ownerConnected),
                ConnectedClientCount = matches.Length,
                TotalConnectedClientCount = clients.Length,
                ConnectedUiClientCount = connectedUiClientCount,
                TargetUrl = targetUrl,
                OwnerBrowserId = ownerBrowserId,
                OwnerConnected = ownerConnected,
                DefaultClient = resolved ? CreateClientInfo(client, mainBrowserId) : null,
                Clients = matches
                    .Select(c => CreateClientInfo(c, mainBrowserId))
                    .ToArray()
            }
        };
    }

    private static BrowserClient[] FilterClients(
        BrowserClient[] clients,
        string? sessionId,
        string? previewName,
        string? previewId)
    {
        if (!HasStatusScope(sessionId, previewName, previewId))
        {
            return clients;
        }

        return clients
            .Where(c =>
                (string.IsNullOrWhiteSpace(previewId)
                    || string.Equals(c.PreviewId, previewId, StringComparison.Ordinal))
                && (string.IsNullOrWhiteSpace(sessionId)
                    || string.Equals(c.SessionId, sessionId, StringComparison.Ordinal))
                && (string.IsNullOrWhiteSpace(previewName)
                    || string.Equals(c.PreviewName, previewName, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static bool HasStatusScope(string? sessionId, string? previewName, string? previewId)
    {
        return !string.IsNullOrWhiteSpace(sessionId)
            || !string.IsNullOrWhiteSpace(previewName)
            || !string.IsNullOrWhiteSpace(previewId);
    }

    private static string BuildScopeDescription(string? sessionId, string? previewName, string? previewId)
    {
        if (!string.IsNullOrWhiteSpace(previewId))
        {
            return $"preview '{previewId}'";
        }

        if (!string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(previewName))
        {
            return $"session '{sessionId}', preview '{previewName}'";
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            return $"session '{sessionId}'";
        }

        if (!string.IsNullOrWhiteSpace(previewName))
        {
            return $"preview '{previewName}'";
        }

        return "(global)";
    }

    private static string BuildUnavailableStatusMessage(
        string? sessionId,
        string? previewName,
        string? previewId,
        string? ownerBrowserId,
        bool hasTarget,
        bool hasUiClient)
    {
        if (!string.IsNullOrWhiteSpace(ownerBrowserId))
        {
            return BuildOwnerUnavailableStatusMessage(sessionId, previewName, ownerBrowserId);
        }

        var reason = BuildDisconnectedReason(sessionId, previewName, previewId);
        if (hasTarget && hasUiClient)
        {
            return $"Target is configured, but no browser preview is attached yet. {reason}";
        }

        if (hasTarget)
        {
            return $"Target is configured, but no MidTerm browser UI is currently attached to /ws/state, so the dev browser cannot work yet. {reason}";
        }

        if (hasUiClient)
        {
            return $"A MidTerm UI is connected, but no matching browser preview is attached. {reason}";
        }

        return reason;
    }

    private static string BuildDisconnectedReason(string? sessionId, string? previewName, string? previewId)
    {
        if (!string.IsNullOrWhiteSpace(previewId))
        {
            return $"No browser preview connected for preview '{previewId}'.";
        }

        if (!string.IsNullOrWhiteSpace(previewName))
        {
            return $"No browser preview connected for preview '{previewName}' in session '{sessionId ?? "(any)"}'.";
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            return $"No browser preview connected for session '{sessionId}'.";
        }

        return "Open the web preview panel in a live MidTerm browser tab to enable browser commands.";
    }

    private static string BuildAmbiguousStatusMessage(string? sessionId, string? previewName, string? previewId)
    {
        if (!string.IsNullOrWhiteSpace(previewId))
        {
            return $"Multiple browser clients are attached for preview '{previewId}'.";
        }

        if (!string.IsNullOrWhiteSpace(previewName))
        {
            return $"Multiple browser clients are attached for preview '{previewName}' in session '{sessionId ?? "(any)"}'.";
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            return $"Multiple browser clients are attached for session '{sessionId}'.";
        }

        return "Multiple browser previews are connected. Narrow the scope so MidTerm can select one deterministically.";
    }

    private static string BuildOwnerUnavailableStatusMessage(
        string? sessionId,
        string? previewName,
        string ownerBrowserId)
    {
        return $"Preview '{previewName ?? WebPreview.WebPreviewService.DefaultPreviewName}' in session '{sessionId ?? "(any)"}' is owned by browser '{ownerBrowserId}', but that browser is not currently attached.";
    }

    private static string ResolveState(
        bool connected,
        bool controllable,
        bool hasTarget,
        bool hasUiClient,
        bool ambiguous)
    {
        if (connected && controllable)
        {
            return "ready";
        }

        if (ambiguous)
        {
            return "ambiguous";
        }

        if (connected || hasTarget || hasUiClient)
        {
            return "waiting";
        }

        return "disconnected";
    }

    private static string ResolveBridgePhase(
        bool connected,
        bool controllable,
        bool hasTarget,
        bool hasUiClient,
        bool ambiguous,
        string? ownerBrowserId,
        bool ownerConnected)
    {
        if (controllable)
        {
            return "ready";
        }

        if (ambiguous)
        {
            return "ambiguous-preview";
        }

        if (!hasUiClient)
        {
            return "no-ui-client";
        }

        if (!hasTarget)
        {
            return "no-target";
        }

        if (!string.IsNullOrWhiteSpace(ownerBrowserId) && !ownerConnected)
        {
            return "owner-offline";
        }

        if (!connected)
        {
            return "preview-frame-disconnected";
        }

        return "unresolved-preview-client";
    }

    private static string? BuildRecoveryHint(
        bool hasTarget,
        bool hasUiClient,
        bool connected,
        bool ambiguous,
        string? ownerBrowserId,
        bool ownerConnected)
    {
        if (!hasUiClient)
        {
            return "Open or reload the owning MidTerm browser tab so /ws/state can receive browser UI instructions.";
        }

        if (!hasTarget)
        {
            return "Run mt_open <url> to configure and dock a preview target.";
        }

        if (!string.IsNullOrWhiteSpace(ownerBrowserId) && !ownerConnected)
        {
            return "Run mt_claim_preview to explicitly assign this preview to the connected MidTerm browser, then retry mt_open or mt_reload.";
        }

        if (ambiguous)
        {
            return "Run the command with --session and --preview, or switch with mt_preview <name>.";
        }

        if (!connected)
        {
            return "The target is configured but the iframe has not attached to /ws/browser; retry mt_open, mt_reload, or inspect the MidTerm browser console.";
        }

        return null;
    }

    private void CancelPendingForClient(string connectionId)
    {
        foreach (var kvp in _pending)
        {
            if (!string.Equals(kvp.Value.ConnectionId, connectionId, StringComparison.Ordinal))
            {
                continue;
            }

            if (_pending.TryRemove(kvp.Key, out var pending))
            {
                pending.CompletionSource.TrySetResult(new BrowserWsResult
                {
                    Id = kvp.Key,
                    Success = false,
                    Error = "Browser disconnected."
                });
            }
        }
    }

    private bool TryResolveClient(
        BrowserCommandRequest request,
        out BrowserClient client,
        out string error)
    {
        error = "";
        client = null!;

        var clients = _clients.Values.ToArray();
        if (clients.Length == 0)
        {
            error = "No browser connected. The dev browser cannot work until a live MidTerm browser tab is attached to /ws/state.";
            return false;
        }

        BrowserClient[] matches;
        if (!string.IsNullOrWhiteSpace(request.PreviewId))
        {
            matches = clients
                .Where(c => string.Equals(c.PreviewId, request.PreviewId, StringComparison.Ordinal))
                .OrderByDescending(c => c.ConnectedAtUtc)
                .ToArray();

            if (matches.Length == 0)
            {
                error = $"No browser preview connected for preview '{request.PreviewId}'.";
                return false;
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.SessionId) || !string.IsNullOrWhiteSpace(request.PreviewName))
        {
            matches = clients
                .Where(c =>
                    (string.IsNullOrWhiteSpace(request.SessionId)
                        || string.Equals(c.SessionId, request.SessionId, StringComparison.Ordinal))
                    && (string.IsNullOrWhiteSpace(request.PreviewName)
                        || string.Equals(c.PreviewName, request.PreviewName, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(c => c.ConnectedAtUtc)
                .ToArray();

            if (matches.Length == 0)
            {
                error = !string.IsNullOrWhiteSpace(request.PreviewName)
                    ? $"No browser preview connected for preview '{request.PreviewName}' in session '{request.SessionId ?? "(any)"}'."
                    : $"No browser preview connected for session '{request.SessionId}'.";
                return false;
            }

            var scopedMatches = matches;
            var ownerBrowserId = _previewOwnerService?.ResolveOwnerBrowserId(
                request.SessionId,
                request.PreviewName,
                scopedMatches.Select(client => client.BrowserId));
            matches = SelectResolutionCandidates(scopedMatches, ownerBrowserId);
            if (matches.Length == 0
                && !string.IsNullOrWhiteSpace(ownerBrowserId)
                && TrySelectMainBrowserClient(
                    scopedMatches,
                    _mainBrowserService?.GetMainBrowserId(),
                    out var mainOwnedClient)
                && !string.IsNullOrWhiteSpace(mainOwnedClient.BrowserId))
            {
                _previewOwnerService?.Claim(request.SessionId, request.PreviewName, mainOwnedClient.BrowserId);
                ownerBrowserId = mainOwnedClient.BrowserId;
                matches = SelectResolutionCandidates(scopedMatches, ownerBrowserId);
            }

            if (matches.Length == 0 && !string.IsNullOrWhiteSpace(ownerBrowserId))
            {
                error = BuildOwnerUnavailableStatusMessage(
                    request.SessionId,
                    request.PreviewName,
                    ownerBrowserId);
                return false;
            }
        }
        else
        {
            matches = clients
                .OrderByDescending(c => c.ConnectedAtUtc)
                .ToArray();
        }

        matches = PreferInteractive(matches);
        matches = PreferPreviewScoped(matches);
        matches = PreferMainBrowser(matches);

        if (matches.Length > 1)
        {
            if (TryResolveDefaultClient(matches, out client))
            {
                return true;
            }

            error = string.IsNullOrWhiteSpace(request.SessionId)
                ? "Multiple browser previews are connected. Re-run the command with --session <id>."
                : !string.IsNullOrWhiteSpace(request.PreviewName)
                    ? $"Multiple browser previews are connected for preview '{request.PreviewName}' in session '{request.SessionId}'."
                    : $"Multiple browser previews are connected for session '{request.SessionId}'.";
            return false;
        }

        client = matches[0];
        return true;
    }

    private static BrowserClient[] PreferInteractive(BrowserClient[] clients)
    {
        if (clients.Length == 0)
        {
            return clients;
        }

        var bestScore = clients.Max(GetInteractiveScore);
        return clients
            .Where(c => GetInteractiveScore(c) == bestScore)
            .ToArray();
    }

    private static BrowserClient[] SelectResolutionCandidates(BrowserClient[] clients, string? ownerBrowserId)
    {
        if (clients.Length == 0 || string.IsNullOrWhiteSpace(ownerBrowserId))
        {
            return clients;
        }

        var ownerClients = clients
            .Where(client => BrowserIdentity.AreSameBrowser(client.BrowserId, ownerBrowserId))
            .ToArray();
        return ownerClients.Length > 0 ? ownerClients : [];
    }

    private BrowserClient[] PreferPreviewScoped(BrowserClient[] clients)
    {
        var scoped = clients
            .Where(c => !string.IsNullOrWhiteSpace(c.PreviewId))
            .ToArray();
        return scoped.Length > 0 ? scoped : clients;
    }

    private BrowserClient[] PreferMainBrowser(BrowserClient[] clients)
    {
        var mainBrowserId = _mainBrowserService?.GetMainBrowserId();
        if (string.IsNullOrWhiteSpace(mainBrowserId))
        {
            return clients;
        }

        var main = clients
            .Where(c => BrowserIdentity.AreSameBrowser(c.BrowserId, mainBrowserId))
            .ToArray();
        return main.Length > 0 ? main : clients;
    }

    private bool TryResolveDefaultClient(BrowserClient[] clients, out BrowserClient client)
    {
        client = null!;
        if (clients.Length == 0)
        {
            return false;
        }

        var preferred = PreferMainBrowser(PreferPreviewScoped(PreferInteractive(clients)));
        if (preferred.Length == 0)
        {
            return false;
        }

        var distinctBrowserIds = preferred
            .Select(c => c.BrowserId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Select(BrowserIdentity.GetClientPart)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (distinctBrowserIds.Length > 1)
        {
            return false;
        }

        client = preferred
            .OrderByDescending(c => c.ConnectedAtUtc)
            .First();
        return true;
    }

    private static BrowserClientInfo CreateClientInfo(BrowserClient client, string? mainBrowserId)
    {
        return new BrowserClientInfo
        {
            SessionId = client.SessionId,
            PreviewName = client.PreviewName,
            PreviewId = client.PreviewId,
            BrowserId = client.BrowserId,
            ConnectedAtUtc = client.ConnectedAtUtc,
            IsMainBrowser = !string.IsNullOrWhiteSpace(mainBrowserId)
                && BrowserIdentity.AreSameBrowser(client.BrowserId, mainBrowserId),
            IsVisible = client.IsVisible,
            HasFocus = client.HasFocus,
            IsTopLevel = client.IsTopLevel
        };
    }

    private static int GetInteractiveScore(BrowserClient client)
    {
        var score = 0;
        if (client.HasFocus)
        {
            score += 4;
        }

        if (client.IsVisible)
        {
            score += 2;
        }

        if (client.IsTopLevel)
        {
            score += 1;
        }

        return score;
    }

    private static bool TrySelectMainBrowserClient(
        BrowserClient[] clients,
        string? mainBrowserId,
        out BrowserClient client)
    {
        client = null!;
        if (string.IsNullOrWhiteSpace(mainBrowserId))
        {
            return false;
        }

        var mainClients = clients
            .Where(c => BrowserIdentity.AreSameBrowser(c.BrowserId, mainBrowserId))
            .ToArray();
        if (mainClients.Length == 0)
        {
            return false;
        }

        client = mainClients
            .OrderByDescending(c => string.Equals(c.BrowserId, mainBrowserId, StringComparison.Ordinal))
            .ThenByDescending(c => c.ConnectedAtUtc)
            .First();
        return true;
    }

    private sealed class BrowserClient
    {
        public string ConnectionId { get; init; } = "";
        public string? SessionId { get; init; }
        public string? PreviewName { get; init; }
        public string? PreviewId { get; init; }
        public string? BrowserId { get; init; }
        public bool IsVisible { get; init; }
        public bool HasFocus { get; init; }
        public bool IsTopLevel { get; init; }
        public required Action<BrowserWsMessage> Listener { get; init; }
        public DateTimeOffset ConnectedAtUtc { get; init; }
    }

    private sealed class PendingCommand
    {
        public string ConnectionId { get; init; } = "";
        public string? PreviewId { get; init; }
        public required TaskCompletionSource<BrowserWsResult> CompletionSource { get; init; }
    }

    private sealed class BrowserStatusSnapshot
    {
        public required BrowserStatusResponse Response { get; init; }
        public bool IsScoped { get; init; }
        public DateTimeOffset? DefaultClientConnectedAtUtc { get; init; }
    }
}
