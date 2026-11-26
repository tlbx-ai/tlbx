using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class AcpAppServerControlAgentRuntime
{
    private readonly Dictionary<string, AcpTerminalState> _nativeTerminals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AcpCompactionState> _nativeCompactions = new(StringComparer.Ordinal);

    private void HandleV2Terminal(string type, JsonElement update, JsonElement root, string raw, string? turnId)
    {
        var id = GetString(update, "terminalId");
        if (string.IsNullOrWhiteSpace(id)) return;
        if (!_nativeTerminals.TryGetValue(id, out var terminal))
        {
            terminal = new AcpTerminalState { TurnId = turnId ?? _activeTurnId ?? $"acp-background:{id}" };
            _nativeTerminals[id] = terminal;
        }
        if (type == "terminal_update")
        {
            if (update.TryGetProperty("command", out _)) terminal.Command = GetString(update, "command");
            if (update.TryGetProperty("output", out var output))
            {
                terminal.Decoder = Encoding.UTF8.GetDecoder();
                terminal.Output = new();
                if (output.ValueKind == JsonValueKind.Object) AppendTerminalBytes(terminal, GetString(output, "data"), raw);
            }
            if (update.TryGetProperty("exitStatus", out var exit))
            {
                terminal.Exited = exit.ValueKind == JsonValueKind.Object;
                terminal.ExitCode = exit.ValueKind == JsonValueKind.Object && exit.TryGetProperty("exitCode", out var code) &&
                    code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value) ? value : null;
                terminal.Signal = exit.ValueKind == JsonValueKind.Object ? GetString(exit, "signal") : null;
                if (terminal.Exited)
                {
                    terminal.CanCancel = false;
                }
            }
            if (update.TryGetProperty("capabilities", out var capabilities))
            {
                terminal.CanCancel = capabilities.ValueKind == JsonValueKind.Object &&
                    Traverse(capabilities, "cancel") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
            }
        }
        else AppendTerminalBytes(terminal, GetString(update, "data"), raw);

        var status = !terminal.Exited ? "in_progress"
            : terminal.ExitCode is not (null or 0) || terminal.Signal is not null ? "failed" : "completed";
        var presentation = new AppServerControlToolPresentation
        {
            Category = "command",
            Label = "Agent terminal",
            Subject = terminal.Command,
            ExitCode = terminal.ExitCode,
            Outcome = terminal.Signal ?? (terminal.Exited ? "Exited" : "Running")
        };
        terminal.Output.ApplyTo(presentation, status);
        _emit(CreateEvent("item.updated", terminal.TurnId, $"acp:terminal:{id}", null, "acp", type, root,
            e => e.Item = new AppServerControlProviderItemPayload
            {
                Replace = true,
                ItemType = "command_execution",
                Status = status,
                Title = terminal.Command ?? "Agent terminal",
                ToolPresentation = presentation
            }, raw));
    }

    private void AppendTerminalBytes(AcpTerminalState terminal, string? encoded, string raw)
    {
        if (encoded is null) return;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException)
        {
            EmitRuntimeMessage("runtime.notice", "Agent terminal supplied invalid base64 output.", raw, timelineVisible: false);
            return;
        }
        var chars = new char[4096];
        var remaining = bytes.AsSpan();
        while (!remaining.IsEmpty)
        {
            terminal.Decoder.Convert(remaining, chars, flush: false, out var usedBytes, out var usedChars, out _);
            terminal.Output.Append(new string(chars, 0, usedChars));
            remaining = remaining[usedBytes..];
        }
    }

    private void HandleV2Compaction(string type, JsonElement update, JsonElement root, string raw, string? turnId)
    {
        var id = GetString(update, "compactionId");
        if (string.IsNullOrWhiteSpace(id)) return;
        if (!_nativeCompactions.TryGetValue(id, out var compaction))
        {
            compaction = new AcpCompactionState { TurnId = turnId ?? _activeTurnId ?? $"acp-background:{id}" };
            _nativeCompactions[id] = compaction;
        }
        if (type == "compaction_update")
        {
            compaction.Status = GetString(update, "status") ?? compaction.Status;
            if (update.TryGetProperty("summary", out var summary)) compaction.Summary = ExtractText(summary);
            if (update.TryGetProperty("error", out _)) compaction.Error = GetString(update, "error");
        }
        else compaction.Summary += ExtractContentText(update);
        const int maxSummaryChars = 16384;
        if (compaction.Summary.Length > maxSummaryChars) compaction.Summary = compaction.Summary[^maxSummaryChars..];
        _emit(CreateEvent("item.updated", compaction.TurnId, $"acp:compaction:{id}", null, "acp", type, root,
            e => e.Item = new AppServerControlProviderItemPayload
            {
                Replace = true,
                ItemType = "context_compaction",
                Status = compaction.Status,
                Title = "Context compaction",
                ToolPresentation = new AppServerControlToolPresentation
                {
                    Category = "other",
                    Label = "Context compaction",
                    Subject = compaction.Error,
                    Evidence = compaction.Summary,
                    EvidenceKind = "text",
                    TotalLineCount = compaction.Summary.Length == 0 ? 0 : 1 + compaction.Summary.Count(character => character == '\n')
                }
            }, raw));
    }

    private sealed class AcpTerminalState
    {
        public string TurnId { get; init; } = string.Empty;
        public string? Command { get; set; }
        public bool Exited { get; set; }
        public bool CanCancel { get; set; }
        public int? ExitCode { get; set; }
        public string? Signal { get; set; }
        public Decoder Decoder { get; set; } = Encoding.UTF8.GetDecoder();
        public BoundedToolOutputAccumulator Output { get; set; } = new();
    }

    private sealed class AcpCompactionState
    {
        public string TurnId { get; init; } = string.Empty;
        public string Status { get; set; } = "in_progress";
        public string Summary { get; set; } = string.Empty;
        public string? Error { get; set; }
    }
}
