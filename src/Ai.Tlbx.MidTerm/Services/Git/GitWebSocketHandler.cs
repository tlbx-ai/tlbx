using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Models.Git;
using Ai.Tlbx.MidTerm.Settings;

using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Services.WebSockets;
namespace Ai.Tlbx.MidTerm.Services.Git;

public sealed class GitWebSocketHandler
{
    private readonly GitWatcherService _gitWatcher;
    private readonly SettingsService _settingsService;
    private readonly AuthService _authService;
    private readonly ShutdownService _shutdownService;
    private readonly TtyHostSessionManager _sessionManager;

    public GitWebSocketHandler(
        GitWatcherService gitWatcher,
        SettingsService settingsService,
        AuthService authService,
        ShutdownService shutdownService,
        TtyHostSessionManager sessionManager)
    {
        _gitWatcher = gitWatcher;
        _settingsService = settingsService;
        _authService = authService;
        _shutdownService = shutdownService;
        _sessionManager = sessionManager;
    }

    public async Task HandleAsync(HttpContext context)
    {
        var authentication = _authService.AuthenticateRequestWithContext(context.Request);
        if (authentication.Method == RequestAuthMethod.None)
        {
            context.Response.StatusCode = 401;
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        using var authLease = _authService.TrackWebSocketAuthentication(authentication, ws);
        var sendLock = new SemaphoreSlim(1, 1);
        var subscribedSessions = new HashSet<string>(StringComparer.Ordinal);
        var shutdownToken = _shutdownService.Token;

        async Task SendMessageAsync(GitWsMessage message)
        {
            if (ws.State != WebSocketState.Open) return;

            await sendLock.WaitAsync(shutdownToken);
            try
            {
                if (ws.State != WebSocketState.Open) return;

                var bytes = JsonSerializer.SerializeToUtf8Bytes(message, GitJsonContext.Default.GitWsMessage);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, shutdownToken);
            }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Verbose(() => $"[GitWS] SendMessageAsync failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                sendLock.Release();
            }
        }

        void OnStatusChanged(string repoRoot, GitStatusResponse status)
        {
            lock (subscribedSessions)
            {
                foreach (var sessionId in subscribedSessions)
                {
                    if (_gitWatcher.SessionHasRepo(sessionId, repoRoot))
                    {
                        _ = SendMessageAsync(new GitWsMessage
                        {
                            Type = "status",
                            SessionId = sessionId,
                            Status = status
                        });
                    }
                }
            }
        }

        void OnReposChanged(string sessionId)
        {
            lock (subscribedSessions)
            {
                if (!subscribedSessions.Contains(sessionId))
                {
                    return;
                }
            }

            _ = SendMessageAsync(new GitWsMessage
            {
                Type = "repos",
                SessionId = sessionId,
                Repos = _gitWatcher.GetRepoBindings(sessionId)
            });
        }

        _gitWatcher.OnStatusChanged += OnStatusChanged;
        _gitWatcher.OnReposChanged += OnReposChanged;

        try
        {
            var buffer = new byte[4096];
            var messageBuffer = new List<byte>();

            while (ws.State == WebSocketState.Open && !shutdownToken.IsCancellationRequested)
            {
                try
                {
                    var result = await ws.ReceiveAsync(buffer, shutdownToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        messageBuffer.AddRange(buffer.AsSpan(0, result.Count).ToArray());

                        if (result.EndOfMessage)
                        {
                            var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                            messageBuffer.Clear();

                            await HandleClientMessageAsync(json, subscribedSessions, SendMessageAsync);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    break;
                }
            }
        }
        finally
        {
            _gitWatcher.OnStatusChanged -= OnStatusChanged;
            _gitWatcher.OnReposChanged -= OnReposChanged;

            lock (subscribedSessions)
            {
                foreach (var sid in subscribedSessions)
                {
                    _gitWatcher.Unsubscribe(sid);
                }
                subscribedSessions.Clear();
            }

            sendLock.Dispose();

            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var closeCode = shutdownToken.IsCancellationRequested
                        ? (WebSocketCloseStatus)MuxProtocol.CloseServerShutdown
                        : WebSocketCloseStatus.NormalClosure;
                    var closeMessage = shutdownToken.IsCancellationRequested
                        ? "Server shutting down"
                        : null;
                    await ws.CloseAsync(closeCode, closeMessage, cts.Token);
                }
                catch
                {
                }
            }
        }
    }

    private async Task HandleClientMessageAsync(
        string json,
        HashSet<string> subscribedSessions,
        Func<GitWsMessage, Task> sendMessage)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
            var sessionId = root.TryGetProperty("sessionId", out var sidProp) ? sidProp.GetString() : null;

            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(sessionId)) return;

            switch (type)
            {
                case "subscribe":
                    bool isNew;
                    lock (subscribedSessions)
                    {
                        isNew = subscribedSessions.Add(sessionId);
                    }

                    if (_gitWatcher.GetRepoRoot(sessionId) is null)
                    {
                        var session = _sessionManager.GetSession(sessionId);
                        if (session is not null && !string.IsNullOrEmpty(session.CurrentDirectory))
                        {
                            await _gitWatcher.RegisterSessionAsync(sessionId, session.CurrentDirectory);
                        }
                    }

                    await sendMessage(new GitWsMessage
                    {
                        Type = "repos",
                        SessionId = sessionId,
                        Repos = _gitWatcher.GetRepoBindings(sessionId)
                    });

                    if (isNew)
                    {
                        _gitWatcher.Subscribe(sessionId);
                    }

                    foreach (var repo in _gitWatcher.GetRepoBindings(sessionId))
                    {
                        _ = _gitWatcher.RefreshStatusAsync(repo.RepoRoot);
                    }
                    break;

                case "unsubscribe":
                    bool wasPresent;
                    lock (subscribedSessions)
                    {
                        wasPresent = subscribedSessions.Remove(sessionId);
                    }
                    if (wasPresent)
                    {
                        _gitWatcher.Unsubscribe(sessionId);
                    }
                    break;
            }
        }
        catch (JsonException ex)
        {
            Log.Verbose(() => $"[GitWS] Failed to parse client message: {ex.Message}");
        }
    }
}
