using System.Text.Json;
using System.Text.RegularExpressions;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class ClaudeAppServerControlAgentRuntime
{
    private bool _replayingHistory;

    internal void ReplayClaudeHistory(JsonElement root)
    {
        if (Traverse(root, "messages") is not { ValueKind: JsonValueKind.Array } messages)
            return;

        _replayingHistory = true;
        try
        {
            ReplayClaudeHistoryEntries(messages);
        }
        finally
        {
            _replayingHistory = false;
            ResetTurnState();
        }
    }

    private void ReplayClaudeHistoryEntries(JsonElement messages)
    {
        JsonElement? previous = null;
        var expectingCompactionOutput = false;
        using var entries = messages.EnumerateArray();
        foreach (var entry in entries)
        {
            if (GetString(entry, "parent_tool_use_id") is not null || GetString(entry, "parent_agent_id") is not null)
                continue;
            if (IsCompletedLocalGoalRead(entry))
            {
                expectingCompactionOutput = false;
                continue;
            }
            var type = GetString(entry, "type");
            var entryLine = entry.GetRawText();
            var localText = ReadCompletedLocalCommandText(entry);
            if (expectingCompactionOutput && localText is not null &&
                CompletedLocalOutputRegex().Match(localText) is { Success: true } output)
            {
                var status = output.Groups[1].Value.Trim();
                if (status.Length > 0)
                    _emit(CreateEvent("runtime.notice", _activeTurnId, null, null, "claude.agent-sdk", "history", entry,
                        e => e.RuntimeMessage = new AppServerControlProviderRuntimeMessagePayload { Message = status }, entryLine));
                expectingCompactionOutput = false;
                previous = entry;
                continue;
            }
            expectingCompactionOutput = false;
            if (type == "user")
            {
                HandleUser(entry, entryLine);
                var text = ReadClaudeHistoryUserText(entry);
                if (localText is not null &&
                    CompletedLocalCompactionRegex().Match(localText) is { Success: true } compact)
                {
                    var arguments = compact.Groups[1].Value.Trim();
                    text = arguments.Length == 0 ? "/compact" : "/compact " + arguments;
                    expectingCompactionOutput = true;
                }
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (previous is { } last) CompleteClaudeHistoryTurn(last, last.GetRawText());
                    StartClaudeHistoryTurn(entry, text, entryLine);
                }
            }
            else if (type == "assistant" && _activeTurnId is not null)
            {
                HandleAssistant(entry, entryLine);
                ReplayClaudeHistoryTools(entry, entryLine);
            }
            previous = entry;
        }
        if (previous is { } final) CompleteClaudeHistoryTurn(final, final.GetRawText());
    }

    private static bool IsCompletedLocalGoalRead(JsonElement entry)
    {
        // The SDK derives this flag from the native local-command caveat/record
        // sequence. XML-looking user text alone is never evidence of a command.
        return ReadCompletedLocalCommandText(entry) is { } text && CompletedLocalGoalReadRegex().IsMatch(text);
    }

    private static string? ReadCompletedLocalCommandText(JsonElement entry)
    {
        return GetString(entry, "type") == "user"
            && GetBoolean(entry, "isCompletedLocalCommand")
            && !GetBoolean(entry, "is_error")
            && !GetBoolean(entry, "message", "is_error")
            && Traverse(entry, "message", "content") is { ValueKind: JsonValueKind.String } content
                ? content.GetString() : null;
    }

    [GeneratedRegex(@"\A\s*<command-name>/compact</command-name>\s*<command-message>compact</command-message>\s*<command-args>([\s\S]*)</command-args>\s*\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CompletedLocalCompactionRegex();

    [GeneratedRegex(@"\A\s*<local-command-stdout>([\s\S]*)</local-command-stdout>\s*\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CompletedLocalOutputRegex();

    [GeneratedRegex(@"\A\s*<command-name>/goal</command-name>\s*<command-message>goal</command-message>\s*<command-args>\s*</command-args>\s*\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CompletedLocalGoalReadRegex();

    private void StartClaudeHistoryTurn(JsonElement entry, string text, string rawLine)
    {
        var messageId = GetString(entry, "uuid");
        if (string.IsNullOrWhiteSpace(messageId)) return;
        _activeTurnId = $"claude-history:{messageId}";
        _emit(CreateEvent("turn.started", _activeTurnId, null, null, "claude.agent-sdk", "history", entry,
            e => e.TurnStarted = new AppServerControlProviderTurnStartedPayload { IsReplay = true }, rawLine));
        _emit(CreateEvent("item.completed", _activeTurnId, messageId, null, "claude.agent-sdk", "history", entry,
            e => e.Item = new AppServerControlProviderItemPayload
            {
                ItemType = "user_message",
                Status = "completed",
                Title = "User message",
                Detail = text
            }, rawLine));
    }

    private void CompleteClaudeHistoryTurn(JsonElement root, string rawLine)
    {
        if (_activeTurnId is not { } turnId) return;
        ResetTurnState();
        // The SDK history API exposes messages, not original turn outcomes.
        _emit(CreateEvent("turn.completed", turnId, null, null, "claude.agent-sdk", "history", root,
            e => e.TurnCompleted = new AppServerControlProviderTurnCompletedPayload
            {
                State = "completed",
                StateLabel = "Restored",
                StopReason = "history_import"
            }, rawLine));
    }

    private static string ReadClaudeHistoryUserText(JsonElement entry)
    {
        var content = Traverse(entry, "message", "content");
        if (content is { ValueKind: JsonValueKind.String } text) return text.GetString() ?? string.Empty;
        if (content is not { ValueKind: JsonValueKind.Array } blocks) return string.Empty;
        using var values = blocks.EnumerateArray();
        return string.Join("\n", values.Select(block => GetString(block, "type") switch
        {
            "text" => GetString(block, "text"),
            "image" => "[Image attachment]",
            _ => null
        }).Where(value => value is not null));
    }

    private void ReplayClaudeHistoryTools(JsonElement entry, string rawLine)
    {
        if (Traverse(entry, "message", "content") is not { ValueKind: JsonValueKind.Array } content) return;
        using var blocks = content.EnumerateArray();
        foreach (var block in blocks)
        {
            if (GetString(block, "type") != "tool_use" || GetString(block, "id") is not { } id) continue;
            var title = GetString(block, "name") ?? "Tool";
            var plan = title == "ExitPlanMode" ? GetString(block, "input", "plan") : null;
            var tool = new ClaudeToolState
            {
                ItemId = id,
                ItemType = NormalizeToolItemType(title),
                Title = title,
                IsPlanReview = !string.IsNullOrWhiteSpace(plan),
                Presentation = AppServerControlToolPresentationProjector.FromClaude(title, "in_progress", Traverse(block, "input"))
            };
            _tools[id] = tool;
            _emit(CreateEvent("item.started", _activeTurnId, id, null, "claude.agent-sdk", "history", entry,
                e => e.Item = new AppServerControlProviderItemPayload
                {
                    ItemType = tool.ItemType,
                    Status = "in_progress",
                    Title = title,
                    Detail = tool.Presentation.Subject,
                    ToolPresentation = AppServerControlToolPresentationProjector.Clone(tool.Presentation)
                }, rawLine));
            if (tool.IsPlanReview)
                _emit(CreateEvent("plan.completed", _activeTurnId, id, null, "claude.agent-sdk", "history", entry,
                    e => e.PlanCompleted = new AppServerControlProviderPlanCompletedPayload { PlanMarkdown = plan!, IsProposal = true }, rawLine));
        }
    }
}
