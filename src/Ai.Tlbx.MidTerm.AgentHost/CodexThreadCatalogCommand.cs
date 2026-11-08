using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Process;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal static class CodexThreadCatalogCommand
{
    internal const int Limit = 60;

    public static async Task<int> RunAsync(string? cwd)
    {
        try
        {
            var lookup = new ProcessStartInfo();
            AppServerControlProviderRuntimeConfiguration.ApplyEnvironmentVariables(lookup, "codex");
            var binary = CodexAppServerControlAgentRuntime.FindExecutableInPath("codex", lookup.Environment["PATH"])
                ?? throw new InvalidOperationException("Codex CLI unavailable.");
            var start = CodexAppServerControlAgentRuntime.CreateCodexAppServerStartInfo(
                binary, cwd ?? Environment.CurrentDirectory, null);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var result = await QueryAsync(start, cwd, timeout.Token).ConfigureAwait(false);
            await Console.Out.WriteLineAsync(result).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or
                                   System.ComponentModel.Win32Exception or OperationCanceledException or JsonException or
                                   KeyNotFoundException or ArgumentException)
        {
            // Provider stderr can contain private configuration. Never echo it or exception messages.
            await Console.Error.WriteLineAsync("Codex catalog unavailable.").ConfigureAwait(false);
            return 1;
        }
    }

    internal static async Task<string> QueryAsync(ProcessStartInfo start, string? cwd, CancellationToken ct)
    {
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Catalog process did not start.");
        using var drains = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stderr = BoundedProcessText.DrainTailAsync(process.StandardError, drains.Token);
        var stdout = new BoundedProcessText(process.StandardOutput, 8 * 1024 * 1024);
        var requestId = 0;
        try
        {
            await process.StandardInput.WriteLineAsync(
                CodexAppServerControlAgentRuntime.BuildCodexInitializeRequest("catalog-init").AsMemory(), ct).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            await ReadResponseAsync("catalog-init").ConfigureAwait(false);
            await process.StandardInput.WriteLineAsync(
                CodexAppServerControlAgentRuntime.BuildCodexInitializedNotification().AsMemory(), ct).ConfigureAwait(false);
            var threads = await RequestAsync("thread/list", writer =>
            {
                writer.WriteString("sortKey", "updated_at");
                writer.WriteString("sortDirection", "desc");
                writer.WriteBoolean("useStateDbOnly", true);
                writer.WriteNumber("limit", Limit);
                if (cwd is not null) writer.WriteString("cwd", cwd);
            }).ConfigureAwait(false);
            var rows = new List<(JsonElement Thread, IReadOnlyList<string> Latest)>();
            var data = threads.GetProperty("data");
            for (var index = 0; index < Math.Min(data.GetArrayLength(), Limit); index++)
            {
                var thread = data[index];
                if (thread.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid thread metadata.");
                var id = thread.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("Missing thread identity.");
                rows.Add((thread.Clone(), []));
            }
            // Preview hydration is optional. Keep the complete fast metadata page even
            // when a single latest turn is slow, oversized, unavailable or malformed.
            using var hydration = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hydration.CancelAfter(TimeSpan.FromSeconds(3));
            for (var index = 0; index < Math.Min(rows.Count, 8); index++)
            {
                var thread = rows[index].Thread;
                try
                {
                    var turns = await RequestAsync("thread/turns/list", writer =>
                    {
                        writer.WriteString("threadId", thread.GetProperty("id").GetString());
                        writer.WriteNumber("limit", 1);
                        writer.WriteString("sortDirection", "desc");
                        writer.WriteString("itemsView", "full");
                    }, hydration.Token, 1).ConfigureAwait(false);
                    rows[index] = (thread, ReadLatestUserTexts(turns));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or
                                           JsonException or InvalidOperationException or KeyNotFoundException)
                {
                    // The response may still arrive; never reuse this transport for
                    // another request after an uncertain optional read.
                    break;
                }
            }
            return CodexAppServerControlAgentRuntime.BuildJsonString(writer =>
            {
                writer.WriteStartArray();
                foreach (var (thread, latest) in rows)
                {
                    writer.WriteStartObject();
                    foreach (var name in new[] { "id", "cwd", "name", "preview", "updatedAt" })
                    {
                        if (!thread.TryGetProperty(name, out var value)) continue;
                        writer.WritePropertyName(name);
                        if (value.ValueKind == JsonValueKind.String && name is "name" or "preview")
                            writer.WriteStringValue(Truncate(value.GetString()));
                        else value.WriteTo(writer);
                    }
                    writer.WriteStartArray("latestUserTexts");
                    foreach (var text in latest) writer.WriteStringValue(text);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            });
        }
        finally
        {
            await BoundedProcessText.StopAsync(process).ConfigureAwait(false);
            await drains.CancelAsync().ConfigureAwait(false);
            try { await stderr.ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException) { }
        }

        async Task<JsonElement> RequestAsync(string method, Action<Utf8JsonWriter> parameters,
            CancellationToken? requestToken = null, int timeoutSeconds = 3)
        {
            var cancellation = requestToken ?? ct;
            var id = "catalog-" + ++requestId;
            var request = CodexAppServerControlAgentRuntime.BuildJsonString(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("id", id);
                writer.WriteString("method", method);
                writer.WriteStartObject("params");
                parameters(writer);
                writer.WriteEndObject();
                writer.WriteEndObject();
            });
            await process.StandardInput.WriteLineAsync(request.AsMemory(), cancellation).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellation).ConfigureAwait(false);
            return await ReadResponseAsync(id, cancellation, timeoutSeconds).ConfigureAwait(false);
        }

        async Task<JsonElement> ReadResponseAsync(string id, CancellationToken? requestToken = null, int timeoutSeconds = 3)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestToken ?? ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            for (var messages = 0; messages < 128; messages++)
            {
                var line = await stdout.ReadLineAsync(timeout.Token).ConfigureAwait(false)
                    ?? throw new IOException("Catalog stdout closed.");
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid catalog response.");
                if (!root.TryGetProperty("id", out var responseId)) continue;
                if (responseId.ValueKind != JsonValueKind.String || responseId.GetString() != id)
                    throw new InvalidDataException("Unexpected catalog response.");
                if (root.TryGetProperty("error", out _)) throw new CatalogRequestException();
                if (!root.TryGetProperty("result", out var result)) throw new InvalidDataException("Missing catalog result.");
                return result.Clone();
            }
            throw new InvalidDataException("Too many catalog notifications.");
        }
    }

    private sealed class CatalogRequestException : IOException { }

    private static IReadOnlyList<string> ReadLatestUserTexts(JsonElement turns)
    {
        var texts = new List<string>();
        var data = turns.GetProperty("data");
        if (data.GetArrayLength() == 0) return texts;
        var turn = data[0];
        if (!turn.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return texts;
        for (var index = items.GetArrayLength() - 1; index >= 0 && texts.Count < 16; index--)
        {
            var item = items[index];
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "userMessage" ||
                !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            var text = new StringBuilder();
            using var parts = content.EnumerateArray();
            foreach (var part in parts)
            {
                if (!part.TryGetProperty("type", out var kind) || kind.GetString() != "text") continue;
                var value = part.GetProperty("text").GetString();
                if (string.IsNullOrEmpty(value)) continue;
                if (text.Length > 0) text.Append('\n');
                text.Append(value.AsSpan(0, Math.Min(value.Length, 4096 - text.Length)));
                if (text.Length >= 4096) break;
            }
            if (!string.IsNullOrWhiteSpace(text.ToString())) texts.Add(text.ToString());
        }
        return texts;
    }

    private static string? Truncate(string? value) => value is { Length: > 4096 } ? value[..4096] : value;
}
