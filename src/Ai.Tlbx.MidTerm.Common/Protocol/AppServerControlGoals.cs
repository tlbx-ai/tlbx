using System.Text.Json;

namespace Ai.Tlbx.MidTerm.Common.Protocol;

public sealed class AppServerControlGoalSummary
{
    public string Objective { get; set; } = string.Empty;
    public string Status { get; set; } = "active";
    public long? TokenBudget { get; set; }
    public long? TokensUsed { get; set; }
    public long? TimeUsedSeconds { get; set; }
    public long? CreatedAt { get; set; }
    public long? UpdatedAt { get; set; }
    public int? Iterations { get; set; }
    public string? LastReason { get; set; }
    public AppServerControlGoalSummary Clone() => (AppServerControlGoalSummary)MemberwiseClone();
}

public sealed class AppServerControlGoalState
{
    public string ThreadId { get; set; } = string.Empty;
    public string Confirmation { get; set; } = "unknown";
    public string Scope { get; set; } = "thread";
    public string? PendingOperation { get; set; }
    public string? Detail { get; set; }
    public AppServerControlGoalSummary? Goal { get; set; }
    public AppServerControlGoalState Clone() => new()
    {
        ThreadId = ThreadId, Confirmation = Confirmation, Scope = Scope,
        PendingOperation = PendingOperation, Detail = Detail, Goal = Goal?.Clone()
    };
}

public sealed class AppServerControlGoalResponse
{
    public string SessionId { get; set; } = string.Empty;
    public string ThreadId { get; set; } = string.Empty;
    public string? TurnId { get; set; }
    public string Status { get; set; } = "unconfirmed";
    public AppServerControlGoalState? State { get; set; }
}

public static class AppServerControlGoals
{
    public static bool Matches(AppServerControlGoalSetRequest request, string? threadId, string? turnId) =>
        !string.IsNullOrWhiteSpace(request.ExpectedThreadId) &&
        string.Equals(request.ExpectedThreadId, threadId, StringComparison.Ordinal) &&
        string.Equals(request.ExpectedTurnId, turnId, StringComparison.Ordinal);

    public static void Validate(AppServerControlGoalSetRequest request)
    {
        if (request.Operation is not ("set" or "get" or "clear"))
            throw new InvalidOperationException("Unknown goal operation.");
        if (request.Operation != "set") return;
        if (request.Objective is not null && string.IsNullOrWhiteSpace(request.Objective))
            throw new InvalidOperationException("Goal objective must not be empty.");
        if (request.Status is not null && request.Status is not ("active" or "paused" or "blocked" or "usageLimited" or "budgetLimited" or "complete"))
            throw new InvalidOperationException("Unknown native goal status.");
        var budget = request.TokenBudget;
        if (budget.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) &&
            (budget.ValueKind != JsonValueKind.Number || !budget.TryGetInt64(out var value) || value < 0))
            throw new InvalidOperationException("Goal token budget must be a non-negative integer or null.");
        if (request.Objective is null && request.Status is null && request.TokenBudget.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("A goal change is required.");
    }
}
