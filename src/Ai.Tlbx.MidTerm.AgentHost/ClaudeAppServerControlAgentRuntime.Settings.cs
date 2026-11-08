using System.Collections.Concurrent;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class ClaudeAppServerControlAgentRuntime
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _configurationAcks = new(StringComparer.Ordinal);

    private async Task<HostCommandOutcome> ConfigureAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        var request = command.Configure ?? throw new InvalidOperationException("Configure payload is required.");
        var response = new AppServerControlConfigureResponse
        {
            SessionId = command.SessionId, ThreadId = _providerThreadId!, TurnId = request.ExpectedTurnId,
            ThreadScope = "subsequentResponses", TurnScope = "subsequentResponses",
            Detail = "SDK controls acknowledge defaults, not effective-settings readback. Model/effort apply to subsequent responses and may be clamped. Auto with Plan off also releases scoped waiting tool approvals."
        };
        if (string.IsNullOrEmpty(request.ExpectedThreadId) ||
            !string.Equals(request.ExpectedThreadId, _providerThreadId, StringComparison.Ordinal) ||
            !string.Equals(request.ExpectedTurnId, _activeTurnId, StringComparison.Ordinal))
        {
            response.Status = response.ThreadStatus = response.TurnStatus = "targetUnavailable";
            return ConfigurationOutcome(command, response);
        }
        var settings = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in request.Settings)
        {
            if (key is "model" or "effort" or "planMode" or "permissionMode")
            {
                if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    throw new InvalidOperationException($"{key} must be a string or null.");
                if (key == "planMode" && value.GetString() is not ("on" or "off"))
                    throw new InvalidOperationException("planMode must be on or off.");
                if (key == "permissionMode" && value.GetString() is not ("auto" or "manual"))
                    throw new InvalidOperationException("permissionMode must be auto or manual.");
                settings[key] = value;
            }
            else response.UnconfirmedFields.Add(key);
        }
        if (settings.Count == 0)
        {
            response.Status = "unconfirmed";
            return ConfigurationOutcome(command, response);
        }
        var ack = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_configurationAcks.TryAdd(command.CommandId, ack))
            throw new InvalidOperationException("Configuration command is already pending.");
        try
        {
            await WriteBridgeCommandAsync(writer =>
            {
                writer.WriteStartObject(); writer.WriteString("type", "runtime.configure");
                writer.WriteString("requestId", command.CommandId);
                writer.WriteString("expectedThreadId", request.ExpectedThreadId);
                writer.WriteString("expectedTurnId", request.ExpectedTurnId); writer.WriteStartObject("settings");
                foreach (var (key, value) in settings) { writer.WritePropertyName(key); value.WriteTo(writer); }
                writer.WriteEndObject(); writer.WriteEndObject();
            }, ct).ConfigureAwait(false);
            var receipt = await ack.Task.WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (GetString(receipt, "status") == "targetUnavailable")
            {
                response.Status = response.ThreadStatus = response.TurnStatus = "targetUnavailable";
                return ConfigurationOutcome(command, response);
            }
            foreach (var property in receipt.GetProperty("confirmed").EnumerateObject())
                response.ConfirmedSettings[property.Name] = property.Value.Clone();
            foreach (var property in receipt.GetProperty("errors").EnumerateObject())
            {
                response.UnconfirmedFields.Add(property.Name);
                response.FieldErrors[property.Name] = property.Value.GetString() ?? "Provider rejected setting.";
            }
            if (!string.Equals(request.ExpectedThreadId, _providerThreadId, StringComparison.Ordinal))
            {
                response.Status = response.ThreadStatus = response.TurnStatus = "targetUnavailable";
                return ConfigurationOutcome(command, response);
            }
            response.ThreadStatus = response.ConfirmedSettings.Count > 0 ? "acknowledged" : "unconfirmed";
            response.TurnStatus = request.ExpectedTurnId is null ? "notRequested" :
                string.Equals(request.ExpectedTurnId, _activeTurnId, StringComparison.Ordinal)
                    ? response.ThreadStatus : "targetUnavailable";
            response.Status = response.UnconfirmedFields.Count == 0 && response.TurnStatus != "targetUnavailable"
                ? "acknowledged" : response.ConfirmedSettings.Count > 0 ? "partial" : "unconfirmed";
            // Preserve acknowledged defaults for subsequent turn.start without claiming readback.
            if (response.ConfirmedSettings.TryGetValue("model", out var model))
                _quickSettings.Model = model.GetString();
            if (response.ConfirmedSettings.TryGetValue("effort", out var effort))
                _quickSettings.Effort = effort.GetString();
            if (response.ConfirmedSettings.TryGetValue("planMode", out var plan)) _quickSettings.PlanMode = plan.GetString()!;
            if (response.ConfirmedSettings.TryGetValue("permissionMode", out var permission)) _quickSettings.PermissionMode = permission.GetString()!;
            if (response.ConfirmedSettings.Count > 0)
            {
                _quickSettings.AcknowledgedFields = _quickSettings.AcknowledgedFields.Union(response.ConfirmedSettings.Keys, StringComparer.Ordinal).ToList();
                _emit(CreateQuickSettingsUpdatedEvent(_quickSettings, "claude.agent-sdk", "runtime.configure/acknowledged-defaults", receipt));
            }
        }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException)
        {
            response.Status = response.ThreadStatus = "unconfirmed";
            response.UnconfirmedFields.AddRange(settings.Keys);
            response.Detail = error.Message;
        }
        finally { _configurationAcks.TryRemove(command.CommandId, out _); }
        // Canonical defaults carry AcknowledgedFields; EffectiveSettings stays empty.
        return ConfigurationOutcome(command, response);
    }

    private void HandlePendingPermissionSettlement(JsonElement root, string decision, string requestType = "tool_approval")
    {
        var requestId = GetString(root, "requestId");
        var turnId = GetString(root, "turnId");
        if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(turnId) ||
            !string.Equals(GetString(root, "session_id"), _providerThreadId, StringComparison.Ordinal) ||
            GetString(root, "decision") != decision) return;
        // This is an actual callback settlement, not an ACK to a host resolve command.
        // Keep its captured turn even if completion has since moved the active turn.
        _emit(CreateEvent("request.resolved", turnId, null, requestId, "claude.agent-sdk", "canUseTool/callback-settled", root,
            e => e.RequestResolved = new() { RequestType = requestType, Decision = decision }));
    }

    private static HostCommandOutcome ConfigurationOutcome(AppServerControlHostCommandEnvelope command, AppServerControlConfigureResponse response)
        => new() { Result = new AppServerControlHostCommandResultEnvelope
            { CommandId = command.CommandId, SessionId = command.SessionId, Status = "accepted", Configured = response } };
}
