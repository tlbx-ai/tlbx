using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using System.Security.Cryptography;

namespace Ai.Tlbx.MidTerm.Services.Browser;

/// <summary>One ownership/readiness workflow shared by REST, CLI-backed REST and MCP.</summary>
public sealed class BrowserPreviewOperations(
    BrowserCommandService commands,
    WebPreviewService previews,
    BrowserUiBridge ui,
    BrowserPreviewRegistry registry,
    BrowserPreviewOwnerService owners)
{
    private sealed class ScopeState(string owner, BrowserPreviewScope scope, string routeKey) : IDisposable
    {
        public string Owner { get; } = owner;
        public BrowserPreviewScope Scope { get; } = scope;
        public string RouteKey { get; } = routeKey;
        public CancellationTokenSource Lifetime { get; } = new(TimeSpan.FromMinutes(11));
        public TaskCompletionSource<bool> Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Closing { get; set; }
        public void Dispose() => Lifetime.Dispose();
    }
    private readonly object _scopeGate = new();
    private readonly Dictionary<string, ScopeState> _scopes = new(StringComparer.Ordinal);

    /// <summary>Fresh-name reservation shared by HTTP and native callback workflows.</summary>
    public async Task<BrowserPreviewScope> OpenScopeAsync(string owner, string sessionId, string? requestedName, string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("An authenticated scope owner is required.");
        var name = requestedName is null ? "mcp-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))
            : WebPreviewService.NormalizePreviewName(requestedName);
        var state = await ui.SerializeTargetOperationAsync(sessionId, name, () =>
        {
            lock (_scopeGate)
            {
            if (string.Equals(name, WebPreviewService.DefaultPreviewName, StringComparison.OrdinalIgnoreCase)
                || previews.GetPreviewSession(sessionId, name) is not null
                || _scopes.Values.Any(item => item.Scope.SessionId == sessionId && string.Equals(item.Scope.PreviewName, name, StringComparison.Ordinal)))
                throw new ArgumentException("An ephemeral preview requires a fresh non-default name; the name is already present or reserved.");
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var reserved = new ScopeState(owner, new BrowserPreviewScope(token, sessionId, name), previews.EnsurePreviewSession(sessionId, name).RouteKey);
            _scopes.Add(token, reserved);
            return Task.FromResult(reserved);
            }
        }, cancellationToken).ConfigureAwait(false);
        _ = ExpireScopeAsync(state);
        try
        {
            var opened = await OpenAsync(sessionId, name, url, false, cancellationToken).ConfigureAwait(false);
            if (!opened.Success) throw new InvalidOperationException(opened.Text);
            return state.Scope;
        }
        catch
        {
            try { await CloseScopeAsync(owner, state.Scope.Token).ConfigureAwait(false); }
            catch (Exception ex) { BrowserLog.Error("Ephemeral preview cleanup failed: " + ex.Message); }
            throw;
        }
    }

    public Task CloseScopeAsync(string owner, string token)
    {
        ScopeState state;
        lock (_scopeGate)
        {
            if (!_scopes.TryGetValue(token, out state!)) return Task.CompletedTask;
            if (!string.Equals(owner, state.Owner, StringComparison.Ordinal)) throw new ArgumentException("Preview scope is unavailable for this caller.");
            if (state.Closing) return state.Closed.Task;
            state.Closing = true;
        }
        _ = CloseScopeCoreAsync(state);
        return state.Closed.Task;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "IDISP007:Don't dispose injected", Justification = "OpenScopeAsync allocates this private ScopeState and transfers its lifetime exclusively to this service's cleanup method.")]
    private async Task CloseScopeCoreAsync(ScopeState state)
    {
        Exception? failure = null;
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = await CloseCoreAsync(state.Scope.SessionId, state.Scope.PreviewName, state.RouteKey, cleanup.Token).ConfigureAwait(false);
            if (!result.Success) throw new InvalidOperationException(result.Text);
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            lock (_scopeGate) _scopes.Remove(state.Scope.Token);
            await state.Lifetime.CancelAsync().ConfigureAwait(false);
            state.Dispose();
        }
        if (failure is null) state.Closed.TrySetResult(true);
        else state.Closed.TrySetException(failure);
    }

    private async Task ExpireScopeAsync(ScopeState state)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, state.Lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_scopeGate) { if (state.Closing) return; }
            try { await CloseScopeAsync(state.Owner, state.Scope.Token).ConfigureAwait(false); }
            catch (Exception ex) { BrowserLog.Error("Expired preview cleanup failed: " + ex.Message); }
        }
    }

    public async Task<T> WithPreviewScopeAsync<T>(string owner, string sessionId, string? name, string url,
        Func<BrowserPreviewScope, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
    {
        var scope = await OpenScopeAsync(owner, sessionId, name, url, cancellationToken).ConfigureAwait(false);
        var completed = false;
        T result = default!;
        Exception? cleanupFailure = null;
        try { result = await body(scope, cancellationToken).ConfigureAwait(false); completed = true; }
        finally
        {
            try { await CloseScopeAsync(owner, scope.Token).ConfigureAwait(false); }
            catch (Exception ex) when (!completed) { BrowserLog.Error("Ephemeral preview cleanup failed: " + ex.Message); }
            catch (Exception ex) { cleanupFailure = ex; }
        }
        if (cleanupFailure is not null) throw new InvalidOperationException("Workflow steps completed but preview cleanup failed. Inspect state before retrying.", cleanupFailure);
        return result;
    }
    public Task<BrowserPreviewOperationResult> OpenAsync(
        string sessionId, string? previewName, string url, bool activateSession = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Task.FromResult(new BrowserPreviewOperationResult(false, 400, "sessionId required"));

        return ui.SerializeTargetOperationAsync(sessionId, previewName, async () =>
        {
            var started = DateTimeOffset.UtcNow;
            if (!previews.SetTarget(sessionId, previewName, url))
                return new BrowserPreviewOperationResult(false, 400,
                    "Invalid URL. Must be http://, https://, or a local file:/// URL, and cannot point to this server.");
            var revision = previews.GetPreviewSession(sessionId, previewName)?.TargetRevision;
            owners.ResolveOwnerBrowserId(sessionId, previewName);
            var opened = await ui.RequestOpenWhenAvailableAsync(sessionId, previewName, url, activateSession,
                targetRevision: revision, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!opened.Success)
                return new BrowserPreviewOperationResult(false, 409, opened.Error + "\n");

            var status = await commands.WaitForControllableAsync(url, sessionId, previewName,
                requireClientConnectedAfterUtc: started, requireVisibleClient: activateSession,
                connectedUiClientCountProvider: () => ui.ConnectedBrowserCount,
                cancellationToken: cancellationToken, requiredTargetRevision: revision).ConfigureAwait(false);
            var text = commands.GetStatusText(url, sessionId, previewName, connectedUiClientCount: ui.ConnectedBrowserCount);
            var ready = status.Controllable && owners.GetGeneration(sessionId, previewName) == opened.OwnershipGeneration
                && (!activateSession || status.DefaultClient?.IsVisible == true);
            if (ready)
            {
                var probe = await commands.ExecuteCommandAsync(new BrowserCommandRequest
                {
                    Command = "ready", SessionId = sessionId, PreviewName = previewName,
                    PreviewId = status.DefaultClient?.PreviewId, Timeout = 15
                }, cancellationToken).ConfigureAwait(false);
                if (!probe.Success) return new BrowserPreviewOperationResult(false, 409, probe.Error + "\n");
                if (owners.GetGeneration(sessionId, previewName) != opened.OwnershipGeneration
                    || previews.GetPreviewSession(sessionId, previewName)?.TargetRevision != revision)
                    return new BrowserPreviewOperationResult(false, 409,
                        "The preview target changed before open completed. Retry mt_open.\n");
            }
            return ready
                ? new BrowserPreviewOperationResult(true, 200, text)
                : new BrowserPreviewOperationResult(false, 409,
                    "Open failed: " + (status.StatusMessage ?? "Preview ownership or visibility changed before readiness.") + "\n" + text);
        }, cancellationToken);
    }

    public Task<BrowserPreviewOperationResult> CloseAsync(
        string sessionId, string? previewName, CancellationToken cancellationToken = default)
        => CloseCoreAsync(sessionId, previewName, null, cancellationToken);

    private Task<BrowserPreviewOperationResult> CloseCoreAsync(string sessionId, string? previewName,
        string? expectedRouteKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Task.FromResult(new BrowserPreviewOperationResult(false, 400, "sessionId required"));
        var name = WebPreviewService.NormalizePreviewName(previewName);
        return ui.SerializeTargetOperationAsync(sessionId, name, () =>
        {
            if (expectedRouteKey is not null && !string.Equals(previews.GetPreviewSession(sessionId, name)?.RouteKey, expectedRouteKey, StringComparison.Ordinal))
                return Task.FromResult(new BrowserPreviewOperationResult(true, 200, null));
            if (string.Equals(name, WebPreviewService.DefaultPreviewName, StringComparison.OrdinalIgnoreCase))
                previews.ClearTarget(sessionId, name);
            else
            {
                previews.DeletePreviewSession(sessionId, name);
                owners.Release(sessionId, name);
            }
            registry.Remove(sessionId, name);
            ui.RequestClose(sessionId, name);
            return Task.FromResult(new BrowserPreviewOperationResult(true, 200, null));
        }, cancellationToken);
    }

    /// <summary>Shares cleanup semantics with any bounded cross-tool workflow, not only browser batches.</summary>
    public async Task<T> WithPreviewAsync<T>(string sessionId, string previewName, string url,
        Func<CancellationToken, Task<T>> body, CancellationToken cancellationToken = default)
    {
        return await WithPreviewScopeAsync("internal:" + Guid.NewGuid().ToString("N"), sessionId, previewName, url,
            (_, ct) => body(ct), cancellationToken).ConfigureAwait(false);
    }
}

public sealed record BrowserPreviewScope(string Token, string SessionId, string PreviewName);

public sealed record BrowserPreviewOperationResult(bool Success, int StatusCode, string? Text)
{
    public IResult ToHttpResult() => Text is null && Success
        ? Results.Ok()
        : Results.Text(Text, statusCode: StatusCode);
}
