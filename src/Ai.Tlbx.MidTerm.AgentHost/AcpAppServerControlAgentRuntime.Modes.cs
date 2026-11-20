using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class AcpAppServerControlAgentRuntime
{
    private string _antigravityModeBeforePlan = "default";

    private bool UsesAntigravityModes => Provider == "antigravity";

    private void ApplyAntigravityModeReadback(AppServerControlQuickSettingsSummary settings, string mode)
    {
        _currentModeId = mode;
        if (mode != "plan")
            _antigravityModeBeforePlan = mode;
        settings.PlanMode = mode == "plan" ? AppServerControlQuickSettings.PlanModeOn : AppServerControlQuickSettings.PlanModeOff;
        settings.PermissionMode = mode == "auto"
            ? AppServerControlQuickSettings.PermissionModeAuto
            : AppServerControlQuickSettings.PermissionModeManual;
    }

    private async Task ApplyAntigravityModeAsync(string? planMode, string? permissionMode, CancellationToken ct)
    {
        var requestedMode = planMode == "on" ? "plan" : permissionMode switch
        {
            "auto" => "auto",
            "manual" => "default",
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
    }

    private async Task<HostCommandOutcome> ConfigureAntigravityModeAsync(
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

        string? plan = null;
        string? permission = null;
        foreach (var (field, value) in request.Settings)
        {
            var setting = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            switch (field)
            {
                case "planMode" when setting is "on" or "off":
                    plan = setting;
                    break;
                case "permissionMode" when setting is "manual" or "auto":
                    permission = setting;
                    break;
                default:
                    response.UnconfirmedFields.Add(field);
                    break;
            }
        }
        if (plan is null && permission is null)
        {
            response.Status = response.ThreadStatus = "unconfirmed";
            return ConfigurationOutcome(command, response);
        }

        try
        {
            await ApplyAntigravityModeAsync(plan, permission, ct).ConfigureAwait(false);
            response.ThreadStatus = "applied";
            response.TurnStatus = request.ExpectedTurnId is null ? "notRequested" :
                request.ExpectedTurnId == _activeTurnId ? "applied" : "targetUnavailable";
            response.Status = response.UnconfirmedFields.Count == 0 && response.TurnStatus != "targetUnavailable"
                ? "applied" : "partial";
            response.EffectiveSettings["planMode"] =
                JsonSerializer.SerializeToElement(_quickSettings.PlanMode, AppServerControlHostJsonContext.Default.String);
            response.EffectiveSettings["permissionMode"] =
                JsonSerializer.SerializeToElement(_quickSettings.PermissionMode, AppServerControlHostJsonContext.Default.String);
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException)
        {
            response.Status = response.ThreadStatus = "unconfirmed";
            response.Detail = error.Message;
        }
        return ConfigurationOutcome(command, response);
    }

    private static HostCommandOutcome ConfigurationOutcome(
        AppServerControlHostCommandEnvelope command, AppServerControlConfigureResponse response) =>
        new() { Result = new() { CommandId = command.CommandId, SessionId = command.SessionId, Status = "accepted", Configured = response } };
}
