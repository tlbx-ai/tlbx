namespace Ai.Tlbx.MidTerm.Settings;

public sealed class ManagerBarButton
{
    private static readonly HashSet<string> ValidActionTypes = new(StringComparer.Ordinal) { "single", "chain" };
    private static readonly HashSet<string> ValidTriggerKinds = new(StringComparer.Ordinal)
    {
        "fireAndForget",
        "onCooldown",
        "repeatCount",
        "repeatInterval",
        "schedule"
    };

    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Text { get; set; } = "";
    public string ActionType { get; set; } = "single";
    public List<string> Prompts { get; set; } = [];
    public ManagerBarTrigger Trigger { get; set; } = new();

    public ManagerBarButton Normalize()
    {
        var normalizedLabel = (Label ?? string.Empty).Trim();
        var normalizedText = NormalizePrompt(Text);
        var normalizedPrompts = Prompts
            .Select(NormalizePrompt)
            .Where(prompt => !string.IsNullOrWhiteSpace(prompt))
            .ToList();

        if (normalizedPrompts.Count == 0 && !string.IsNullOrWhiteSpace(normalizedText))
        {
            normalizedPrompts.Add(normalizedText);
        }

        if (normalizedPrompts.Count == 0 && !string.IsNullOrWhiteSpace(normalizedLabel))
        {
            normalizedPrompts.Add(normalizedLabel);
        }

        if (normalizedPrompts.Count == 0)
        {
            normalizedPrompts.Add("command");
        }

        var normalizedActionType = ValidActionTypes.Contains(ActionType) ? ActionType : "single";
        if (normalizedActionType == "single")
        {
            normalizedPrompts = [normalizedPrompts[0]];
        }

        var normalizedTrigger = Trigger?.Normalize() ?? new ManagerBarTrigger();
        if (!ValidTriggerKinds.Contains(normalizedTrigger.Kind))
        {
            normalizedTrigger = new ManagerBarTrigger();
        }

        if (string.IsNullOrWhiteSpace(normalizedLabel))
        {
            normalizedLabel = BuildFallbackLabel(normalizedPrompts[0]);
        }

        return new ManagerBarButton
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim(),
            Label = normalizedLabel,
            Text = normalizedPrompts[0],
            ActionType = normalizedActionType,
            Prompts = normalizedPrompts,
            Trigger = normalizedTrigger
        };
    }

    public static List<ManagerBarButton> NormalizeList(IEnumerable<ManagerBarButton>? buttons)
    {
        return buttons?.Select(button => button.Normalize()).ToList() ?? [];
    }

    private static string NormalizePrompt(string? prompt)
    {
        return (prompt ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();
    }

    private static string BuildFallbackLabel(string prompt)
    {
        var firstLine = prompt
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(firstLine) ? "Action" : firstLine;
    }
}
