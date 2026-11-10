using System.Globalization;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.UnitTests;

internal static class AppServerControlHostTestClient
{
    public static async Task WriteCommandAsync(StreamWriter writer, AppServerControlHostCommandEnvelope command)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(command, AppServerControlHostJsonContext.Default.AppServerControlHostCommandEnvelope).AsMemory(), Xunit.TestContext.Current.CancellationToken);
        await writer.FlushAsync(Xunit.TestContext.Current.CancellationToken);
    }

    public static async Task<AppServerControlHostHello> ReadHelloAsync(StreamReader reader)
    {
        var line = await ReadLineWithTimeoutAsync(reader);
        return JsonSerializer.Deserialize(line, AppServerControlHostJsonContext.Default.AppServerControlHostHello)
               ?? throw new InvalidOperationException("Failed to deserialize AppServerControl host hello.");
    }

    public static async Task<AppServerControlHostCommandResultEnvelope> ReadResultAsync(
        StreamReader reader,
        Queue<AppServerControlHostHistoryPatchEnvelope> pendingPatches,
        string? expectedCommandId = null)
    {
        while (true)
        {
            var message = await ReadMessageAsync(reader);
            if (message.Patch is not null)
            {
                pendingPatches.Enqueue(message.Patch);
                continue;
            }

            if (message.Result is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(expectedCommandId) ||
                string.Equals(message.Result.CommandId, expectedCommandId, StringComparison.Ordinal))
            {
                return message.Result;
            }
        }
    }

    public static async Task<AppServerControlHostHistoryPatchEnvelope> ReadPatchAsync(
        StreamReader reader,
        Queue<AppServerControlHostHistoryPatchEnvelope> pendingPatches)
    {
        return await ReadPatchAsync(reader, pendingPatches, TimeSpan.FromSeconds(30));
    }

    public static async Task<AppServerControlHostHistoryPatchEnvelope> ReadPatchAsync(
        StreamReader reader,
        Queue<AppServerControlHostHistoryPatchEnvelope> pendingPatches,
        TimeSpan timeout)
    {
        if (pendingPatches.Count > 0)
        {
            return pendingPatches.Dequeue();
        }

        while (true)
        {
            var message = await ReadMessageAsync(reader, timeout);
            if (message.Patch is not null)
            {
                return message.Patch;
            }
        }
    }

    public static async Task<IReadOnlyList<AppServerControlHostHistoryPatchEnvelope>> ReadUntilAsync(
        StreamReader reader,
        Queue<AppServerControlHostHistoryPatchEnvelope> pendingPatches,
        Func<AppServerControlHostHistoryPatchEnvelope, bool> predicate,
        int maxPatches)
    {
        var patches = new List<AppServerControlHostHistoryPatchEnvelope>();
        while (patches.Count < maxPatches)
        {
            var envelope = await ReadPatchAsync(reader, pendingPatches);
            patches.Add(envelope);
            if (predicate(envelope))
            {
                break;
            }
        }

        return patches;
    }

    public static async Task<IReadOnlyList<AppServerControlHostHistoryPatchEnvelope>> ReadUntilMatchAsync(
        StreamReader reader,
        Queue<AppServerControlHostHistoryPatchEnvelope> pendingPatches,
        Func<AppServerControlHostHistoryPatchEnvelope, bool> predicate,
        int maxPatches = 64,
        TimeSpan? timeout = null)
    {
        var patches = new List<AppServerControlHostHistoryPatchEnvelope>();
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));

        while (patches.Count < maxPatches)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var envelope = await ReadPatchAsync(reader, pendingPatches, remaining);
            patches.Add(envelope);
            if (predicate(envelope))
            {
                return patches;
            }
        }

        throw new TimeoutException(
            $"Timed out waiting for a matching AppServerControl host patch. Saw {patches.Count} patch(es): {string.Join(", ", patches.Select(static envelope => envelope.Patch.CurrentTurn.State))}");
    }

    public static async Task<IReadOnlyList<AppServerControlHostHistoryPatchEnvelope>> ReadPatchesAsync(
        StreamReader reader,
        Queue<AppServerControlHostHistoryPatchEnvelope> pendingPatches,
        int count)
    {
        var patches = new List<AppServerControlHostHistoryPatchEnvelope>(count);
        while (patches.Count < count)
        {
            patches.Add(await ReadPatchAsync(reader, pendingPatches));
        }

        return patches;
    }

    public static async Task<AppServerControlHistoryWindowResponse> GetHistoryWindowAsync(
        StreamReader reader,
        StreamWriter writer,
        Queue<AppServerControlHostHistoryPatchEnvelope> pendingPatches,
        string sessionId,
        int? startIndex = null,
        int? count = null,
        int? viewportWidth = null,
        string? commandId = null)
    {
        var requestCommandId = string.IsNullOrWhiteSpace(commandId)
            ? $"cmd-history-{Guid.NewGuid():N}"
            : commandId;

        await WriteCommandAsync(writer, new AppServerControlHostCommandEnvelope
        {
            CommandId = requestCommandId,
            SessionId = sessionId,
            Type = "history.window.get",
            HistoryWindow = new AppServerControlHostHistoryWindowRequest
            {
                StartIndex = startIndex,
                Count = count,
                ViewportWidth = viewportWidth
            }
        });

        var result = await ReadResultAsync(reader, pendingPatches, requestCommandId);
        return result.HistoryWindow
               ?? throw new InvalidOperationException("mtagenthost did not return a history window.");
    }

    public static async Task<AppServerControlHistoryWindowResponse> WaitForHistoryWindowAsync(
        StreamReader reader,
        StreamWriter writer,
        Queue<AppServerControlHostHistoryPatchEnvelope> pendingPatches,
        string sessionId,
        Func<AppServerControlHistoryWindowResponse, bool> predicate,
        TimeSpan timeout,
        int? count = null)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var window = await GetHistoryWindowAsync(reader, writer, pendingPatches, sessionId, count: count);
            if (predicate(window))
            {
                return window;
            }

            await Task.Delay(100, Xunit.TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for a matching history window for AppServerControl session '{sessionId}'.");
    }

    public static string CollectAssistantText(AppServerControlHistoryWindowResponse window)
    {
        var completedAssistantMessages = window.History
            .Where(static item =>
                string.Equals(item.ItemType, "assistant_message", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(item.Body))
            .Select(static item => item.Body)
            .ToList();

        if (completedAssistantMessages.Count > 0)
        {
            return string.Join(Environment.NewLine, completedAssistantMessages);
        }

        return window.Streams.AssistantText;
    }

    private static async Task<AppServerControlHostMessage> ReadMessageAsync(StreamReader reader, TimeSpan? timeout = null)
    {
        var line = await ReadLineWithTimeoutAsync(reader, timeout);
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        if (root.TryGetProperty("patch", out _))
        {
            return new AppServerControlHostMessage
            {
                Patch = JsonSerializer.Deserialize(line, AppServerControlHostJsonContext.Default.AppServerControlHostHistoryPatchEnvelope)
                        ?? throw new InvalidOperationException("Failed to deserialize AppServerControl host history patch.")
            };
        }

        if (root.TryGetProperty("commandId", out _))
        {
            return new AppServerControlHostMessage
            {
                Result = JsonSerializer.Deserialize(line, AppServerControlHostJsonContext.Default.AppServerControlHostCommandResultEnvelope)
                         ?? throw new InvalidOperationException("Failed to deserialize AppServerControl host command result.")
            };
        }

        throw new InvalidOperationException("Unexpected AppServerControl host message payload.");
    }

    private static async Task<string> ReadLineWithTimeoutAsync(StreamReader reader, TimeSpan? timeout = null)
    {
        var readTask = reader.ReadLineAsync(Xunit.TestContext.Current.CancellationToken).AsTask();
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var completed = await Task.WhenAny(readTask, Task.Delay(effectiveTimeout, Xunit.TestContext.Current.CancellationToken));
        if (!ReferenceEquals(completed, readTask))
        {
            var secondsText = effectiveTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            throw new TimeoutException($"Timed out waiting {secondsText}s for mtagenthost stdout.");
        }

        var line = await readTask;
        return line ?? throw new EndOfStreamException("mtagenthost closed stdout unexpectedly.");
    }

    private sealed class AppServerControlHostMessage
    {
        public AppServerControlHostCommandResultEnvelope? Result { get; init; }

        public AppServerControlHostHistoryPatchEnvelope? Patch { get; init; }
    }
}
