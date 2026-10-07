using System.Globalization;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

internal static class CodexCliIdentityService
{
    // A detached daemon buffers the original tool's output until it returns.
    // A separate shellCommand emits the challenge through the thread's actual
    // subscribed TUI clients while that original request is still waiting.
    internal static async Task<bool> EmitProofAsync(string rootId, string? codexHome, string proof, CancellationToken ct)
    {
        if (!Guid.TryParseExact(rootId, "D", out _) || proof.Length != 40 ||
            !proof.StartsWith(SessionCliContextService.MarkerPrefix, StringComparison.Ordinal) ||
            proof.AsSpan(8).ContainsAnyExcept("0123456789abcdef".AsSpan())) return false;
        var home = string.IsNullOrWhiteSpace(codexHome)
            ? Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : codexHome;
        if (!Path.IsPathFullyQualified(home)) return false;
        var socketPath = Path.Combine(home, "app-server-control", "app-server-control.sock");
        if (!File.Exists(socketPath)) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var handler = CreateDaemonHandler(socketPath);
            using var invoker = new HttpMessageInvoker(handler);
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri("ws://localhost/"), invoker, timeout.Token).ConfigureAwait(false);
            await SendAsync(ws, "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"tlbx_cli_context\",\"version\":\"1\"}}}", timeout.Token);
            using var initialized = await ReadAsync(ws, 1, timeout.Token);
            await SendAsync(ws, "{\"method\":\"initialized\"}", timeout.Token);
            // Both values are validated above. echo works in PowerShell and POSIX shells;
            // never send user command text through this local challenge channel.
            await SendAsync(ws, $"{{\"id\":2,\"method\":\"thread/shellCommand\",\"params\":{{\"threadId\":\"{rootId}\",\"command\":\"echo {proof}\",\"timeoutMs\":2000}}}}", timeout.Token);
            using var submitted = await ReadAsync(ws, 2, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or WebSocketException or JsonException or
            OperationCanceledException or KeyNotFoundException or InvalidOperationException) { }
        return false;
    }

    internal static async Task<bool> ExistsAsync(string rootId, string? codexHome, CancellationToken ct)
    {
        if (!Guid.TryParseExact(rootId, "D", out _)) return false;
        var home = string.IsNullOrWhiteSpace(codexHome)
            ? Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : codexHome;
        if (!Path.IsPathFullyQualified(home)) return false;
        // A rollout proves the full conversation identity, never which terminal owns it.
        // UUIDv7 embeds the creation day, avoiding scans of the user's history.
        try
        {
            if (rootId[14] == '7')
            {
                var milliseconds = long.Parse(string.Concat(rootId.AsSpan(0, 8), rootId.AsSpan(9, 4)), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var day = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
                // Codex partitions rollouts by local date, which can differ from the UUID's
                // UTC day. Check both adjacent days as the client and service may use
                // different time zones; the complete metadata UUID remains authoritative.
                for (var offset = -1; offset <= 1; offset++)
                {
                    var directory = Path.Combine(home, "sessions", day.AddDays(offset).ToString("yyyy/MM/dd", CultureInfo.InvariantCulture));
                    if (!Directory.Exists(directory)) continue;
                    foreach (var path in Directory.EnumerateFiles(directory, "*" + rootId + ".jsonl"))
                    {
                        // Codex may keep the rollout open for append, including after resume.
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(stream);
                        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                        if (line is null) continue;
                        using var meta = JsonDocument.Parse(line);
                        if (meta.RootElement.GetProperty("type").GetString() == "session_meta" &&
                            meta.RootElement.GetProperty("payload").GetProperty("id").GetString() == rootId) return true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            FormatException or ArgumentException or KeyNotFoundException) { }

        var socketPath = Path.Combine(home, "app-server-control", "app-server-control.sock");
        if (!File.Exists(socketPath)) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var handler = CreateDaemonHandler(socketPath);
            using var invoker = new HttpMessageInvoker(handler);
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri("ws://localhost/"), invoker, timeout.Token).ConfigureAwait(false);
            await SendAsync(ws, "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"tlbx_cli_context\",\"version\":\"1\"}}}", timeout.Token);
            using var initialized = await ReadAsync(ws, 1, timeout.Token);
            await SendAsync(ws, "{\"method\":\"initialized\"}", timeout.Token);
            string? cursor = null;
            for (var id = 2; id < 66; id++)
            {
                var cursorValue = cursor is null ? "null" : "\"" + JsonEncodedText.Encode(cursor) + "\"";
                await SendAsync(ws, string.Create(CultureInfo.InvariantCulture, $"{{\"id\":{id},\"method\":\"thread/loaded/list\",\"params\":{{\"cursor\":{cursorValue}}}}}"), timeout.Token);
                using var page = await ReadAsync(ws, id, timeout.Token);
                var result = page.RootElement.GetProperty("result");
                using var items = result.GetProperty("data").EnumerateArray();
                foreach (var item in items)
                    if (item.GetString() == rootId) return true;
                cursor = result.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
                if (cursor is null) break;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or WebSocketException or JsonException or
            OperationCanceledException or KeyNotFoundException or InvalidOperationException) { }
        return false;
    }

    private static SocketsHttpHandler CreateDaemonHandler(string socketPath) => new()
    {
        ConnectCallback = async (_, token) =>
        {
            // Ownership transfers to the returned NetworkStream.
#pragma warning disable IDISP001
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
#pragma warning restore IDISP001
            try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), token).ConfigureAwait(false); return new NetworkStream(socket, true); }
            catch { socket.Dispose(); throw; }
        }
    };

    private static Task SendAsync(ClientWebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonDocument> ReadAsync(ClientWebSocket ws, int id, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (true)
        {
            using var body = new MemoryStream();
            WebSocketReceiveResult part;
            do
            {
                part = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (part.MessageType == WebSocketMessageType.Close || body.Length + part.Count > 1024 * 1024)
                    throw new IOException("Codex context response closed or exceeded its limit.");
                body.Write(buffer, 0, part.Count);
            } while (!part.EndOfMessage);
            var doc = JsonDocument.Parse(body.ToArray());
            if (doc.RootElement.TryGetProperty("id", out var responseId) && responseId.TryGetInt32(out var value) && value == id)
            {
                if (doc.RootElement.TryGetProperty("error", out _)) { doc.Dispose(); throw new IOException("Codex context request rejected."); }
                return doc;
            }
            doc.Dispose();
        }
    }
}
