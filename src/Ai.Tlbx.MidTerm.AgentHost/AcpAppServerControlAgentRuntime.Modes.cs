using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class AcpAppServerControlAgentRuntime
{
    private string _antigravityModeBeforePlan = "default";
    private volatile bool _antigravityFullAccess;

    private bool UsesAntigravityModes => Provider == "antigravity";

    private void ApplyAntigravityModeReadback(AppServerControlQuickSettingsSummary settings, string mode)
    {
        _currentModeId = mode;
        if (mode != "plan")
            _antigravityModeBeforePlan = mode;
        settings.PlanMode = mode == "plan" ? AppServerControlQuickSettings.PlanModeOn : AppServerControlQuickSettings.PlanModeOff;
        settings.PermissionMode = mode switch
        {
            "auto" => AppServerControlQuickSettings.PermissionModeAuto,
            "default" when _antigravityFullAccess => AppServerControlQuickSettings.PermissionModeFullAccess,
            _ => AppServerControlQuickSettings.PermissionModeManual
        };
    }

    private async Task ApplyAntigravityModeAsync(string? planMode, string? permissionMode, CancellationToken ct)
    {
        var requestedMode = planMode == "on" ? "plan" : permissionMode switch
        {
            "auto" => "auto",
            "manual" or AppServerControlQuickSettings.PermissionModeFullAccess => "default",
            null when planMode == "off" && _currentModeId == "plan" => _antigravityModeBeforePlan,
            _ => null
        };
        if (requestedMode is null)
            return;

        var option = FindNativeConfigOption("mode");
        if (!ReadConfigValueOptions(option).Any(candidate => candidate.Value == requestedMode))
            throw new InvalidOperationException($"{_agentName} does not advertise the '{requestedMode}' execution mode.");
        if (GetString(option, "currentValue") != requestedMode)
            await SetConfigOptionAsync("mode", requestedMode, ct).ConfigureAwait(false);
        if (GetString(FindNativeConfigOption("mode"), "currentValue") != requestedMode)
            throw new InvalidOperationException($"{_agentName} did not report the requested '{requestedMode}' execution mode.");

        // Like T3, keep agy in its supervised native mode for explicit Full access.
        // The ACP client answers the resulting one-time tool requests for this runtime.
        if (planMode != "on" && permissionMode is not null)
            _antigravityFullAccess = permissionMode == AppServerControlQuickSettings.PermissionModeFullAccess;
        var reportedPermission = _quickSettings.PermissionMode;
        ApplyAntigravityModeReadback(_quickSettings, requestedMode);
        if (_quickSettings.PermissionMode != reportedPermission)
            _emit(CreateQuickSettingsUpdatedEvent(_quickSettings, "midterm.appServerControl", "runtime.configure", null));
    }

    private async Task<HostCommandOutcome> ConfigureQuickSettingsAsync(
        AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        var request = command.Configure ?? throw new InvalidOperationException("Configure payload is required.");
        var response = new AppServerControlConfigureResponse
        {
            SessionId = command.SessionId,
            ThreadId = _providerThreadId!,
            TurnId = request.ExpectedTurnId,
            ThreadScope = "subsequentResponses",
            TurnScope = "subsequentResponses"
        };
        if (string.IsNullOrEmpty(request.ExpectedThreadId) ||
            request.ExpectedThreadId != _providerThreadId ||
            request.ExpectedTurnId != _activeTurnId)
        {
            response.Status = response.ThreadStatus = response.TurnStatus = "targetUnavailable";
            return ConfigurationOutcome(command, response);
        }

        // Configure model before effort because a model switch can replace the native effort catalogue.
        foreach (var field in new[] { "model", "effort", "permissionMode", "planMode" })
        {
            if (!request.Settings.TryGetValue(field, out var value)) continue;
            var setting = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            try
            {
                switch (field)
                {
                    case "model" when !string.IsNullOrWhiteSpace(setting):
                    case "effort" when !string.IsNullOrWhiteSpace(setting):
                        await ApplyRequestedQuickSettingsAsync(new AppServerControlQuickSettingsSummary
                        {
                            Model = field == "model" ? setting : _quickSettings.Model,
                            Effort = field == "effort" ? setting : _quickSettings.Effort,
                            PlanMode = _quickSettings.PlanMode
                        }, ct).ConfigureAwait(false);
                        if ((field == "model" ? _quickSettings.Model : _quickSettings.Effort) != setting)
                            throw new InvalidOperationException($"{_agentName} did not report the requested {field}.");
                        if (field == "model" && !_configOptionIds.ContainsKey("model"))
                            _emit(CreateQuickSettingsUpdatedEvent(_quickSettings, "acp", "session/set_model", null));
                        break;
                    case "permissionMode" when UsesAntigravityModes &&
                        setting is "manual" or "auto" or AppServerControlQuickSettings.PermissionModeFullAccess:
                        await ApplyAntigravityModeAsync(null, setting, ct).ConfigureAwait(false);
                        break;
                    case "permissionMode" when setting is "manual" or "auto":
                        _quickSettings.PermissionMode = setting;
                        _emit(CreateQuickSettingsUpdatedEvent(_quickSettings, "midterm.appServerControl", "runtime.configure", null));
                        break;
                    case "planMode" when UsesAntigravityModes && setting is "on" or "off":
                        await ApplyAntigravityModeAsync(setting, null, ct).ConfigureAwait(false);
                        break;
                    default:
                        response.UnconfirmedFields.Add(field);
                        break;
                }
            }
            catch (Exception error) when (error is InvalidOperationException or TimeoutException)
            {
                response.UnconfirmedFields.Add(field);
                response.FieldErrors[field] = error.Message;
            }
        }

        foreach (var field in request.Settings.Keys)
        {
            if (field is not ("model" or "effort" or "permissionMode" or "planMode"))
                response.UnconfirmedFields.Add(field);
        }
        var applied = response.UnconfirmedFields.Count < request.Settings.Count;
        response.ThreadStatus = applied ? "applied" : "unconfirmed";
        response.TurnStatus = request.ExpectedTurnId is null ? "notRequested" :
            request.ExpectedTurnId == _activeTurnId ? response.ThreadStatus : "targetUnavailable";
        response.Status = !applied ? "unconfirmed" :
            response.UnconfirmedFields.Count == 0 && response.TurnStatus != "targetUnavailable" ? "applied" : "partial";
        foreach (var (field, effective) in new (string, string?)[]
        {
            ("model", _quickSettings.Model), ("effort", _quickSettings.Effort),
            ("planMode", _quickSettings.PlanMode), ("permissionMode", _quickSettings.PermissionMode)
        })
        {
            if (!response.UnconfirmedFields.Contains(field))
                response.EffectiveSettings[field] = JsonSerializer.SerializeToElement(effective, AppServerControlHostJsonContext.Default.String);
        }
        return ConfigurationOutcome(command, response);
    }

    private static HostCommandOutcome ConfigurationOutcome(
        AppServerControlHostCommandEnvelope command, AppServerControlConfigureResponse response) =>
        new() { Result = new() { CommandId = command.CommandId, SessionId = command.SessionId, Status = "accepted", Configured = response } };
}
