namespace Ai.Tlbx.MidTerm.Services.Browser;

using Ai.Tlbx.MidTerm.Models.Browser;

public sealed class BrowserUiBridge
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ListenerRegistration> _listeners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<AgentHistoryWheelResult>> _pendingAgentWheels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<BrowserUiCommandResult>> _pendingUiCommands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TargetOperationGate> _targetOperationGates = new(StringComparer.Ordinal);
    private readonly MainBrowserService _mainBrowserService;
    private readonly BrowserPreviewOwnerService _previewOwnerService;

    public BrowserUiBridge(
        MainBrowserService mainBrowserService,
        BrowserPreviewOwnerService? previewOwnerService = null)
    {
        _mainBrowserService = mainBrowserService;
        _previewOwnerService = previewOwnerService ?? new BrowserPreviewOwnerService();
    }

    public int ConnectedBrowserCount
    {
        get
        {
            lock (_lock)
            {
                return _listeners.Count;
            }
        }
    }

    public async Task<T> SerializeTargetOperationAsync<T>(
        string sessionId,
        string? previewName,
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        var key = $"{sessionId}::{(string.IsNullOrWhiteSpace(previewName) ? "default" : previewName.Trim())}";
        TargetOperationGate targetGate;
        lock (_lock)
        {
            if (!_targetOperationGates.TryGetValue(key, out targetGate!))
            {
                targetGate = new TargetOperationGate();
                _targetOperationGates[key] = targetGate;
            }
            targetGate.References++;
        }

        try
        {
            await targetGate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await operation().ConfigureAwait(false);
            }
            finally
            {
                targetGate.Semaphore.Release();
            }
        }
        finally
        {
            lock (_lock)
            {
                targetGate.References--;
                if (targetGate.References == 0)
                {
                    _targetOperationGates.Remove(key);
                    targetGate.Semaphore.Dispose();
                }
            }
        }
    }

    public void RegisterListener(
        string connectionId,
        string browserId,
        Action<string?, string?> detach,
        Action<string?, string?> dock,
        Action<string?, string?, int, int> viewport,
        Action<string?, string?, string, bool> open,
        Action<string?, string?, string, string?>? mobileDevice = null,
        Action<string, string, double, int>? agentWheel = null,
        Action<string?, string?>? close = null)
    {
        lock (_lock)
        {
            _previewOwnerService.RegisterUi(connectionId, browserId);
            _listeners[connectionId] = new ListenerRegistration
            {
                ConnectionId = connectionId,
                BrowserId = browserId,
                Detach = detach,
                Dock = dock,
                Viewport = viewport,
                Open = open,
                Close = close,
                MobileDevice = mobileDevice,
                AgentWheel = agentWheel,
                ConnectedAtUtc = DateTimeOffset.UtcNow
            };
        }
    }

    public void RegisterAcknowledgedListener(
        string connectionId,
        string browserId,
        Action<string, string?, string?> detach,
        Action<string, string?, string?> dock,
        Action<string, string?, string?, int, int> viewport,
        Action<string, string?, string?, string, bool, long?> open,
        Action<string?, string?, string, string?>? mobileDevice = null,
        Action<string, string, double, int>? agentWheel = null,
        Action<string, string?, string?>? close = null)
    {
        lock (_lock)
        {
            _previewOwnerService.RegisterUi(connectionId, browserId);
            _listeners[connectionId] = new ListenerRegistration
            {
                ConnectionId = connectionId,
                BrowserId = browserId,
                Detach = (_, _) => { },
                Dock = (_, _) => { },
                Viewport = (_, _, _, _) => { },
                Open = (_, _, _, _) => { },
                Close = null,
                MobileDevice = mobileDevice,
                AgentWheel = agentWheel,
                AcknowledgedDetach = detach,
                AcknowledgedDock = dock,
                AcknowledgedViewport = viewport,
                AcknowledgedOpen = open,
                AcknowledgedClose = close,
                ConnectedAtUtc = DateTimeOffset.UtcNow
            };
        }
    }

    public async Task<AgentHistoryWheelResult> RequestAgentWheelAsync(
        string sessionId,
        double deltaY,
        int steps,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new AgentHistoryWheelResult { Error = "sessionId required" };
        }

        if (!TryGetTargetListener(sessionId, "default", out var target, out var error))
            return new AgentHistoryWheelResult { SessionId = sessionId, Error = error };
        if (target.AgentWheel is null)
            return new AgentHistoryWheelResult { SessionId = sessionId, Error = "The owning tlbx UI does not support ACP wheel control. Reload it and retry." };
        var generation = _previewOwnerService.GetGeneration(sessionId, "default");
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<AgentHistoryWheelResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) _pendingAgentWheels[requestId] = completion;
        try
        {
            target.AgentWheel(requestId, sessionId, deltaY, Math.Clamp(steps, 1, 100));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ResolveAgentWheelAttemptTimeout(steps));
            var result = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (!_previewOwnerService.IsCurrent(sessionId, "default", target.BrowserId, generation))
                return new AgentHistoryWheelResult { SessionId = sessionId, Error = "Preview ownership changed while the wheel command was in flight. Inspect before retrying." };
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AgentHistoryWheelResult { SessionId = sessionId, Error = "Timed out waiting for the owning tlbx UI to complete the wheel command. Inspect before retrying." };
        }
        finally
        {
            lock (_lock) _pendingAgentWheels.Remove(requestId);
        }
    }

    private static TimeSpan ResolveAgentWheelAttemptTimeout(int steps)
    {
        var boundedSteps = Math.Clamp(steps, 1, 100);
        return TimeSpan.FromSeconds(Math.Min(10, 2 + boundedSteps * 0.06));
    }

    public bool CompleteAgentWheel(AgentHistoryWheelResult result)
    {
        TaskCompletionSource<AgentHistoryWheelResult>? completion;
        lock (_lock)
        {
            _pendingAgentWheels.TryGetValue(result.RequestId, out completion);
        }

        return completion?.TrySetResult(result) == true;
    }

    public void UnregisterListener(string connectionId)
    {
        lock (_lock)
        {
            _listeners.Remove(connectionId);
            _previewOwnerService.UnregisterUi(connectionId);
        }
    }

    public bool RequestDetach(string? sessionId, string? previewName, out string error)
    {
        error = "";
        if (!TryGetTargetListener(sessionId, previewName, out var target, out error))
        {
            return false;
        }

        target.Detach(sessionId, previewName);
        return true;
    }

    public bool RequestDock(string? sessionId, string? previewName, out string error)
    {
        error = "";
        if (!TryGetTargetListener(sessionId, previewName, out var target, out error))
        {
            return false;
        }

        target.Dock(sessionId, previewName);
        return true;
    }

    public bool RequestViewport(string? sessionId, string? previewName, int width, int height, out string error)
    {
        error = "";
        if (!TryGetTargetListener(sessionId, previewName, out var target, out error))
        {
            return false;
        }

        target.Viewport(sessionId, previewName, width, height);
        return true;
    }

    public bool RequestOpen(
        string? sessionId,
        string? previewName,
        string url,
        bool activateSession,
        out string error)
    {
        error = "";
        if (!TryGetTargetListener(sessionId, previewName, out var target, out error))
        {
            return false;
        }

        target.Open(sessionId, previewName, url, activateSession);
        return true;
    }

    public Task<BrowserUiCommandResult> RequestDetachAsync(
        string? sessionId,
        string? previewName,
        CancellationToken cancellationToken = default) =>
        RequestUiCommandAsync(
            "detach",
            sessionId,
            previewName,
            (target, requestId) =>
            {
                if (target.AcknowledgedDetach is not null)
                    target.AcknowledgedDetach(requestId, sessionId, previewName);
                else
                {
                    target.Detach(sessionId, previewName);
                    CompleteUiCommand(requestId, "detach");
                }
            },
            cancellationToken);

    public Task<BrowserUiCommandResult> RequestDockAsync(
        string? sessionId,
        string? previewName,
        CancellationToken cancellationToken = default) =>
        RequestUiCommandAsync(
            "dock",
            sessionId,
            previewName,
            (target, requestId) =>
            {
                if (target.AcknowledgedDock is not null)
                    target.AcknowledgedDock(requestId, sessionId, previewName);
                else
                {
                    target.Dock(sessionId, previewName);
                    CompleteUiCommand(requestId, "dock");
                }
            },
            cancellationToken);

    public Task<BrowserUiCommandResult> RequestViewportAsync(
        string? sessionId,
        string? previewName,
        int width,
        int height,
        CancellationToken cancellationToken = default) =>
        RequestUiCommandAsync(
            "viewport",
            sessionId,
            previewName,
            (target, requestId) =>
            {
                if (target.AcknowledgedViewport is not null)
                    target.AcknowledgedViewport(requestId, sessionId, previewName, width, height);
                else
                {
                    target.Viewport(sessionId, previewName, width, height);
                    CompleteUiCommand(requestId, "viewport");
                }
            },
            cancellationToken);

    public Task<BrowserUiCommandResult> RequestOpenAsync(
        string? sessionId,
        string? previewName,
        string url,
        bool activateSession,
        long? targetRevision,
        CancellationToken cancellationToken = default) =>
        RequestUiCommandAsync(
            "open",
            sessionId,
            previewName,
            (target, requestId) =>
            {
                if (target.AcknowledgedOpen is not null)
                    target.AcknowledgedOpen(
                        requestId,
                        sessionId,
                        previewName,
                        url,
                        activateSession,
                        targetRevision);
                else
                {
                    target.Open(sessionId, previewName, url, activateSession);
                    CompleteUiCommand(requestId, "open");
                }
            },
            cancellationToken);

    public bool CompleteUiCommand(BrowserUiCommandResult result)
    {
        if (string.IsNullOrWhiteSpace(result.RequestId))
            return false;

        TaskCompletionSource<BrowserUiCommandResult>? completion;
        lock (_lock)
        {
            _pendingUiCommands.TryGetValue(result.RequestId, out completion);
        }

        return completion?.TrySetResult(result) == true;
    }

    private void CompleteUiCommand(string requestId, string command)
    {
        CompleteUiCommand(new BrowserUiCommandResult
        {
            RequestId = requestId,
            Command = command,
            Success = true
        });
    }

    private async Task<BrowserUiCommandResult> RequestUiCommandAsync(
        string command,
        string? sessionId,
        string? previewName,
        Action<ListenerRegistration, string> dispatch,
        CancellationToken cancellationToken)
    {
        if (!TryGetTargetListener(sessionId, previewName, out var target, out var error))
            return new BrowserUiCommandResult { Command = command, Error = error };

        var generation = _previewOwnerService.GetGeneration(sessionId, previewName);
        if (!_previewOwnerService.IsCurrent(sessionId, previewName, target.BrowserId, generation))
            return new BrowserUiCommandResult { Command = command, Error = "Preview ownership changed before dispatch. Retry the command." };
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<BrowserUiCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _pendingUiCommands[requestId] = completion;
        }

        try
        {
            dispatch(target, requestId);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var result = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (!_previewOwnerService.IsCurrent(sessionId, previewName, target.BrowserId, generation))
                return new BrowserUiCommandResult { Command = command, Error = "Preview ownership changed while the UI command was in flight. Inspect the current preview before retrying." };
            return new BrowserUiCommandResult { RequestId = requestId, Command = command, Success = result.Success, Error = result.Error, OwnershipGeneration = generation };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BrowserUiCommandResult
            {
                RequestId = requestId,
                Command = command,
                Error = $"Timed out waiting for the tlbx browser UI to complete the {command} command."
            };
        }
        finally
        {
            lock (_lock)
            {
                _pendingUiCommands.Remove(requestId);
            }
        }
    }

    public int RequestClose(string sessionId, string? previewName)
    {
        ListenerRegistration[] targets;
        lock (_lock)
        {
            targets = _listeners.Values
                .Where(listener => listener.Close is not null || listener.AcknowledgedClose is not null)
                .ToArray();
        }

        foreach (var target in targets)
        {
            if (target.AcknowledgedClose is not null)
                target.AcknowledgedClose("", sessionId, previewName);
            else
                target.Close!(sessionId, previewName);
        }

        return targets.Length;
    }

    public async Task<(bool Success, string Error, long OwnershipGeneration)> RequestOpenWhenAvailableAsync(
        string? sessionId,
        string? previewName,
        string url,
        bool activateSession,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        long? targetRevision = null,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(4));
        var retryDelay = pollInterval ?? TimeSpan.FromMilliseconds(100);
        var lastError = "";

        while (true)
        {
            var result = await RequestOpenAsync(
                sessionId,
                previewName,
                url,
                activateSession,
                targetRevision,
                cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                return (true, "", result.OwnershipGeneration);
            }

            lastError = result.Error ?? "The tlbx browser UI could not complete the open command.";
            if (ConnectedBrowserCount > 0)
            {
                return (false, lastError, 0);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return (false, lastError, 0);
            }

            await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    public bool RequestMobileDevice(
        string? sessionId,
        string? previewName,
        string action,
        string? profile,
        out string error)
    {
        error = "";
        if (!IsSupportedMobileDeviceAction(action))
        {
            error = "Unsupported mobile device action. Use status, open, rotate, keyboard, background, foreground, reload, screenshot, or close.";
            return false;
        }

        if (!TryGetTargetListener(sessionId, previewName, out var target, out error))
        {
            return false;
        }

        if (target.MobileDevice is null)
        {
            error = "The connected tlbx browser UI does not support mobile device control. Reload it and retry.";
            return false;
        }

        target.MobileDevice(sessionId, previewName, action.Trim().ToLowerInvariant(), profile);
        return true;
    }

    private static bool IsSupportedMobileDeviceAction(string action)
    {
        return !string.IsNullOrWhiteSpace(action) && action.Trim().ToLowerInvariant() is
            "status" or "open" or "rotate" or "keyboard" or "background" or "foreground" or
            "reload" or "screenshot" or "close";
    }

    public string[] GetConnectedBrowserIds() => _previewOwnerService.GetConnectedBrowserIds();

    public bool RequestClaim(string? sessionId, string? previewName, out string error, string? browserId = null)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            error = "sessionId required";
            return false;
        }
        var candidates = GetConnectedBrowserIds();
        var selected = string.IsNullOrWhiteSpace(browserId)
            ? candidates.Length == 1 ? candidates[0] : null
            : candidates.FirstOrDefault(id => string.Equals(id, browserId.Trim(), StringComparison.Ordinal));
        if (selected is null)
        {
            error = candidates.Length == 0 ? "No tlbx browser UI is connected. Open a tlbx tab and retry."
                : browserId is not null ? $"No connected tlbx browser UI matches exact tab '{browserId}'. List /api/browser/ui-clients."
                : BrowserPreviewOwnerService.UnavailableMessage(sessionId, previewName, null);
            return false;
        }
        _previewOwnerService.Claim(sessionId, previewName, selected);
        return true;
    }

    public bool RequestClaimMain(string? browserId, out string claimedBrowserId, out string error)
    {
        claimedBrowserId = "";
        error = "";

        ListenerRegistration[] listeners;
        lock (_lock)
        {
            if (_listeners.Count == 0)
            {
                error = "No tlbx browser UI is connected. Open the owning tlbx browser tab first; the preview target alone cannot drive /ws/state.";
                return false;
            }

            listeners = _listeners.Values.ToArray();
        }

        ListenerRegistration? target;
        if (string.IsNullOrWhiteSpace(browserId))
        {
            target = SelectClaimListener(listeners);
        }
        else
        {
            target = SelectListenerByBrowserId(listeners, browserId.Trim());
        }

        if (target is null || string.IsNullOrWhiteSpace(target.BrowserId))
        {
            error = string.IsNullOrWhiteSpace(browserId)
                ? "Multiple tlbx browser UIs are connected. Pass --browser with the intended browser id to claim the leading browser deterministically."
                : $"No connected tlbx browser UI matches '{browserId}'.";
            return false;
        }

        _mainBrowserService.Claim(target.BrowserId);
        claimedBrowserId = target.BrowserId;
        return true;
    }

    private bool TryGetTargetListener(
        string? sessionId,
        string? previewName,
        out ListenerRegistration target,
        out string error)
    {
        ListenerRegistration[] listeners;
        lock (_lock)
        {
            if (_listeners.Count == 0)
            {
                error = "No tlbx browser UI is connected. Open the owning tlbx browser tab first; the preview target alone cannot drive /ws/state.";
                target = null!;
                return false;
            }

            listeners = _listeners.Values.ToArray();
        }

        var owner = _previewOwnerService.ResolveOwnerBrowserId(sessionId, previewName);
        // Unscoped operations are safe only when there is a single exact UI tab.
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            var ids = GetConnectedBrowserIds();
            owner = ids.Length == 1 ? ids[0] : null;
        }
        var selected = owner is null ? null : SelectListenerByBrowserId(listeners, owner);
        target = selected!;
        error = selected is null
            ? BrowserPreviewOwnerService.UnavailableMessage(sessionId, previewName, owner) : "";
        return selected is not null;
    }

    private ListenerRegistration? SelectClaimListener(ListenerRegistration[] listeners)
    {
        if (listeners.Length == 0)
        {
            return null;
        }

        var candidates = listeners.AsEnumerable();
        var mainBrowserId = _mainBrowserService.GetMainBrowserId();
        if (!string.IsNullOrWhiteSpace(mainBrowserId))
        {
            var mainCandidates = candidates
                .Where(listener => BrowserIdentity.AreSameBrowser(listener.BrowserId, mainBrowserId))
                .ToArray();
            if (mainCandidates.Length > 0)
            {
                candidates = mainCandidates;
            }
        }

        var candidateArray = candidates.ToArray();
        var distinctBrowserClients = candidateArray
            .Select(listener => listener.BrowserId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Select(BrowserIdentity.GetClientPart)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctBrowserClients.Length > 1)
        {
            return null;
        }

        return candidateArray
            .OrderByDescending(listener => listener.ConnectedAtUtc)
            .FirstOrDefault();
    }

    private static ListenerRegistration? SelectListenerByBrowserId(
        ListenerRegistration[] listeners,
        string browserId)
    {
        var exact = listeners
            .Where(listener => string.Equals(listener.BrowserId, browserId, StringComparison.Ordinal))
            .OrderByDescending(listener => listener.ConnectedAtUtc)
            .FirstOrDefault();
        if (exact is not null)
        {
            return exact;
        }

        return null;
    }

    private sealed class ListenerRegistration
    {
        public string ConnectionId { get; init; } = "";
        public string BrowserId { get; init; } = "";
        public required Action<string?, string?> Detach { get; init; }
        public required Action<string?, string?> Dock { get; init; }
        public required Action<string?, string?, int, int> Viewport { get; init; }
        public required Action<string?, string?, string, bool> Open { get; init; }
        public Action<string?, string?>? Close { get; init; }
        public Action<string, string?, string?>? AcknowledgedDetach { get; init; }
        public Action<string, string?, string?>? AcknowledgedDock { get; init; }
        public Action<string, string?, string?, int, int>? AcknowledgedViewport { get; init; }
        public Action<string, string?, string?, string, bool, long?>? AcknowledgedOpen { get; init; }
        public Action<string, string?, string?>? AcknowledgedClose { get; init; }
        public Action<string?, string?, string, string?>? MobileDevice { get; init; }
        public Action<string, string, double, int>? AgentWheel { get; init; }
        public DateTimeOffset ConnectedAtUtc { get; init; }
    }

    private sealed class TargetOperationGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References { get; set; }
    }
}
