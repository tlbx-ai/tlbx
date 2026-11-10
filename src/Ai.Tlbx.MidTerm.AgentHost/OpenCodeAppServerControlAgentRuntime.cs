using System.Globalization;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

/// <summary>
/// OpenCode's native HTTP/SSE protocol. Its ACP adapter does not forward question
/// tools, so using ACP here would silently lose part of the conversation.
/// </summary>
internal sealed partial class OpenCodeAppServerControlAgentRuntime : IAppServerControlAgentRuntime
{
    private readonly Action<AppServerControlProviderEvent> _emit;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly object _state = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HttpClient _http;
    private readonly bool _externalTransport;
    private readonly Dictionary<string, JsonElement> _models = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _parts = new(StringComparer.Ordinal);
    private readonly Dictionary<(string MessageId, string Url), AppServerControlAttachmentReference> _submittedAttachments = [];
    private readonly Dictionary<string, string> _messageTurns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingOpenCodeRequest> _requests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _nativeSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _childTurns = new(StringComparer.Ordinal);
    private readonly List<AppServerControlCommand> _availableCommands = [];
    private readonly List<string> _agents = [];
    private readonly List<JsonElement> _bufferedEvents = [];
    private AppServerControlQuickSettingsSummary _settings = new();
    private AppServerControlAttachRuntimeRequest? _attachment;
    private string _sessionId = string.Empty;
    private string _threadId = string.Empty;
    private string? _turnId;
    private string? _userMessageId;
    private string? _error;
    private string _agent = "build";
    private long _sequence;
    private bool _ready;
    private bool _busy;
    private bool _assistantSeen;
    private bool _userReceipt;
    private bool _interrupting;
    private bool _disposed;
    private TaskCompletionSource? _turnCompletion;
    private Task? _eventsTask;
    private readonly List<Task> _operations = [];

    public OpenCodeAppServerControlAgentRuntime(Action<AppServerControlProviderEvent> emit, HttpMessageHandler? transport = null, Uri? transportAddress = null)
    {
        _emit = emit;
        _externalTransport = transport is not null;
        // A runtime owns one server and its private Basic credentials. Sharing the
        // client across runtimes would leak authorization between local servers.
#pragma warning disable IDISP014
        _http = transport is null ? new HttpClient() : new HttpClient(transport, disposeHandler: true);
#pragma warning restore IDISP014
        _http.Timeout = Timeout.InfiniteTimeSpan;
        if (transport is not null) _http.BaseAddress = transportAddress ?? new Uri("http://127.0.0.1:22202");
    }

    public string Provider => "opencode";

