using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models.Sessions;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

internal static class AppServerControlContextHandoff
{
    private const int PageSize = 128;
    private const int TerminalByteLimit = 24 * 1024;

    public static async Task<string> SaveAsync(
        TtyHostSessionManager sessions,
        SessionAppServerControlRuntimeService runtime,
        SessionInfoDto source,
        string targetSessionId,
        CancellationToken ct)
    {
        var directory = sessions.GetTempDirectory(targetSessionId);
        if (source.AppServerControlOnly)
            return await SaveChatAsync(runtime, source, directory, ct).ConfigureAwait(false);

        var snapshot = await sessions.GetBufferAsync(source.Id, maxBytes: TerminalByteLimit, ct: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Terminal history is unavailable.");
        var text = TerminalOutputSanitizer.StripEscapeSequences(TerminalOutputSanitizer.Decode(snapshot.Data));
        var file = Path.Combine(directory, "terminal-output.txt");
        if (snapshot.SequenceStart > 0)
            text = "[Earlier output omitted]\n" + text;
        await using var stream = OpenHandoffFile(file);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(text.AsMemory(), ct).ConfigureAwait(false);
        return file;
    }

    private static async Task<string> SaveChatAsync(
        SessionAppServerControlRuntimeService runtime,
        SessionInfoDto source,
        string directory,
        CancellationToken ct)
    {
        if (!runtime.IsAttached(source.Id) &&
            !await runtime.EnsureAttachedAsync(source.Id, source, ct: ct).ConfigureAwait(false))
            throw new InvalidOperationException("Agent Controller history is unavailable.");

        var window = await ReadWindowAsync(runtime, source.Id, null, PageSize, ct).ConfigureAwait(false);
        var threadId = window.Thread.ThreadId;
        var file = Path.Combine(directory, "chat-history.jsonl");
        await using (var stream = OpenHandoffFile(file))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 16_384))
        {
            while (true)
            {
                for (var index = window.History.Count - 1; index >= 0; index--)
                {
                    var entry = window.History[index];
                    if (!IsConversationEntry(entry))
                        continue;
                    var line = JsonSerializer.Serialize(entry, AppServerControlHostJsonContext.Default.AppServerControlHistoryItem);
                    await writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
                }

                var end = window.HistoryWindowStart;
                if (end == 0)
                    break;
                var start = Math.Max(0, end - PageSize);
                window = await ReadWindowAsync(runtime, source.Id, start, end - start, ct).ConfigureAwait(false);
                if (window.Thread.ThreadId != threadId || window.HistoryWindowStart != start || window.HistoryWindowEnd != end)
                    throw new InvalidOperationException("The source conversation changed during handoff.");
            }
        }
        return file;
    }

    private static FileStream OpenHandoffFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private static async Task<AppServerControlHistoryWindowResponse> ReadWindowAsync(
        SessionAppServerControlRuntimeService runtime, string sessionId, int? start, int count, CancellationToken ct) =>
        await runtime.GetHistoryWindowAsync(sessionId, start, count, updateCache: false, ct: ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException("The source conversation is unavailable.");

    private static bool IsConversationEntry(AppServerControlHistoryItem entry) =>
        entry.Kind is "user" or "assistant" or "plan" or "tool";
}
