using System.Collections.Concurrent;
using System.Globalization;
using Ai.Tlbx.MidTerm.Models.Browser;

namespace Ai.Tlbx.MidTerm.Services.Browser;

public sealed class BrowserCommandService
{
    private const int DefaultCommandTimeoutSeconds = 10;
    private const int DefaultScreenshotTimeoutSeconds = 30;
    private readonly Lock _clientGate = new();
    private TaskCompletionSource _clientChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, PendingCommand> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BrowserClient> _clients = new(StringComparer.Ordinal);
    private readonly MainBrowserService? _mainBrowserService;
    private readonly BrowserPreviewOwnerService? _previewOwnerService;
    private readonly WebPreview.WebPreviewService? _webPreviewService;

    public BrowserCommandService(
        MainBrowserService? mainBrowserService = null,
        BrowserPreviewOwnerService? previewOwnerService = null,
        WebPreview.WebPreviewService? webPreviewService = null)
    {
        _mainBrowserService = mainBrowserService;
        _previewOwnerService = previewOwnerService;
        _webPreviewService = webPreviewService;
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
        bool isTopLevel = false,
        long? targetRevision = null,
        long? ownershipGeneration = null)
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
                TargetRevision = targetRevision,
                OwnershipGeneration = ownershipGeneration,
                Listener = listener,
                ConnectedAtUtc = DateTimeOffset.UtcNow
            };
        }

        foreach (var replacedConnectionId in replacedConnectionIds)
        {
            CancelPendingForClient(replacedConnectionId);
        }

        SignalClientChanged();

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
            SignalClientChanged();
        }
    }

    private void SignalClientChanged() =>
        Interlocked.Exchange(ref _clientChanged, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    public async Task<BrowserWsResult> ExecuteCommandAsync(BrowserCommandRequest request, CancellationToken ct)
    {
        var grace = request.Command == "wait"
            ? TimeSpan.FromSeconds(ResolveTimeoutSeconds(request)) : BridgeAttachGrace;
        return await ExecuteCommandOnceAsync(request, DateTime.UtcNow + grace, ct).ConfigureAwait(false);
    }

    public async Task<BrowserBatchResponse> ExecuteBatchAsync(BrowserBatchRequest request, CancellationToken ct)
    {
        var result = new BrowserBatchResponse();
        var timeoutSeconds = request.Timeout ?? 60;
        if (string.IsNullOrWhiteSpace(request.SessionId) || request.Commands is null
            || request.Commands.Count is < 1 or > 32 || timeoutSeconds is < 1 or > 120)
        {
            result.Error = "A batch requires sessionId, 1–32 commands, and a timeout of 1–120 seconds.";
            return result;
        }
        if (request.Commands.Any(c => c is null || c.Command is not
            ("query" or "click" or "fill" or "exec" or "wait" or "screenshot" or "scroll" or "wheel"
            or "navigate" or "reload" or "outline" or "attrs" or "css" or "log" or "links" or "submit" or "forms" or "url")))
        {
            result.Error = "Unsupported batch command. Nested batches and ownership changes are not allowed.";
            return result;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var owner = _previewOwnerService?.ResolveOwnerBrowserId(request.SessionId, request.PreviewName);
        var generation = _previewOwnerService?.GetGeneration(request.SessionId, request.PreviewName);
        var revision = _webPreviewService?.GetPreviewSession(request.SessionId, request.PreviewName)?.TargetRevision;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        for (var i = 0; i < request.Commands.Count; i++)
        {
            var command = request.Commands[i];
            var stepStartedAt = DateTimeOffset.UtcNow;
            var stepClock = System.Diagnostics.Stopwatch.StartNew();
            BrowserWsResult step;
            if ((_previewOwnerService is not null && !_previewOwnerService.IsCurrent(request.SessionId, request.PreviewName, owner, generation ?? 0))
                || _webPreviewService?.GetPreviewSession(request.SessionId, request.PreviewName)?.TargetRevision != revision)
            {
                step = new BrowserWsResult { Error = "Preview ownership or target changed; remaining batch steps were not run." };
            }
            else
            {
                try
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    step = await ExecuteCommandAsync(new BrowserCommandRequest
                    {
                        Command = command.Command, Selector = command.Selector, Value = command.Value,
                        MaxDepth = command.MaxDepth, TextOnly = command.TextOnly, Timeout = command.Timeout,
                        DeltaX = command.DeltaX, DeltaY = command.DeltaY, Steps = command.Steps, FullPage = command.FullPage,
                        SessionId = request.SessionId, PreviewName = request.PreviewName
                    }, deadline.Token).ConfigureAwait(false);
                    if (step.Success && (command.WaitForNavigation || command.Command is "navigate" or "reload"))
                    {
                        var status = await WaitForControllableAsync(
                            _webPreviewService?.GetPreviewSession(request.SessionId, request.PreviewName)?.Url,
                            request.SessionId, request.PreviewName,
                            timeout: TimeSpan.FromSeconds(command.Timeout ?? 15),
                            requireClientConnectedAfterUtc: stepStartedAt,
                            cancellationToken: deadline.Token,
                            requiredTargetRevision: revision).ConfigureAwait(false);
                        if (!status.Controllable)
                            step = new BrowserWsResult { Error = "Navigation was dispatched, but the new document did not become controllable. Remaining steps were not run; do not replay the action." };
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    step = new BrowserWsResult { Error = "Batch deadline exceeded. The current step may have executed; inspect state before retrying. Remaining steps were not run." };
                }
            }
            result.Results.Add(new BrowserBatchStepResult
            {
                Index = i, Command = command.Command, Success = step.Success, Result = step.Result,
                Error = step.Error, MatchCount = step.MatchCount, DurationMs = stepClock.Elapsed.TotalMilliseconds
            });
            if (!step.Success)
            {
                result.FailedIndex = i;
                result.Error = step.Error;
                break;
            }
        }
        result.Success = result.Results.Count == request.Commands.Count && result.FailedIndex is null;
        result.DurationMs = clock.Elapsed.TotalMilliseconds;
        return result;
    }

    private async Task<BrowserWsResult> ExecuteCommandOnceAsync(
        BrowserCommandRequest request,
        DateTime deadline,
        CancellationToken ct,
        BrowserClient? expectedClient = null,
        long? expectedGeneration = null)
    {
        var (client, error) = await ResolveClientWithAttachGraceAsync(request, deadline, ct).ConfigureAwait(false);
        if (client is null)
        {
            return new BrowserWsResult
            {
                Success = false,
                Error = error
            };
        }

        var generation = _previewOwnerService?.GetGeneration(client.SessionId, client.PreviewName) ?? 0;
        if (expectedClient is not null && (generation != expectedGeneration
                || client.BrowserId != expectedClient.BrowserId
                || client.TargetRevision != expectedClient.TargetRevision))
            return new BrowserWsResult { Error = "Preview ownership or target changed while waiting for navigation." };
        if (_previewOwnerService is not null && !_previewOwnerService.IsCurrent(client.SessionId, client.PreviewName, client.BrowserId, generation))
            return new BrowserWsResult { Error = "Preview ownership changed before dispatch. Retry the command." };
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
            DeltaX = request.DeltaX,
            DeltaY = request.DeltaY,
            Steps = request.Steps,
            FullPage = request.FullPage,
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
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        if (request.Command == "wait")
            timeout = TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(timeout.TotalMilliseconds, (deadline - DateTime.UtcNow).TotalMilliseconds)));
        cts.CancelAfter(timeout);

        try
        {
            var result = await tcs.Task.WaitAsync(cts.Token);
            if ((_previewOwnerService is not null
                    && !_previewOwnerService.IsCurrent(client.SessionId, client.PreviewName, client.BrowserId, generation))
                || !IsCurrentTarget(client))
                return new BrowserWsResult { Error = "Preview ownership or target changed while the command was in flight. Its outcome is unknown; inspect the current preview before retrying an action." };
            // Waiting for a selector is read-only. A document navigation can close its
            // bridge after dispatch; continue on the same owner/target within one deadline.
            // Never retry a dispatched action or arbitrary JavaScript.
            if (request.Command == "wait" && result.Error == BridgeDisconnectedError && DateTime.UtcNow < deadline)
                return await ExecuteCommandOnceAsync(request, deadline, ct, client, generation).ConfigureAwait(false);
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

    public void UpdateClientState(string connectionId, BrowserWsResult state)
    {
        if (_clients.TryGetValue(connectionId, out var client))
        {
            client.IsVisible = state.Visible;
            client.HasFocus = state.Focus;
            client.IsTopLevel = state.TopLevel;
            SignalClientChanged();
        }
    }

    public void ReceiveResult(BrowserWsResult result, string? connectionId = null)
    {
        if (connectionId is not null && _pending.TryGetValue(result.Id, out var expected)
            && !string.Equals(connectionId, expected.ConnectionId, StringComparison.Ordinal)) return;
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
                    Error = BridgeDisconnectedError
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
            string.Create(CultureInfo.InvariantCulture, $"ownership generation: {status.OwnershipGeneration}"),
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
            lines.Add("hint: No tlbx browser UI is attached to /ws/state. The dev browser cannot work until the owning tlbx browser tab is open.");
        }

        if (status.State == "waiting" && status.HasTarget)
        {
            lines.Add("hint: The preview target is set, but no controllable browser has attached yet. Open the preview panel in tlbx or wait for it to finish docking.");
        }

        if (status.Controllable && status.DefaultClient?.IsVisible == false)
        {
            lines.Add("hint: The selected preview bridge is controllable, but it is attached from a hidden frame. It remains available across session switches; a suspended or closed owning browser cannot execute commands.");
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
            lines.Add("hint: Narrow the scope with --session, --preview, or --preview-id so tlbx can pick a single browser preview.");
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
        long? requiredTargetRevision = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(8);
        var effectivePollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200);
        var deadline = DateTimeOffset.UtcNow + effectiveTimeout;
        BrowserStatusResponse? latest = null;

        while (true)
        {
            var changed = Volatile.Read(ref _clientChanged).Task;
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

            var hasRevision = requiredTargetRevision is null || latest.DefaultClient?.TargetRevision == requiredTargetRevision;
            if (latest.Controllable && hasFreshClient && hasVisibleClient && hasRevision)
            {
                return latest;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                // A stale/hidden attachment must never return a ready snapshot on timeout.
                return new BrowserStatusResponse
                {
                    State = "waiting", BridgePhase = "preview-not-ready", HasTarget = latest.HasTarget,
                    HasUiClient = latest.HasUiClient, IsScoped = latest.IsScoped,
                    TargetUrl = targetUrl, OwnerBrowserId = latest.OwnerBrowserId,
                    OwnerConnected = latest.OwnerConnected, DefaultClient = latest.DefaultClient,
                    StatusMessage = "The exact preview revision did not become ready before the deadline. Retry mt_open or reconnect its owner.",
                    ConnectedUiClientCount = connectedUiClientCount
                };
            }

            // Wake immediately when a document bridge attaches. Keep polling only as
            // a fallback for owner/UI changes that do not touch a browser client.
            try { await changed.WaitAsync(effectivePollInterval, cancellationToken); }
            catch (TimeoutException) { }
        }
    }

    private BrowserStatusSnapshot GetStatusSnapshot(
        string? targetUrl,
        string? sessionId,
        string? previewName,
        string? previewId,
        int connectedUiClientCount)
    {
        var clients = _clients.Values.ToArray();
        var matches = FilterClients(clients, sessionId, previewName, previewId);
        var owner = _previewOwnerService?.ResolveOwnerBrowserId(sessionId, previewName);
        var resolved = TryResolveClient(new BrowserCommandRequest
        {
            SessionId = sessionId, PreviewName = previewName, PreviewId = previewId, Command = "status"
        }, out var client, out var error);
        var ownerConnected = _previewOwnerService?.IsConnected(owner) == true;
        var ambiguous = !resolved && (error.StartsWith("Multiple", StringComparison.Ordinal));
        var hasTarget = !string.IsNullOrWhiteSpace(targetUrl);
        var hasUi = connectedUiClientCount > 0;
        return new BrowserStatusSnapshot
        {
            IsScoped = HasStatusScope(sessionId, previewName, previewId),
            DefaultClientConnectedAtUtc = resolved ? client.ConnectedAtUtc : null,
            Response = new BrowserStatusResponse
            {
                Connected = matches.Length > 0, Controllable = resolved,
                HasTarget = hasTarget, HasUiClient = hasUi,
                IsScoped = HasStatusScope(sessionId, previewName, previewId),
                State = ResolveState(matches.Length > 0, resolved, hasTarget, hasUi, ambiguous),
                BridgePhase = ResolveBridgePhase(matches.Length > 0, resolved, hasTarget, hasUi, ambiguous, owner, ownerConnected),
                ScopeDescription = BuildScopeDescription(sessionId, previewName, previewId),
                StatusMessage = resolved ? null : error,
                RecoveryHint = resolved ? null : BuildRecoveryHint(hasTarget, hasUi, matches.Length > 0, ambiguous, owner, ownerConnected),
                ConnectedClientCount = matches.Length, TotalConnectedClientCount = clients.Length,
                ConnectedUiClientCount = connectedUiClientCount, TargetUrl = targetUrl,
                OwnerBrowserId = owner, OwnerConnected = ownerConnected,
                OwnershipGeneration = _previewOwnerService?.GetGeneration(sessionId, previewName) ?? 0,
                DefaultClient = resolved ? CreateClientInfo(client, _mainBrowserService?.GetMainBrowserId()) : null,
                Clients = matches.Select(c => CreateClientInfo(c, _mainBrowserService?.GetMainBrowserId())).ToArray()
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

        return "Open the web preview panel in a live tlbx browser tab to enable browser commands.";
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

        return "Multiple browser previews are connected. Narrow the scope with --session and --preview so tlbx can select one deterministically.";
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
            return "Open or reload the owning tlbx browser tab so /ws/state can receive browser UI instructions.";
        }

        if (!hasTarget)
        {
            return "Run mt_open <url> to configure and dock a preview target.";
        }

        if (!string.IsNullOrWhiteSpace(ownerBrowserId) && !ownerConnected)
        {
            return "List /api/browser/ui-clients, then run mt_claim_preview --browser <browserId> to explicitly hand off this preview and retry mt_open.";
        }

        if (ambiguous)
        {
            return "Run the command with --session and --preview, or switch with mt_preview <name>.";
        }

        if (!connected)
        {
            return "The target is configured but the iframe has not attached to /ws/browser; retry mt_open, mt_reload, or inspect the tlbx browser console.";
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
                    Error = BridgeDisconnectedError
                });
            }
        }
    }

    // Reload, navigation, and viewport changes tear the preview bridge down for a moment while the
    // iframe reattaches to /ws/browser. Commands issued in that window should wait for the bridge
    // instead of failing hard; only the no-client-yet errors are transient, ambiguity is not.
    private static readonly TimeSpan BridgeAttachGrace = TimeSpan.FromSeconds(8);
    private const string BridgeDisconnectedError = "Browser disconnected.";

    private async Task<(BrowserClient? Client, string Error)> ResolveClientWithAttachGraceAsync(
        BrowserCommandRequest request,
        DateTime deadline,
        CancellationToken ct)
    {
        while (true)
        {
            if (TryResolveClient(request, out var client, out var error))
            {
                return (client, "");
            }

            var transient = error.StartsWith("No browser", StringComparison.Ordinal);
            if (!transient || DateTime.UtcNow >= deadline || ct.IsCancellationRequested)
            {
                return (null, error);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return (null, error);
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
        var matches = FilterClients(_clients.Values.ToArray(), request.SessionId, request.PreviewName, request.PreviewId);
        if (HasStatusScope(request.SessionId, request.PreviewName, request.PreviewId))
        {
            // Explicit preview IDs still obey the session/preview's exact-tab execution owner.
            var sessionId = request.SessionId ?? (matches.Length == 1 ? matches[0].SessionId : null);
            var previewName = request.PreviewName ?? (matches.Length == 1 ? matches[0].PreviewName : null);
            var owner = _previewOwnerService?.ResolveOwnerBrowserId(sessionId, previewName);
            if (owner is not null)
            {
                if (!_previewOwnerService!.IsConnected(owner))
                {
                    error = BrowserPreviewOwnerService.UnavailableMessage(sessionId, previewName, owner);
                    return false;
                }
                matches = SelectResolutionCandidates(matches, owner)
                    .Where(c => c.OwnershipGeneration is null || c.OwnershipGeneration == _previewOwnerService.GetGeneration(sessionId, previewName)).ToArray();
            }
            else if (_previewOwnerService is not null && !string.IsNullOrWhiteSpace(sessionId))
            {
                error = _previewOwnerService.GetConnectedBrowserIds().Length == 0
                    ? "No browser UI connected. Open a tlbx tab and retry mt_open."
                    : BrowserPreviewOwnerService.UnavailableMessage(sessionId, previewName, null);
                return false;
            }
            matches = matches.Where(IsCurrentTarget).ToArray();
            if (matches.Length != 1)
            {
                error = matches.Length == 0
                    ? BuildDisconnectedReason(sessionId, previewName, request.PreviewId)
                    : BuildAmbiguousStatusMessage(sessionId, previewName, request.PreviewId);
                return false;
            }
            client = matches[0];
            return true;
        }
        if (TryResolveDefaultClient(matches, out client)) return true;
        error = matches.Length == 0 ? "No browser connected. Open a tlbx tab and retry mt_open."
            : BuildAmbiguousStatusMessage(null, null, null);
        return false;
    }

    private bool IsCurrentTarget(BrowserClient client)
    {
        if (_webPreviewService is null || string.IsNullOrWhiteSpace(client.SessionId)) return true;
        var target = _webPreviewService.GetPreviewSession(client.SessionId, client.PreviewName);
        return target?.Url is not null && client.TargetRevision == target.TargetRevision;
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
            .Where(client => string.Equals(client.BrowserId, ownerBrowserId, StringComparison.Ordinal))
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
            IsTopLevel = client.IsTopLevel,
            TargetRevision = client.TargetRevision
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

    private sealed class BrowserClient
    {
        public string ConnectionId { get; init; } = "";
        public string? SessionId { get; init; }
        public string? PreviewName { get; init; }
        public string? PreviewId { get; init; }
        public string? BrowserId { get; init; }
        public bool IsVisible { get; set; }
        public bool HasFocus { get; set; }
        public bool IsTopLevel { get; set; }
        public long? TargetRevision { get; init; }
        public long? OwnershipGeneration { get; init; }
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