    public async Task<HostCommandOutcome> ExecuteAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        await _commands.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (command.Type == "runtime.attach") return await AttachAsync(command, ct).ConfigureAwait(false);
            if (!_ready || command.SessionId != _sessionId) throw new InvalidOperationException("OpenCode is not attached to this session.");
            switch (command.Type)
            {
                case "turn.start":
                    return await StartAsync(command, command.StartTurn ?? throw new InvalidOperationException("turn.start payload is required."), ct).ConfigureAwait(false);
                case "turn.interrupt":
                    var interrupted = await InterruptAsync(command.InterruptTurn?.TurnId, ct).ConfigureAwait(false);
                    return Accepted(command, interrupted);
                case "turn.steer":
                    var steer = command.SteerTurn ?? throw new InvalidOperationException("turn.steer payload is required.");
                    if (string.IsNullOrWhiteSpace(steer.Text) && steer.Attachments.Count == 0) throw new InvalidOperationException("Steering requires text or attachments.");
                    if (string.IsNullOrWhiteSpace(steer.ExpectedTurnId)) throw new InvalidOperationException("OpenCode steering requires the current turn ID.");
                    await InterruptAsync(steer.ExpectedTurnId, ct).ConfigureAwait(false);
                    return await StartAsync(command, new AppServerControlTurnRequest
                    {
                        Text = steer.Text,
                        Attachments = steer.Attachments,
                        Model = _settings.Model,
                        Effort = _settings.Effort,
                        PlanMode = _settings.PlanMode,
                        PermissionMode = _settings.PermissionMode
                    }, ct).ConfigureAwait(false);
                case "thread.compact":
                    return await CompactAsync(command, ct).ConfigureAwait(false);
                case "request.resolve":
                case "user-input.resolve":
                    return await ResolveAsync(command, ct).ConfigureAwait(false);
                default:
                    throw new InvalidOperationException($"OpenCode does not support '{command.Type}'.");
            }
        }
        finally { _commands.Release(); }
    }

    private async Task<HostCommandOutcome> AttachAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        var attach = command.AttachRuntime ?? throw new InvalidOperationException("runtime.attach payload is required.");
        if (attach.Provider != Provider || !Directory.Exists(attach.WorkingDirectory)) throw new InvalidOperationException("OpenCode requires an existing working directory.");
        if (_attachment is not null)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (_sessionId != command.SessionId || !string.Equals(Path.GetFullPath(attach.WorkingDirectory), Path.GetFullPath(_attachment.WorkingDirectory), comparison) ||
                attach.ExecutablePath != _attachment.ExecutablePath || !attach.ExecutableArguments.SequenceEqual(_attachment.ExecutableArguments, StringComparer.Ordinal) ||
                !attach.EnvironmentVariables.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(_attachment.EnvironmentVariables.OrderBy(p => p.Key, StringComparer.Ordinal)) ||
                (!string.IsNullOrEmpty(attach.ResumeThreadId) && attach.ResumeThreadId != _threadId))
                throw new InvalidOperationException("OpenCode already owns a different attachment; its conversation was preserved.");
            if (!_ready) throw new InvalidOperationException("OpenCode attachment is not ready.");
            return Accepted(command);
        }

        _sessionId = command.SessionId;
        _attachment = attach;
        try
        {
            if (!_externalTransport) await StartServerAsync(attach, ct).ConfigureAwait(false);
            var health = await SendAsync(HttpMethod.Get, "/global/health", null, ct).ConfigureAwait(false);
            var version = Text(health, "version")?.Split('-')[0];
            if (Value(health, "healthy").ValueKind != JsonValueKind.True || !Version.TryParse(version, out var parsed) || parsed < new Version(1, 14, 19))
                throw new InvalidOperationException("OpenCode HTTP control requires a healthy OpenCode 1.14.19 or newer. Update the OpenCode CLI.");

            await LoadCatalogAsync(ct).ConfigureAwait(false);
            // Subscribe before opening/loading the native session. Notifications are
            // buffered until the authoritative history and metadata are published.
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _eventsTask = ReadEventsAsync(connected, _shutdown.Token);
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(attach.ResumeThreadId))
            {
                var native = await SendAsync(HttpMethod.Get, "/session/" + Uri.EscapeDataString(attach.ResumeThreadId), null, ct).ConfigureAwait(false);
                _threadId = Text(native, "id") ?? throw new InvalidOperationException("OpenCode did not return the requested native session.");
                if (_threadId != attach.ResumeThreadId) throw new InvalidOperationException("OpenCode returned a different native session.");
                var directory = Text(native, "directory");
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!string.IsNullOrEmpty(directory) && !string.Equals(Path.GetFullPath(directory), Path.GetFullPath(attach.WorkingDirectory), comparison))
                    throw new InvalidOperationException("OpenCode native session belongs to another directory.");
            }
            else
            {
                var native = await SendAsync(HttpMethod.Post, "/session", w => { w.WriteStartObject(); WritePermissions(w); w.WriteEndObject(); }, ct).ConfigureAwait(false);
                _threadId = Text(native, "id") ?? throw new InvalidOperationException("OpenCode did not create a native session.");
            }
            _nativeSessions.Add(_threadId);
            await LoadChildSessionsAsync(_threadId, ct).ConfigureAwait(false);
            lock (_state)
            {
                Publish("agent.metadata.updated", e => e.AgentMetadataUpdated = new() { AgentName = "OpenCode", AvailableCommands = AppServerControlCommand.CloneMany(_availableCommands) });
                PublishSettings();
                Publish("thread.ready", e => e.ThreadState = new() { State = "ready", StateLabel = "Ready", ProviderThreadId = _threadId });
            }
            await RefreshHistoryAsync(replay: true, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(attach.ResumeThreadId))
            {
                lock (_state)
                {
                    var last = _messages.Values.Where(m => Text(m, "role") == "assistant")
                        .OrderBy(m => Number(Value(m, "time", "created")) ?? 0).LastOrDefault();
                    var model = Text(last, "providerID") + "/" + Text(last, "modelID");
                    if (_models.TryGetValue(model, out var definition))
                    {
                        _settings.Model = model;
                        _settings.EffortOptions = Variants(definition);
                        var variant = Text(last, "variant");
                        _settings.Effort = _settings.EffortOptions.Any(v => v.Value == variant) ? variant : null;
                        PublishSettings();
                    }
                }
            }
            var permissions = await SendAsync(HttpMethod.Get, "/permission", null, ct).ConfigureAwait(false);
            var questions = await SendAsync(HttpMethod.Get, "/question", null, ct).ConfigureAwait(false);
            lock (_state)
            {
                PublishSession("ready", "Ready");
                foreach (var request in Array(permissions)) OpenRequest(request, question: false);
                foreach (var request in Array(questions)) OpenRequest(request, question: true);
                _ready = true;
                foreach (var buffered in _bufferedEvents) ProjectEvent(buffered);
                _bufferedEvents.Clear();
            }
            return Accepted(command);
        }
        catch
        {
            await StopServerAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<HostCommandOutcome> StartAsync(AppServerControlHostCommandEnvelope command, AppServerControlTurnRequest request, CancellationToken ct)
    {
        lock (_state) { if (_turnId is not null || _requests.Count > 0) throw new InvalidOperationException("Resolve the active OpenCode turn or pending requests first."); }
        if (string.IsNullOrWhiteSpace(request.Text) && request.Attachments.Count == 0) throw new InvalidOperationException("OpenCode requires text or attachments.");
        ApplySettings(request);
        if (request.Text?.Trim() == "/compact" && request.Attachments.Count == 0)
            return await CompactAsync(command, ct).ConfigureAwait(false);
        var files = new List<(string Mime, string Name, string Url)>();
        foreach (var attachment in request.Attachments)
        {
            var path = Path.GetFullPath(attachment.Path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 10 * 1024 * 1024) throw new InvalidOperationException("OpenCode attachments must exist and be at most 10 MiB.");
            var mime = attachment.MimeType ?? (attachment.Kind == "image" ? "image/png" : "text/plain");
            if (mime.StartsWith("image/", StringComparison.Ordinal) && Value(Value(_models[_settings.Model!], "capabilities"), "input", "image").ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("The selected OpenCode model does not advertise image input.");
            files.Add((mime, attachment.DisplayName ?? info.Name, "data:" + mime + ";base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false))));
        }
        await SendAsync(HttpMethod.Patch, SessionPath(), w => { w.WriteStartObject(); WritePermissions(w); w.WriteEndObject(); }, ct).ConfigureAwait(false);
        string turn;
        lock (_state)
        {
            turn = BeginTurn();
            _userMessageId = "msg_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            _messageTurns[_userMessageId] = turn;
            for (var index = 0; index < files.Count; index++)
            {
                var attachment = request.Attachments[index];
                _submittedAttachments[(_userMessageId, files[index].Url)] = new()
                {
                    Kind = attachment.Kind, Path = attachment.Path,
                    MimeType = attachment.MimeType, DisplayName = attachment.DisplayName
                };
            }
            Publish("item.completed", e => e.Item = new()
            {
                ItemType = "user_message",
                Status = "completed",
                Title = "You",
                Detail = request.Text,
                Attachments = request.Attachments.Select(a => new AppServerControlAttachmentReference { Kind = a.Kind, Path = a.Path, MimeType = a.MimeType, DisplayName = a.DisplayName }).ToList()
            }, item: "local-user:" + turn);
        }
        var text = request.Text ?? string.Empty;
        var slashEnd = text.IndexOfAny([' ', '\r', '\n']);
        var slash = text.StartsWith('/', StringComparison.Ordinal) ? text[1..(slashEnd < 0 ? text.Length : slashEnd)] : null;
        var nativeCommand = slash is not null && slash != "compact" && _availableCommands.Any(c => c.Name == slash);
        Action<Utf8JsonWriter> body = w =>
        {
            w.WriteStartObject();
            w.WriteString("messageID", _userMessageId);
            w.WriteString("agent", _agent);
            if (nativeCommand) w.WriteString("model", _settings.Model);
            else WriteModel(w);
            if (!string.IsNullOrEmpty(_settings.Effort)) w.WriteString("variant", _settings.Effort);
            if (nativeCommand)
            {
                w.WriteString("command", slash);
                w.WriteString("arguments", slashEnd < 0 ? string.Empty : text[(slashEnd + 1)..]);
            }
            w.WriteStartArray("parts");
            if (!nativeCommand && text.Length > 0) { w.WriteStartObject(); w.WriteString("type", "text"); w.WriteString("text", text); w.WriteEndObject(); }
            foreach (var file in files)
            {
                w.WriteStartObject(); w.WriteString("type", "file"); w.WriteString("mime", file.Mime); w.WriteString("filename", file.Name); w.WriteString("url", file.Url); w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject();
        };
        try
        {
            if (nativeCommand) _operations.Add(RunOperationAsync(SessionPath() + "/command", body, turn, compact: false));
            else await SendAsync(HttpMethod.Post, SessionPath() + "/prompt_async", body, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_state) CompleteTurn("failed", ex.Message);
            throw;
        }
        return TurnAccepted(command, turn);
    }

    private Task<HostCommandOutcome> CompactAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var selected = SplitModel();
        string turn;
        lock (_state)
        {
            if (_turnId is not null || _requests.Count > 0) throw new InvalidOperationException("Wait for the OpenCode turn and resolve pending requests before compacting.");
            turn = BeginTurn();
            Publish("item.completed", e => e.Item = new() { ItemType = "user_message", Status = "completed", Title = "You", Detail = "/compact" }, item: "local-user:" + turn);
        }
        _operations.Add(RunOperationAsync(SessionPath() + "/summarize", w =>
        {
            w.WriteStartObject();
            var (provider, model) = selected;
            w.WriteString("providerID", provider); w.WriteString("modelID", model); w.WriteBoolean("auto", false);
            w.WriteEndObject();
        }, turn, compact: true));
        return Task.FromResult(TurnAccepted(command, turn));
    }

    private async Task RunOperationAsync(string path, Action<Utf8JsonWriter> body, string turn, bool compact)
    {
        try
        {
            Task completion;
            lock (_state) completion = _turnCompletion?.Task ?? Task.CompletedTask;
            var result = await SendAsync(HttpMethod.Post, path, body, _shutdown.Token).ConfigureAwait(false);
            if (compact && result.ValueKind == JsonValueKind.False) throw new InvalidOperationException("OpenCode declined native compaction.");
            await RefreshHistoryAsync(replay: false, _shutdown.Token).ConfigureAwait(false);
            try { await completion.WaitAsync(TimeSpan.FromSeconds(5), _shutdown.Token).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                // HTTP generation returned, but its last SSE notifications may
                // still be buffered. Confirm quiescence before admitting work.
                if (await IsNativeIdleAsync(_shutdown.Token).ConfigureAwait(false))
                {
                    lock (_state)
                    {
                        if (_turnId == turn && _requests.Count == 0) CompleteTurn(_interrupting ? "interrupted" : _error is null ? "completed" : "failed", _error);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (_state) { if (_turnId == turn) CompleteTurn("failed", ex.Message); }
        }
    }

    private async Task<string> InterruptAsync(string? expectedTurn, CancellationToken ct)
    {
        Task completion;
        string turn;
        lock (_state)
        {
            if (_turnId is null || (!string.IsNullOrEmpty(expectedTurn) && expectedTurn != _turnId)) throw new InvalidOperationException("OpenCode interrupt target no longer matches the active turn.");
            _interrupting = true;
            turn = _turnId;
            completion = _turnCompletion!.Task;
        }
        var aborted = await SendAsync(HttpMethod.Post, SessionPath() + "/abort", w => { w.WriteStartObject(); w.WriteEndObject(); }, ct).ConfigureAwait(false);
        if (aborted.ValueKind == JsonValueKind.False) throw new InvalidOperationException("OpenCode did not accept cancellation.");
        try { await completion.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            await StopServerAsync().ConfigureAwait(false);
            throw new InvalidOperationException("OpenCode did not settle cancellation; its owned server was stopped to prevent overlapping prompts.");
        }
        return turn;
    }

    private string BeginTurn()
    {
        _turnId = "opencode-turn-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        _error = null; _busy = false; _assistantSeen = false; _interrupting = false;
        _userReceipt = false;
        _recoveringEvents = false;
        _turnCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PublishSettings();
        PublishSession("running", "Running");
        Publish("turn.started", e => e.TurnStarted = new() { Model = _settings.Model, Effort = _settings.Effort });
        return _turnId;
    }

    private void CompleteTurn(string state, string? error = null)
    {
        if (_turnId is null) return;
        var turn = _turnId;
        var completion = _turnCompletion;
        _turnId = null; _turnCompletion = null; _userMessageId = null;
        foreach (var request in _requests.Values.ToArray()) CloseRequest(request.Id, "cancelled");
        Publish("turn.completed", e => e.TurnCompleted = new() { State = state, StateLabel = state == "completed" ? "Completed" : state == "interrupted" ? "Interrupted" : "Failed", ErrorMessage = error }, turn: turn);
        PublishSession("ready", "Ready");
        completion?.TrySetResult();
    }

    private void PublishSession(string state, string label) => Publish(state == "ready" ? "session.ready" : "session.state.changed", e => e.SessionState = new() { State = state, StateLabel = label });
    private void PublishSettings() => Publish("quick-settings.updated", e => e.QuickSettingsUpdated = AppServerControlQuickSettings.ToPayload(_settings));

    private void Publish(string type, Action<AppServerControlProviderEvent> configure, string? item = null, string? request = null, string? turn = null, DateTimeOffset? at = null)
    {
        var evt = new AppServerControlProviderEvent
        {
            Sequence = Interlocked.Increment(ref _sequence),
            EventId = "evt-opencode-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            SessionId = _sessionId,
            Provider = Provider,
            ThreadId = _threadId,
            TurnId = turn ?? _turnId,
            ItemId = item,
            RequestId = request,
            Type = type,
            CreatedAt = at ?? DateTimeOffset.UtcNow,
            Raw = new() { Source = "opencode.http", Method = type, PayloadOmitted = true }
        };
        configure(evt); _emit(evt);
    }

    private static HostCommandOutcome Accepted(AppServerControlHostCommandEnvelope command, string? turn = null) => new()
    {
        Result = new() { CommandId = command.CommandId, SessionId = command.SessionId, Status = "accepted", Accepted = new() { SessionId = command.SessionId, TurnId = turn, RequestId = command.ResolveRequest?.RequestId ?? command.ResolveUserInput?.RequestId } }
    };

    private HostCommandOutcome TurnAccepted(AppServerControlHostCommandEnvelope command, string turn)
    {
        var accepted = Accepted(command, turn);
        accepted.Result.TurnStarted = new()
        {
            SessionId = _sessionId,
            Provider = Provider,
            ThreadId = _threadId,
            TurnId = turn,
            QuickSettings = new()
            {
                Model = _settings.Model,
                Effort = _settings.Effort,
                PlanMode = _settings.PlanMode,
                PermissionMode = _settings.PermissionMode,
                ModelOptions = AppServerControlQuickSettings.CloneOptions(_settings.ModelOptions),
                EffortOptions = AppServerControlQuickSettings.CloneOptions(_settings.EffortOptions),
                ConfigOptions = AppServerControlQuickSettings.CloneConfigOptions(_settings.ConfigOptions)
            }
        };
        return accepted;
    }

    private string SessionPath() => "/session/" + Uri.EscapeDataString(_threadId);

    private async Task LoadChildSessionsAsync(string session, CancellationToken ct)
    {
        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { session };
        pending.Enqueue(session);
        while (pending.TryDequeue(out var parent))
        {
            var children = await SendAsync(HttpMethod.Get, "/session/" + Uri.EscapeDataString(parent) + "/children", null, ct).ConfigureAwait(false);
            foreach (var child in Array(children))
            {
                var id = Text(child, "id");
                if (id is null || !visited.Add(id)) continue;
                if (visited.Count > 512) throw new InvalidOperationException("OpenCode session descendants exceed the supported recovery limit.");
                lock (_state) _nativeSessions.Add(id);
                pending.Enqueue(id);
            }
        }
    }
    private static string? Text(JsonElement root, string property) => Value(root, property) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    private static JsonElement Value(JsonElement root, params string[] path)
    {
        foreach (var key in path) { if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out root)) return default; }
        return root;
    }
    private static JsonElement[] Array(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return [];
        using var items = root.EnumerateArray();
        return items.ToArray();
    }
    private static JsonProperty[] Properties(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return [];
        using var properties = root.EnumerateObject();
        return properties.ToArray();
    }
    private sealed record PendingOpenCodeRequest(string Id, string? Turn, bool Question, List<AppServerControlQuestion> Questions);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _shutdown.Cancel();
        await StopServerAsync().ConfigureAwait(false);
        foreach (var task in _operations.Prepend(_eventsTask))
        {
            if (task is null) continue;
            try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _http.Dispose(); _shutdown.Dispose(); _commands.Dispose();
    }
}
