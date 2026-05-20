using System.Collections.Frozen;

namespace Ai.Tlbx.MidTerm.Services.Tmux.Commands;

/// <summary>
/// Handles: show-options, set-option (stubs returning reasonable defaults)
/// </summary>
public sealed class ConfigCommands
{
    private static readonly FrozenDictionary<string, string> DefaultOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["default-terminal"] = "tmux-256color",
        ["default-shell"] = "",
        ["escape-time"] = "500",
        ["history-limit"] = "2000",
        ["mouse"] = "on",
        ["status"] = "on",
        ["extended-keys"] = "on",
        ["extended-keys-format"] = "csi-u",
        ["xterm-keys"] = "on",
        ["base-index"] = "0",
        ["pane-base-index"] = "0",
        ["prefix"] = "C-b",
        ["mode-keys"] = "emacs",
        ["status-keys"] = "emacs",
        ["renumber-windows"] = "off",
        ["set-clipboard"] = "external",
        ["focus-events"] = "on",
        ["allow-passthrough"] = "on",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Return tmux option value(s). Returns reasonable defaults since MidTerm doesn't store tmux config.
    /// </summary>
    public TmuxResult ShowOptions(TmuxCommandParser.ParsedCommand cmd)
    {
        // If a specific option is requested
        if (cmd.Positional.Count > 0)
        {
            var optionName = cmd.Positional[0];
            if (DefaultOptions.TryGetValue(optionName, out var value))
            {
                return TmuxResult.Ok($"{value}\n");
            }
            return TmuxResult.Ok("\n");
        }

        // Show all options
        var sb = new System.Text.StringBuilder();
        foreach (var (key, value) in DefaultOptions)
        {
            sb.AppendLine($"{key} {value}");
        }
        return TmuxResult.Ok(sb.ToString());
    }

    /// <summary>
    /// Accept set-option silently. MidTerm doesn't support mutable tmux options.
    /// </summary>
    public TmuxResult SetOption(TmuxCommandParser.ParsedCommand cmd)
    {
        // Accept silently — MidTerm doesn't support tmux options
        return TmuxResult.Ok();
    }
}
