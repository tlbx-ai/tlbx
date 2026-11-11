using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class OpenCodeAppServerControlAgentRuntime
{
    private Process? _server;
    private Task? _stdout;
    private Task? _stderr;
    private bool _recoveringEvents;
    private DateTimeOffset _lastRecoverySnapshot;

    private async Task StartServerAsync(AppServerControlAttachRuntimeRequest attach, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(attach.ExecutablePath) || !File.Exists(attach.ExecutablePath)) throw new InvalidOperationException("OpenCode executable was not found.");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var start = CreateServerStartInfo(attach.ExecutablePath, attach.ExecutableArguments, attach.WorkingDirectory, port);
        AppServerControlProviderRuntimeConfiguration.ApplyUserProfileEnvironment(start, attach.UserProfileDirectory);
        AppServerControlProviderRuntimeConfiguration.ApplyEnvironmentVariables(start, Provider);
        start.Environment.Remove("FORCE_COLOR");
        start.Environment["NO_COLOR"] = "1";
        foreach (var pair in attach.EnvironmentVariables)
        {
            if (pair.Value is null) start.Environment.Remove(pair.Key);
            else start.Environment[pair.Key] = pair.Value;
        }
        // Never put the secret in argv, history, event payloads, or diagnostics.
        start.Environment["OPENCODE_SERVER_USERNAME"] = "opencode";
        start.Environment["OPENCODE_SERVER_PASSWORD"] = password;
        var logs = Path.Combine(Path.GetTempPath(), "tlbx-opencode", _sessionId + "-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(logs);
        _server?.Dispose();
        _server = new Process { StartInfo = start };
        if (!_server.Start()) throw new InvalidOperationException("OpenCode server did not start.");
        _stdout = DrainAsync(_server.StandardOutput, Path.Combine(logs, "stdout.log"), password);
        _stderr = DrainAsync(_server.StandardError, Path.Combine(logs, "stderr.log"), password);
        _http.BaseAddress = new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture), UriKind.Absolute);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + password)));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_server.HasExited) throw new InvalidOperationException($"OpenCode server exited with code {_server.ExitCode.ToString(CultureInfo.InvariantCulture)}. Logs: {logs}");
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(1));
                using var response = await _http.GetAsync("/global/health", attempt.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    Publish("runtime.notice", e => e.RuntimeNoticeOnly = new() { Message = "OpenCode server ready", Detail = "Logs: " + logs });
                    return;
                }
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        throw new InvalidOperationException("OpenCode server did not become healthy. Logs: " + logs);
    }

    internal static ProcessStartInfo CreateServerStartInfo(string executable, IReadOnlyList<string> prefixArguments, string directory, int port)
    {
        var arguments = prefixArguments.Concat(["serve", "--hostname", "127.0.0.1", "--port", port.ToString(CultureInfo.InvariantCulture)]).ToArray();
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        var extension = Path.GetExtension(executable);
        if (OperatingSystem.IsWindows() && (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            // npm installs a cmd shim on Windows. Keep the shell boundary
            // explicit and reject expansion/operators in manifest arguments.
            var values = arguments.Prepend(executable).ToArray();
            if (values.Any(value => value.IndexOfAny(['"', '\r', '\n', '\0', '%', '!', '&', '|', '<', '>', '^']) >= 0))
                throw new InvalidOperationException("OpenCode cmd shim paths/arguments must not contain shell expansion characters. Configure the native executable instead.");
            info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            info.Arguments = "/d /s /c \"" + string.Join(" ", values.Select(value => "\"" + value + "\"")) + "\"";
        }
        else
        {
            if (OperatingSystem.IsWindows() && extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                info.FileName = "pwsh.exe";
                foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", executable }) info.ArgumentList.Add(arg);
            }
            foreach (var arg in arguments) info.ArgumentList.Add(arg);
        }
        return info;
    }

    private static async Task DrainAsync(StreamReader reader, string path, string secret)
    {
        await using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            await writer.WriteLineAsync(line.Replace(secret, "[redacted]", StringComparison.Ordinal)).ConfigureAwait(false);
    }

    private async Task StopServerAsync()
    {
        lock (_state) _ready = false;
        if (_server is not null)
        {
            try { if (!_server.HasExited) _server.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            try { await _server.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch (InvalidOperationException) { }
            foreach (var drain in new[] { _stdout, _stderr }) if (drain is not null) await drain.ConfigureAwait(false);
            _server.Dispose(); _server = null;
        }
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, Action<Utf8JsonWriter>? body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        // Generation endpoints can legitimately run longer; ordinary control and
        // history calls must fail promptly instead of freezing the command lane.
        if (!path.EndsWith("/command", StringComparison.Ordinal) && !path.EndsWith("/summarize", StringComparison.Ordinal)) timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(method, ScopedPath(path));
        if (body is not null)
        {
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes)) { body(writer); }
            request.Content = new ByteArrayContent(bytes.GetBuffer(), 0, checked((int)bytes.Length));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Provider error bodies may contain settings or credentials. Keep
            // control failures useful without echoing those bodies into chat.
            throw new InvalidOperationException($"OpenCode {method.Method} {path.Split('?')[0]} failed (HTTP {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}).");
        }
        if (response.StatusCode == HttpStatusCode.NoContent) return default;
        var content = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        if (content.Length == 0) return default;
        using var document = JsonDocument.Parse(content);
        return document.RootElement.Clone();
    }

    private string ScopedPath(string path) => path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "directory=" + Uri.EscapeDataString(_attachment!.WorkingDirectory);

    private async Task ReadEventsAsync(TaskCompletionSource connected, CancellationToken ct)
    {
        var reconnect = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ScopedPath("/event"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                connected.TrySetResult();
                if (reconnect && _ready)
                {
                    lock (_state) _recoveringEvents = true;
                    await RefreshHistoryAsync(replay: false, ct).ConfigureAwait(false);
                    await LoadChildSessionsAsync(_threadId, ct).ConfigureAwait(false);
                    var permissions = await SendAsync(HttpMethod.Get, "/permission", null, ct).ConfigureAwait(false);
                    var questions = await SendAsync(HttpMethod.Get, "/question", null, ct).ConfigureAwait(false);
                    lock (_state)
                    {
                        var pending = Array(permissions).Concat(Array(questions)).Select(p => Text(p, "id")).ToHashSet(StringComparer.Ordinal);
                        foreach (var stale in _requests.Keys.Where(id => !pending.Contains(id)).ToArray()) CloseRequest(stale, "resolved");
                        foreach (var permission in Array(permissions)) OpenRequest(permission, question: false);
                        foreach (var question in Array(questions)) OpenRequest(question, question: true);
                    }
                    var statuses = await SendAsync(HttpMethod.Get, "/session/status", null, ct).ConfigureAwait(false);
                    lock (_state)
                    {
                        if ((Text(Value(statuses, _threadId), "type") is null or "idle") && CanFinishFromIdle())
                            CompleteTurn(_interrupting ? "interrupted" : _error is null ? "completed" : "failed", _error);
                    }
                }
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                var data = new StringBuilder();
                while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        if (data.Length > 0) data.Append('\n');
                        data.Append(line.AsSpan(5).TrimStart());
                        if (data.Length > 32 * 1024 * 1024) throw new JsonException("OpenCode event exceeded the supported frame size.");
                    }
                    else if (line.Length == 0 && data.Length > 0)
                    {
                        using var document = JsonDocument.Parse(data.ToString());
                        data.Clear();
                        var evt = document.RootElement;
                        if (Value(evt, "payload").ValueKind == JsonValueKind.Object) evt = Value(evt, "payload");
                        if (_recoveringEvents && Text(evt, "type") == "message.part.delta")
                        {
                            // A snapshot fetched after reconnect can already
                            // contain buffered deltas. Refresh authoritative
                            // text during this turn instead of applying them twice.
                            if (DateTimeOffset.UtcNow - _lastRecoverySnapshot > TimeSpan.FromMilliseconds(250))
                            {
                                await RefreshHistoryAsync(replay: false, ct).ConfigureAwait(false);
                                _lastRecoverySnapshot = DateTimeOffset.UtcNow;
                            }
                            continue;
                        }
                        var finish = false;
                        lock (_state)
                        {
                            if (!_ready) _bufferedEvents.Add(evt.Clone());
                            else finish = ProjectEvent(evt);
                        }
                        if (finish)
                        {
                            if (!await IsNativeIdleAsync(ct).ConfigureAwait(false)) continue;
                            await RefreshHistoryAsync(replay: false, ct).ConfigureAwait(false);
                            lock (_state)
                            {
                                if (_turnId is not null) CompleteTurn(_interrupting ? "interrupted" : _error is null ? "completed" : "failed", _error);
                                _recoveringEvents = false;
                            }
                        }
                    }
                }
                if (!ct.IsCancellationRequested) throw new IOException("OpenCode event stream closed.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
            {
                if (!connected.Task.IsCompleted) { connected.TrySetException(ex); return; }
                if (_server is { HasExited: true })
                {
                    lock (_state)
                    {
                        CompleteTurn("failed", "The owned OpenCode server exited.");
                        _ready = false;
                        PublishSession("error", "Server exited");
                    }
                    return;
                }
                lock (_state) Publish("runtime.warning", e => e.RuntimeMessage = new() { Message = "OpenCode event stream interrupted; reconnecting.", Detail = ex.GetType().Name });
                reconnect = true;
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> IsNativeIdleAsync(CancellationToken ct)
    {
        var statuses = await SendAsync(HttpMethod.Get, "/session/status", null, ct).ConfigureAwait(false);
        return Text(Value(statuses, _threadId), "type") is null or "idle";
    }
}
