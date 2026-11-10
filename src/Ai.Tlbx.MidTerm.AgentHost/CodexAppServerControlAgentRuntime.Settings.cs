using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class CodexAppServerControlAgentRuntime
{
    private TaskCompletionSource<JsonElement>? _settingsUpdated;
    private bool _hasConfiguredThreadDefaults;
    private static readonly HashSet<string> LiveSettings = new(StringComparer.Ordinal)
        { "model", "effort", "summary", "serviceTier", "approvalsReviewer" };
    private static readonly HashSet<string> ThreadSettings = new(StringComparer.Ordinal)
        { "model", "effort", "summary", "serviceTier", "approvalsReviewer", "collaborationMode",
          "approvalPolicy", "sandboxPolicy", "permissions" };

    private async Task<HostCommandOutcome> ConfigureAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        var request = command.Configure ?? throw new InvalidOperationException("Configure payload is required.");
        var response = new AppServerControlConfigureResponse
        {
            SessionId = command.SessionId, ThreadId = _providerThreadId!, TurnId = request.ExpectedTurnId
        };
        // Guard the observed idle state too: null never means "whatever is running now".
        if (string.IsNullOrEmpty(request.ExpectedThreadId) ||
            !string.Equals(request.ExpectedThreadId, _providerThreadId, StringComparison.Ordinal) ||
            !string.Equals(request.ExpectedTurnId, _activeTurnId, StringComparison.Ordinal))
        {
            response.Status = response.ThreadStatus = response.TurnStatus = "targetUnavailable";
            return Configured(command, response);
        }

        var patch = BuildSettingsPatch(request.Settings, _quickSettings);
        foreach (var key in patch.Keys.Where(key => !ThreadSettings.Contains(key)))
            response.UnconfirmedFields.Add(key);
        var threadPatch = patch.Where(pair => ThreadSettings.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (threadPatch.Count == 0)
        {
            response.Status = "unconfirmed";
            return Configured(command, response);
        }

        if (request.ExpectedTurnId is not null)
        {
            response.DeferredFields.AddRange(threadPatch.Keys.Where(key => !LiveSettings.Contains(key)));
            var livePatch = threadPatch.Where(pair => LiveSettings.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (livePatch.Count > 0)
            {
                try
                {
                    var result = await SendCodexRequestAsync("turn/settings/update",
                        id => BuildSettingsUpdateRequest(id, "turn/settings/update", request.ExpectedThreadId,
                            request.ExpectedTurnId, livePatch), ct).ConfigureAwait(false);
                    response.TurnStatus = GetString(result, "status") is "applied" ? "applied"
                        : GetString(result, "status") is "targetUnavailable" ? "targetUnavailable" : "unconfirmed";
                    if (response.TurnStatus == "applied") response.ConfirmedSettings = livePatch;
                }
                catch (Exception error) when (error is InvalidOperationException ||
                    error is OperationCanceledException && !ct.IsCancellationRequested && !_shutdown.IsCancellationRequested)
                {
                    response.TurnStatus = "unconfirmed";
                    response.FieldErrors["turn"] = error.Message;
                    response.Detail = error.Message;
                }
            }
        }

        var confirmation = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _settingsUpdated = confirmation;
        try
        {
            await SendCodexRequestAsync("thread/settings/update",
                id => BuildSettingsUpdateRequest(id, "thread/settings/update", request.ExpectedThreadId, null, threadPatch), ct)
                .ConfigureAwait(false);
            // The RPC result is {}; only the scoped notification carries actual values.
            var actual = await confirmation.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            response.ThreadStatus = "applied";
            // Thread defaults do not mutate an inference in flight. Separately answer only
            // currently waiting typed MCP tool approvals under the acknowledged owned Auto scope.
            if (threadPatch.ContainsKey("approvalPolicy") && threadPatch.ContainsKey("sandboxPolicy") &&
                string.Equals(request.ExpectedThreadId, _providerThreadId, StringComparison.Ordinal) &&
                string.Equals(request.ExpectedTurnId, _activeTurnId, StringComparison.Ordinal)) _mcpTurnStartUnconfirmed = false;
            await ReleasePendingMcpApprovalsAsync(request.ExpectedThreadId, request.ExpectedTurnId).ConfigureAwait(false);
            _hasConfiguredThreadDefaults = true;
            using var actualProperties = actual.EnumerateObject();
            foreach (var property in actualProperties)
                response.EffectiveSettings[property.Name] = property.Value.Clone();
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException ||
            error is OperationCanceledException && !ct.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        {
            response.ThreadStatus = "unconfirmed";
            response.Detail = error.Message;
            response.UnconfirmedFields.AddRange(threadPatch.Keys);
        }
        finally { _settingsUpdated = null; }

        var anyApplied = response.ThreadStatus == "applied" || response.TurnStatus == "applied";
        response.Status = response.ThreadStatus == "applied" &&
            (response.TurnStatus is "applied" or "notRequested") && response.UnconfirmedFields.Count == 0 &&
            response.DeferredFields.Count == 0 ? "applied" : anyApplied ? "partial" : "unconfirmed";
        return Configured(command, response);
    }

    private static Dictionary<string, JsonElement> BuildSettingsPatch(
        Dictionary<string, JsonElement> settings, AppServerControlQuickSettingsSummary current)
    {
        var patch = new Dictionary<string, JsonElement>(settings, StringComparer.Ordinal);
        if (patch.Remove("fastMode", out var fast))
        {
            if (fast.ValueKind != JsonValueKind.Null && fast.GetString() is not ("on" or "off"))
                throw new InvalidOperationException("fastMode must be on, off or null.");
            if (patch.ContainsKey("serviceTier")) throw new InvalidOperationException("Use fastMode or serviceTier, not both.");
            patch["serviceTier"] = SettingsValue(writer => writer.WriteStringValue(fast.ValueKind == JsonValueKind.Null ? null :
                fast.GetString() == "on" ? "fast" : null));
        }
        if (patch.Remove("permissionMode", out var permission))
        {
            if (patch.ContainsKey("approvalPolicy") || patch.ContainsKey("sandboxPolicy") || patch.ContainsKey("permissions"))
                throw new InvalidOperationException("Use permissionMode or native permission settings, not both.");
            var mode = permission.GetString();
            if (mode is not ("auto" or "manual")) throw new InvalidOperationException("permissionMode must be auto or manual.");
            patch["approvalPolicy"] = SettingsValue(writer => writer.WriteStringValue(mode == "auto" ? "never" : "on-request"));
            patch["sandboxPolicy"] = SettingsValue(writer =>
            {
                writer.WriteStartObject(); writer.WriteString("type", mode == "auto" ? "dangerFullAccess" : "workspaceWrite");
                writer.WriteEndObject();
            });
        }
        if (patch.Remove("planMode", out var plan))
        {
            if (patch.ContainsKey("collaborationMode")) throw new InvalidOperationException("Use planMode or collaborationMode, not both.");
            if (plan.GetString() is not ("on" or "off")) throw new InvalidOperationException("planMode must be on or off.");
            var model = patch.TryGetValue("model", out var selected) && selected.ValueKind == JsonValueKind.String
                ? selected.GetString() : current.Model;
            if (string.IsNullOrEmpty(model)) throw new InvalidOperationException("Select a model before changing plan mode.");
            var effort = patch.TryGetValue("effort", out var reasoning) ? reasoning : SettingsValue(writer => writer.WriteStringValue(current.Effort));
            patch["collaborationMode"] = SettingsValue(writer =>
            {
                writer.WriteStartObject(); writer.WriteString("mode", plan.GetString() == "on" ? "plan" : "default");
                writer.WriteStartObject("settings"); writer.WriteString("model", model);
                writer.WritePropertyName("reasoning_effort"); effort.WriteTo(writer);
                writer.WriteNull("developer_instructions"); writer.WriteEndObject(); writer.WriteEndObject();
            });
        }
        return patch;
    }

    private static string BuildSettingsUpdateRequest(string id, string method, string threadId, string? turnId,
        Dictionary<string, JsonElement> patch) => BuildJsonString(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0"); writer.WriteString("id", id); writer.WriteString("method", method);
        writer.WriteStartObject("params"); writer.WriteString("threadId", threadId);
        if (turnId is not null) writer.WriteString("turnId", turnId);
        foreach (var (key, value) in patch)
        {
            writer.WritePropertyName(key); value.WriteTo(writer);
        }
        writer.WriteEndObject(); writer.WriteEndObject();
    });

    private string PreserveConfiguredDefaults(string turnStartJson, AppServerControlTurnRequest request)
    {
        if (!_hasConfiguredThreadDefaults) return turnStartJson;
        using var document = JsonDocument.Parse(turnStartJson);
        return BuildJsonString(writer =>
        {
            writer.WriteStartObject();
            using var properties = document.RootElement.EnumerateObject();
            foreach (var property in properties)
            {
                if (property.Name != "params") { property.WriteTo(writer); continue; }
                writer.WriteStartObject("params");
                using var parameters = property.Value.EnumerateObject();
                foreach (var parameter in parameters)
                {
                    // Native defaults can be richer than the quick-settings aliases. Do not flatten them.
                    if ((parameter.Name is "approvalPolicy" or "sandboxPolicy") && request.PermissionMode is null) continue;
                    if (parameter.Name == "collaborationMode" && request.PlanMode is null && request.Model is null && request.Effort is null) continue;
                    if (parameter.Name == "serviceTier" && request.FastMode is null) continue;
                    if (parameter.Name == "model" && request.Model is null) continue;
                    if (parameter.Name == "effort" && request.Effort is null) continue;
                    parameter.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        });
    }

    private static JsonElement SettingsValue(Action<Utf8JsonWriter> write)
    {
        using var document = JsonDocument.Parse(BuildJsonString(write));
        return document.RootElement.Clone();
    }

    private void ApplyConfirmedThreadSettings(JsonElement actual)
    {
        if (actual.TryGetProperty("model", out var model)) _quickSettings.Model = model.GetString();
        if (actual.TryGetProperty("effort", out var effort)) _quickSettings.Effort = effort.GetString();
        if (actual.TryGetProperty("collaborationMode", out var collaboration))
            _quickSettings.PlanMode = GetString(collaboration, "mode") == "plan" ? "on" : "off";
        if (actual.TryGetProperty("serviceTier", out var tier))
            _quickSettings.FastMode = tier.GetString() == "fast" ? "on" : "off";
        if (actual.TryGetProperty("approvalPolicy", out var approval) &&
            actual.TryGetProperty("sandboxPolicy", out var sandbox))
            _quickSettings.PermissionMode = approval.ValueKind == JsonValueKind.String && approval.GetString() == "never" &&
                GetString(sandbox, "type") == "dangerFullAccess" ? "auto" : "manual";
    }

    private static HostCommandOutcome Configured(AppServerControlHostCommandEnvelope command, AppServerControlConfigureResponse response)
        => new() { Result = new AppServerControlHostCommandResultEnvelope
            { CommandId = command.CommandId, SessionId = command.SessionId, Status = "accepted", Configured = response } };
}
