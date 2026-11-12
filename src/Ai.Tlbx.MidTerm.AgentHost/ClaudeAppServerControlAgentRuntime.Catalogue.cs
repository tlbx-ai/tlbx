using System.Globalization;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class ClaudeAppServerControlAgentRuntime
{
    private readonly List<JsonElement> _nativeModels = [];
    private List<AppServerControlCommand> _nativeCommands = [];
    private readonly HashSet<string> _terminalCommands = new(StringComparer.Ordinal);

    private void ApplyClaudeNativeCatalogue(JsonElement root, string rawLine)
    {
        if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            _nativeModels.Clear();
            using var enumerator = models.EnumerateArray();
            foreach (var model in enumerator)
                if (model.ValueKind == JsonValueKind.Object) _nativeModels.Add(model.Clone());
            _quickSettings.ModelOptions = _nativeModels
                .Where(model => !string.IsNullOrWhiteSpace(GetString(model, "value")))
                .Select(model => new AppServerControlQuickSettingsOption
                {
                    Value = GetString(model, "resolvedModel") ?? GetString(model, "value")!,
                    Label = GetString(model, "displayName") ?? GetString(model, "value")!,
                    Description = GetString(model, "description"),
                    IsDefault = GetString(model, "value") == "default",
                    SupportsFastMode = GetBoolean(model, "supportsFastMode"),
                    EffortOptions = ReadClaudeEffortOptions(GetString(model, "value"))
                }).DistinctBy(option => option.Value, StringComparer.Ordinal).ToList();
            _quickSettings.EffortOptions = ReadClaudeEffortOptions(_quickSettings.Model);
        }

        if (root.TryGetProperty("commands", out var commands) && commands.ValueKind == JsonValueKind.Array)
        {
            var byName = new Dictionary<string, AppServerControlCommand>(StringComparer.Ordinal);
            using var enumerator = commands.EnumerateArray();
            foreach (var command in enumerator)
            {
                if (command.ValueKind != JsonValueKind.Object) continue;
                var name = GetString(command, "name")?.Trim().TrimStart('/');
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!byName.ContainsKey(name) || GetBoolean(command, "builtin"))
                    byName[name] = new AppServerControlCommand
                    {
                        Name = name,
                        Description = GetString(command, "description"),
                        InputHint = GetString(command, "argumentHint")
                    };
            }
            _nativeCommands = byName.Values.ToList();
        }

        EmitClaudeCatalogue(root, rawLine);
    }

    private bool SupportsClaudeFastMode(string? model)
    {
        return _nativeModels.Any(candidate =>
            (string.Equals(GetString(candidate, "value"), model ?? "default", StringComparison.Ordinal) ||
             string.Equals(GetString(candidate, "resolvedModel"), model, StringComparison.Ordinal)) &&
            GetBoolean(candidate, "supportsFastMode"));
    }

    private List<AppServerControlQuickSettingsOption> ReadClaudeEffortOptions(string? model)
    {
        var selected = _nativeModels.FirstOrDefault(candidate =>
            string.Equals(GetString(candidate, "value"), model ?? "default", StringComparison.Ordinal) ||
            (model is not null && string.Equals(GetString(candidate, "resolvedModel"), model, StringComparison.Ordinal)));
        if (selected.ValueKind != JsonValueKind.Object ||
            !selected.TryGetProperty("supportedEffortLevels", out var efforts) || efforts.ValueKind != JsonValueKind.Array)
            return [];

        var options = new List<AppServerControlQuickSettingsOption>();
        using var enumerator = efforts.EnumerateArray();
        foreach (var effort in enumerator)
        {
            if (effort.ValueKind == JsonValueKind.String && effort.GetString() is { Length: > 0 } level)
                options.Add(new AppServerControlQuickSettingsOption
                {
                    Value = level,
                    Label = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(level)
                });
        }
        return options;
    }

    private void HandleClaudeSystemMessage(JsonElement root, string rawLine)
    {
        if (_manualCompactionTurn && GetString(root, "subtype") == "status" && GetString(root, "compact_result") == "failed")
            _compactionFailure = GetString(root, "compact_error") ?? "Claude could not compact the conversation.";
        if (GetString(root, "subtype") == "status" && GetString(root, "status") == "compacting" && _activeTurnId is not null)
        {
            if (!_turnStarted)
            {
                _turnStarted = true;
                _emit(CreateEvent("turn.started", _activeTurnId, null, null, "claude.agent-sdk", "status", root,
                    e => e.TurnStarted = new AppServerControlProviderTurnStartedPayload { Model = _activeTurnModel, Effort = _activeTurnEffort }, rawLine));
            }
            _emit(CreateEvent("session.state.changed", _activeTurnId, null, null, "claude.agent-sdk", "status", root,
                e => e.SessionState = new AppServerControlProviderSessionStatePayload
                {
                    State = "running", StateLabel = "Compacting", Reason = "Claude is compacting the conversation."
                }, rawLine));
            return;
        }
        if (GetString(root, "subtype") == "compact_boundary")
        {
            UpdateClaudeCompactionUsage(root, rawLine);
            EmitRuntimeMessage("runtime.notice", "Conversation compacted.", null);
            return;
        }
        if (GetString(root, "subtype") == "commands_changed")
        {
            ApplyClaudeNativeCatalogue(root, rawLine);
            return;
        }
        if (GetString(root, "subtype") != "init") return;
        if (root.TryGetProperty("terminal_slash_commands", out var terminal) && terminal.ValueKind == JsonValueKind.Array)
        {
            _terminalCommands.Clear();
            using var enumerator = terminal.EnumerateArray();
            foreach (var name in enumerator)
                if (name.ValueKind == JsonValueKind.String && name.GetString() is { } command)
                    _terminalCommands.Add(command.TrimStart('/'));
        }

        if (GetString(root, "model") is { Length: > 0 } model)
        {
            _quickSettings.Model = model;
            _activeTurnModel = model;
            _quickSettings.EffortOptions = ReadClaudeEffortOptions(model);
        }
        if (root.TryGetProperty("effort", out var effort) && effort.ValueKind is JsonValueKind.String or JsonValueKind.Null)
        {
            _quickSettings.Effort = effort.ValueKind == JsonValueKind.String ? effort.GetString() : null;
            _activeTurnEffort = _quickSettings.Effort;
        }
        var fastState = GetString(root, "fast_mode_state");
        if (fastState is "on" or "off" or "cooldown")
        {
            _quickSettings.FastMode = fastState is "on" or "cooldown" ? "on" : "off";
            _quickSettings.AcknowledgedFields.Remove("fastMode");
        }
        EmitClaudeCatalogue(root, rawLine);
    }

    private void EmitClaudeCatalogue(JsonElement root, string rawLine)
    {
        _emit(CreateEvent("agent.metadata.updated", _activeTurnId, null, null, "claude.agent-sdk", "catalogue", root,
            e =>
            {
                e.AgentMetadataUpdated = new AppServerControlProviderAgentMetadataPayload
                {
                    AgentName = "Claude",
                    AvailableCommands = AppServerControlCommand.CloneMany(_nativeCommands.Where(command => !_terminalCommands.Contains(command.Name)))
                };
                if (GetString(root, "sdkVersion") is { } version)
                    e.RuntimeNoticeOnly = new AppServerControlProviderRuntimeMessagePayload
                    {
                        Message = "Claude Agent SDK initialized.",
                        Detail = $"@anthropic-ai/claude-agent-sdk {version}"
                    };
            }, rawLine));
        _emit(CreateQuickSettingsUpdatedEvent(_quickSettings, "claude.agent-sdk", "catalogue", root));
    }
}
