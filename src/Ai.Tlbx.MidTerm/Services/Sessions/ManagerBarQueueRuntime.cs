using System.Text;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Services;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public interface IManagerBarQueueRuntime
{
    IReadOnlyCollection<string> GetActiveSessionIds();
    bool SessionExists(string sessionId);
    SessionHeatSnapshot GetHeatSnapshot(string sessionId);
    bool UsesTurnQueue(string sessionId);
    bool IsTurnQueueReady(string sessionId);
    Task SendPromptAsync(string sessionId, string prompt, CancellationToken cancellationToken);
    Task SendTurnAsync(string sessionId, AppServerControlTurnRequest request, CancellationToken cancellationToken);
}

public sealed class ManagerBarQueueRuntime : IManagerBarQueueRuntime
{
    private const int SessionTextSubmitDelayMs = 200;

    private readonly TtyHostSessionManager _sessionManager;
    private readonly SessionHeatService _sessionHeat;
    private readonly SessionTelemetryService _sessionTelemetry;
    private readonly SessionAppServerControlRuntimeService _appServerControlRuntime;
    private readonly ClipboardService _clipboardService;

    public ManagerBarQueueRuntime(
        TtyHostSessionManager sessionManager,
        SessionHeatService sessionHeat,
        SessionTelemetryService sessionTelemetry,
        SessionAppServerControlRuntimeService appServerControlRuntime,
        ClipboardService clipboardService)
    {
        _sessionManager = sessionManager;
        _sessionHeat = sessionHeat;
        _sessionTelemetry = sessionTelemetry;
        _appServerControlRuntime = appServerControlRuntime;
        _clipboardService = clipboardService;
    }

    public IReadOnlyCollection<string> GetActiveSessionIds()
    {
        return _sessionManager.GetAllSessions()
            .Select(static session => session.Id)
            .ToArray();
    }

    public bool SessionExists(string sessionId)
    {
        return _sessionManager.GetSession(sessionId) is not null;
    }

    public SessionHeatSnapshot GetHeatSnapshot(string sessionId)
    {
        return _sessionHeat.GetSnapshot(sessionId);
    }

    public bool UsesTurnQueue(string sessionId)
    {
        return _sessionManager.GetSessionList().Sessions
            .FirstOrDefault(candidate => string.Equals(candidate.Id, sessionId, StringComparison.Ordinal))
            ?.AppServerControlOnly == true;
    }

    public bool IsTurnQueueReady(string sessionId)
    {
        if (!UsesTurnQueue(sessionId))
        {
            return false;
        }

        if (!_appServerControlRuntime.TryGetCachedHistoryWindow(sessionId, out var historyWindow))
        {
            return false;
        }

        if (historyWindow.Requests.Any(static request =>
                string.Equals(request.State, "open", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var turnState = historyWindow.CurrentTurn.State?.Trim().ToLowerInvariant();
        if (turnState is "running" or "in_progress" or "started" or "submitted")
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(turnState))
        {
            return true;
        }

        var sessionState = historyWindow.Session.State?.Trim().ToLowerInvariant();
        return sessionState is not "starting" and not "running";
    }

    public async Task SendPromptAsync(string sessionId, string prompt, CancellationToken cancellationToken)
    {
        var session = _sessionManager.GetSession(sessionId);
        if (session is null)
        {
            return;
        }

        var appServerControlOnly = _sessionManager.GetSessionList().Sessions
            .FirstOrDefault(candidate => string.Equals(candidate.Id, sessionId, StringComparison.Ordinal))
            ?.AppServerControlOnly == true;

        if (appServerControlOnly)
        {
            var sent = await _appServerControlRuntime.TrySendPromptAsync(
                sessionId,
                new SessionPromptRequest { Text = prompt },
                cancellationToken).ConfigureAwait(false);
            if (sent)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Agent Controller session {sessionId} is not attached; refusing PTY fallback.");
        }

        var promptBytes = Encoding.UTF8.GetBytes(prompt);
        _sessionTelemetry.RecordInput(sessionId, promptBytes.Length);
        await _sessionManager.SendInputAsync(sessionId, promptBytes, cancellationToken).ConfigureAwait(false);

        await Task.Delay(SessionTextSubmitDelayMs, cancellationToken).ConfigureAwait(false);

        var submitBytes = new byte[] { (byte)'\r' };
        _sessionTelemetry.RecordInput(sessionId, submitBytes.Length);
        await _sessionManager.SendInputAsync(sessionId, submitBytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendTurnAsync(string sessionId, AppServerControlTurnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!UsesTurnQueue(sessionId))
        {
            if (request.TerminalReplay is { Count: > 0 })
            {
                var session = _sessionManager.GetSession(sessionId);
                if (session is null)
                {
                    return;
                }

                await TerminalReplayExecutor.ExecuteAsync(
                    request.TerminalReplay,
                    (data, ct) => SendInputAndRecordAsync(sessionId, data, ct),
                    (path, mimeType, ct) => TryPasteTerminalReplayImageAsync(sessionId, session, path, mimeType, ct),
                    static (delayMs, ct) => Task.Delay(delayMs, ct),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!string.IsNullOrWhiteSpace(request.Text))
            {
                await SendPromptAsync(sessionId, request.Text, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await _appServerControlRuntime.SendComposerTurnAsync(sessionId, request, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendInputAndRecordAsync(
        string sessionId,
        byte[] data,
        CancellationToken cancellationToken)
    {
        _sessionTelemetry.RecordInput(sessionId, data.Length);
        await _sessionManager.SendInputAsync(sessionId, data, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryPasteTerminalReplayImageAsync(
        string sessionId,
        SessionInfo session,
        string path,
        string? mimeType,
        CancellationToken cancellationToken)
    {
        var success = await SessionApiEndpoints.TrySetClipboardImageAsync(
            token => _sessionManager.SetClipboardImageAsync(sessionId, path, mimeType, token),
            _ => _clipboardService.SetImageAsync(
                path,
                mimeType,
                SessionApiEndpoints.GetPreferredClipboardProcessId(session)),
            cancellationToken).ConfigureAwait(false);

        if (!success)
        {
            return false;
        }

        await SendInputAndRecordAsync(sessionId, [0x1b, 0x76], cancellationToken).ConfigureAwait(false);
        return true;
    }
}
