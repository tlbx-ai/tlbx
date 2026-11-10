using System.Globalization;

namespace Ai.Tlbx.MidTerm.Common.Protocol;

public static class AppServerControlProviderEventCompaction
{
    private const int MaxAssistantDeltaChars = 16384;
    private const int MaxReasoningDeltaChars = 1024;
    private const int MaxPlanDeltaChars = 4096;
    private const int MaxToolDeltaChars = 2048;
    private const int MaxDiffChars = 16384;
    private const int MaxCommandTextChars = 2048;
    private const int MaxToolItemDetailChars = 4096;
    private const int MaxRequestDetailChars = 2048;
    private const int MaxPendingMcpRequestDetailChars = 64 * 1024;
    private const int MaxRuntimeDetailChars = 4096;
    private const int MaxPlanMarkdownChars = 16384;

    public static AppServerControlProviderEvent CloneForRetention(AppServerControlProviderEvent source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new AppServerControlProviderEvent
        {
            Sequence = source.Sequence,
            EventId = source.EventId,
            SessionId = source.SessionId,
            Provider = source.Provider,
            ThreadId = source.ThreadId,
            TurnId = source.TurnId,
            ItemId = source.ItemId,
            RequestId = source.RequestId,
            CreatedAt = source.CreatedAt,
            Type = source.Type,
            Raw = CloneRawMetadata(source.Raw),
            AgentMetadataUpdated = source.AgentMetadataUpdated is null ? null : new AppServerControlProviderAgentMetadataPayload
            {
                AgentName = source.AgentMetadataUpdated.AgentName,
                Usage = source.AgentMetadataUpdated.Usage?.Clone(),
                AvailableCommands = source.AgentMetadataUpdated.AvailableCommands is null ? null : AppServerControlCommand.CloneMany(source.AgentMetadataUpdated.AvailableCommands)
            },
            SessionState = source.SessionState is null ? null : new AppServerControlProviderSessionStatePayload
            {
                State = source.SessionState.State,
                StateLabel = source.SessionState.StateLabel,
                Reason = source.SessionState.Reason
            },
            GoalUpdated = source.GoalUpdated?.Clone(),
            ThreadState = source.ThreadState is null ? null : new AppServerControlProviderThreadStatePayload
            {
                State = source.ThreadState.State,
                StateLabel = source.ThreadState.StateLabel,
                ProviderThreadId = source.ThreadState.ProviderThreadId
            },
            TurnStarted = source.TurnStarted is null ? null : new AppServerControlProviderTurnStartedPayload
            {
                IsReplay = source.TurnStarted.IsReplay,
                Model = source.TurnStarted.Model,
                Effort = source.TurnStarted.Effort
            },
            TurnCompleted = source.TurnCompleted is null ? null : new AppServerControlProviderTurnCompletedPayload
            {
                State = source.TurnCompleted.State,
                StateLabel = source.TurnCompleted.StateLabel,
                StopReason = source.TurnCompleted.StopReason,
                ErrorMessage = CompactTextMiddle(source.TurnCompleted.ErrorMessage, MaxRuntimeDetailChars)
            },
            ContentDelta = source.ContentDelta is null ? null : new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = source.ContentDelta.StreamKind,
                Delta = source.ContentDelta.Replace
                    ? CompactTextMiddle(source.ContentDelta.Delta, source.ContentDelta.StreamKind == "reasoning_text" ? 8192 : 262144)
                    : CompactContentDelta(source.ContentDelta.StreamKind, source.ContentDelta.Delta),
                Replace = source.ContentDelta.Replace
            },
            PlanDelta = source.PlanDelta is null ? null : new AppServerControlProviderPlanDeltaPayload
            {
                Delta = CompactTextMiddle(source.PlanDelta.Delta, MaxPlanDeltaChars) ?? string.Empty
            },
            PlanCompleted = source.PlanCompleted is null ? null : new AppServerControlProviderPlanCompletedPayload
            {
                IsProposal = source.PlanCompleted.IsProposal,
                PlanMarkdown = CompactTextMiddle(source.PlanCompleted.PlanMarkdown, MaxPlanMarkdownChars) ?? string.Empty
            },
            DiffUpdated = source.DiffUpdated is null ? null : new AppServerControlProviderDiffUpdatedPayload
            {
                UnifiedDiff = CompactTextMiddle(source.DiffUpdated.UnifiedDiff, MaxDiffChars) ?? string.Empty
            },
            Item = source.Item is null ? null : new AppServerControlProviderItemPayload
            {
                Replace = source.Item.Replace,
                ItemType = source.Item.ItemType,
                Status = source.Item.Status,
                Title = source.Item.Title,
                Detail = CompactItemDetail(source.Item.ItemType, source.Item.Detail),
                ToolPresentation = AppServerControlToolPresentationProtocol.Clone(source.Item.ToolPresentation),
                Attachments = CloneAttachments(source.Item.Attachments)
            },
            Task = source.Task is null ? null : new AppServerControlProviderTaskPayload
            {
                TaskId = source.Task.TaskId,
                Status = source.Task.Status,
                TaskType = source.Task.TaskType,
                Description = CompactTextMiddle(source.Task.Description, MaxRuntimeDetailChars),
                Summary = CompactTextMiddle(source.Task.Summary, MaxRuntimeDetailChars),
                LastToolName = source.Task.LastToolName,
                UsageJson = CompactTextMiddle(source.Task.UsageJson, MaxRuntimeDetailChars)
            },
            QuickSettingsUpdated = source.QuickSettingsUpdated is null ? null : new AppServerControlQuickSettingsPayload
            {
                Model = source.QuickSettingsUpdated.Model,
                Effort = source.QuickSettingsUpdated.Effort,
                PlanMode = AppServerControlQuickSettings.NormalizePlanMode(source.QuickSettingsUpdated.PlanMode),
                PermissionMode = AppServerControlQuickSettings.NormalizePermissionMode(source.QuickSettingsUpdated.PermissionMode),
                FastMode = AppServerControlQuickSettings.NormalizeFastMode(source.QuickSettingsUpdated.FastMode),
                ModelOptions = AppServerControlQuickSettings.CloneOptions(source.QuickSettingsUpdated.ModelOptions),
                EffortOptions = AppServerControlQuickSettings.CloneOptions(source.QuickSettingsUpdated.EffortOptions),
                AcknowledgedFields = [.. source.QuickSettingsUpdated.AcknowledgedFields],
                ConfigOptions = AppServerControlQuickSettings.CloneConfigOptions(source.QuickSettingsUpdated.ConfigOptions)
            },
            RequestOpened = source.RequestOpened is null ? null : new AppServerControlProviderRequestOpenedPayload
            {
                RequestType = source.RequestOpened.RequestType,
                RequestTypeLabel = source.RequestOpened.RequestTypeLabel,
                Detail = CompactRequestDetail(source.RequestOpened.Detail, source.RequestOpened.RequestType, pending: true)
            },
            RequestResolved = source.RequestResolved is null ? null : new AppServerControlProviderRequestResolvedPayload
            {
                RequestType = source.RequestResolved.RequestType,
                Decision = source.RequestResolved.Decision
            },
            UserInputRequested = source.UserInputRequested is null ? null : new AppServerControlProviderUserInputRequestedPayload
            {
                Questions = source.UserInputRequested.Questions.Select(CloneQuestion).ToList()
            },
            UserInputResolved = source.UserInputResolved is null ? null : new AppServerControlProviderUserInputResolvedPayload
            {
                Answers = source.UserInputResolved.Answers.Select(CloneAnsweredQuestion).ToList()
            },
            RuntimeMessage = source.RuntimeMessage is null ? null : new AppServerControlProviderRuntimeMessagePayload
            {
                Message = source.RuntimeMessage.Message,
                Detail = CompactTextMiddle(source.RuntimeMessage.Detail, MaxRuntimeDetailChars)
            },
            RuntimeNoticeOnly = source.RuntimeNoticeOnly is null ? null : new AppServerControlProviderRuntimeMessagePayload
            {
                Message = source.RuntimeNoticeOnly.Message,
                Detail = CompactTextMiddle(source.RuntimeNoticeOnly.Detail, MaxRuntimeDetailChars)
            }
        };
    }

    private static string CompactContentDelta(string streamKind, string value)
    {
        var normalized = (streamKind ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "assistant_text" => CompactTextMiddle(value, MaxAssistantDeltaChars),
            "reasoning_text" or "reasoning_summary_text" => CompactTextMiddle(value, MaxReasoningDeltaChars),
            "plan_text" => CompactTextMiddle(value, MaxPlanDeltaChars),
            "command_output" or "file_change_output" => CompactTextMiddle(value, MaxToolDeltaChars),
            _ when normalized.EndsWith("_output", StringComparison.Ordinal) ||
                   normalized.EndsWith("_result", StringComparison.Ordinal)
                => CompactTextMiddle(value, MaxToolDeltaChars),
            _ => value ?? string.Empty
        };
    }

    private static AppServerControlProviderEventRaw? CloneRawMetadata(AppServerControlProviderEventRaw? source)
    {
        if (source is null)
        {
            return null;
        }

        var raw = new AppServerControlProviderEventRaw
        {
            Source = source.Source,
            Method = source.Method,
            PayloadOmitted = source.PayloadOmitted
        };

        return string.IsNullOrWhiteSpace(raw.Source) && string.IsNullOrWhiteSpace(raw.Method) && !raw.PayloadOmitted
            ? null
            : raw;
    }

    private static string? CompactItemDetail(string itemType, string? detail)
    {
        var normalized = (itemType ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "command_execution" or "command" => CompactTextMiddle(detail, MaxCommandTextChars),
            "command_output" or "file_change_output" => CompactTextMiddle(detail, MaxToolItemDetailChars),
            _ when normalized.EndsWith("_output", StringComparison.Ordinal) ||
                   normalized.EndsWith("_result", StringComparison.Ordinal)
                => CompactTextMiddle(detail, MaxToolItemDetailChars),
            _ => detail
        };
    }

    public static string CompactRequestDetail(string? detail, string requestType, bool pending) =>
        CompactTextMiddle(detail, pending && requestType == "mcp_tool_approval" ? MaxPendingMcpRequestDetailChars : MaxRequestDetailChars);

    private static string CompactTextMiddle(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars || maxChars < 32)
        {
            return value ?? string.Empty;
        }

        var omissionCount = value.Length - maxChars;
        var marker = $" ... [{omissionCount.ToString(CultureInfo.InvariantCulture)} chars omitted] ... ";
        var available = maxChars - marker.Length;
        if (available < 8)
        {
            return value[..maxChars];
        }

        var headLength = available / 2;
        var tailLength = available - headLength;
        return string.Concat(value.AsSpan(0, headLength), marker, value.AsSpan(value.Length - tailLength));
    }

    private static List<AppServerControlAttachmentReference> CloneAttachments(IReadOnlyList<AppServerControlAttachmentReference>? attachments)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return [];
        }

        return attachments.Select(static attachment => new AppServerControlAttachmentReference
        {
            Kind = attachment.Kind,
            Path = attachment.Path,
            MimeType = attachment.MimeType,
            DisplayName = attachment.DisplayName
        }).ToList();
    }

    private static AppServerControlQuestion CloneQuestion(AppServerControlQuestion source)
    {
        return new AppServerControlQuestion
        {
            Id = source.Id,
            Header = source.Header,
            Question = source.Question,
            MultiSelect = source.MultiSelect,
            AllowCustomAnswer = source.AllowCustomAnswer,
            IsSecret = source.IsSecret,
            Options = source.Options.Select(static option => new AppServerControlQuestionOption
            {
                Label = option.Label,
                Value = option.Value,
                Description = option.Description
            }).ToList()
        };
    }

    private static AppServerControlAnsweredQuestion CloneAnsweredQuestion(AppServerControlAnsweredQuestion source)
    {
        return new AppServerControlAnsweredQuestion
        {
            QuestionId = source.QuestionId,
            Answers = [.. source.Answers]
        };
    }
}
