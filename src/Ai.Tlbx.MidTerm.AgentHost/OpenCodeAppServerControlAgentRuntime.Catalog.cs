using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class OpenCodeAppServerControlAgentRuntime
{
    private async Task LoadCatalogAsync(CancellationToken ct)
    {
        var providers = await SendAsync(HttpMethod.Get, "/provider", null, ct).ConfigureAwait(false);
        var agents = await SendAsync(HttpMethod.Get, "/agent", null, ct).ConfigureAwait(false);
        var commands = await SendAsync(HttpMethod.Get, "/command", null, ct).ConfigureAwait(false);
        var config = await SendAsync(HttpMethod.Get, "/config", null, ct).ConfigureAwait(false);
        var connected = Array(Value(providers, "connected")).Select(p => p.GetString()).ToHashSet(StringComparer.Ordinal);
        foreach (var provider in Array(Value(providers, "all")))
        {
            var id = Text(provider, "id");
            if (id is null || !connected.Contains(id)) continue;
            var models = Value(provider, "models");
            if (models.ValueKind != JsonValueKind.Object) continue;
            foreach (var property in Properties(models))
            {
                var model = property.Value;
                if (Text(model, "status") == "deprecated") continue;
                var key = id + "/" + (Text(model, "id") ?? property.Name);
                _models[key] = model.Clone();
                var variants = Variants(model);
                _settings.ModelOptions.Add(new() { Value = key, Label = Text(model, "name") ?? key, Description = Text(provider, "name"), EffortOptions = variants });
            }
        }
        if (_models.Count == 0) throw new InvalidOperationException("OpenCode has no models from a connected provider. Configure a provider in OpenCode first.");
        var configured = Text(config, "model");
        _settings.Model = configured is not null && _models.ContainsKey(configured) ? configured : _models.Count == 1 ? _models.Keys.Single() : null;
        // A catalogue's iteration order is not a native model preference. Do
        // not silently select a potentially expensive model from that order.
        _settings.EffortOptions = _settings.Model is null ? [] : Variants(_models[_settings.Model]);
        foreach (var agent in Array(agents))
        {
            var name = Text(agent, "name");
            if (!string.IsNullOrEmpty(name) && Value(agent, "hidden").ValueKind != JsonValueKind.True && Text(agent, "mode") != "subagent") _agents.Add(name);
        }
        var configuredAgent = Text(config, "default_agent");
        _agent = configuredAgent is not null && _agents.Contains(configuredAgent, StringComparer.Ordinal) ? configuredAgent : _agents.Contains("build", StringComparer.Ordinal) ? "build" : _agents.FirstOrDefault() ?? throw new InvalidOperationException("OpenCode has no primary agent.");
        _settings.PlanMode = _agent == "plan" ? "on" : "off";
        foreach (var command in Array(commands))
        {
            var name = Text(command, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            _availableCommands.Add(new() { Name = name, Description = Text(command, "description"), InputHint = Value(command, "hints").ValueKind == JsonValueKind.Array ? string.Join(" ", Array(Value(command, "hints")).Select(v => v.GetString())) : null });
        }
        if (!_availableCommands.Any(c => c.Name == "compact"))
            _availableCommands.Add(new() { Name = "compact", Description = "Compact this conversation using OpenCode's native summarizer." });
        UpdateAgentOption();
    }

    private static List<AppServerControlQuickSettingsOption> Variants(JsonElement model)
    {
        var variants = Value(model, "variants");
        return variants.ValueKind == JsonValueKind.Object
            ? Properties(variants).Where(p => Value(p.Value, "disabled").ValueKind != JsonValueKind.True).Select(p => new AppServerControlQuickSettingsOption { Value = p.Name, Label = p.Name }).ToList()
            : [];
    }

    private void ApplySettings(AppServerControlTurnRequest request)
    {
        var model = string.IsNullOrEmpty(request.Model) ? _settings.Model! : request.Model;
        if (string.IsNullOrEmpty(model)) throw new InvalidOperationException("Select a connected OpenCode model before starting a turn, or configure OpenCode's default model.");
        if (!_models.TryGetValue(model, out var definition)) throw new InvalidOperationException("The selected model is not in OpenCode's connected model catalogue.");
        var variants = Variants(definition);
        var effort = request.Effort ?? (model == _settings.Model ? _settings.Effort : null);
        if (!string.IsNullOrEmpty(effort) && !variants.Any(v => v.Value == effort)) throw new InvalidOperationException("The selected effort is not a native OpenCode model variant.");
        var agent = _agent;
        foreach (var pair in request.ConfigValues)
        {
            if (pair.Key != "agent" || pair.Value.ValueKind != JsonValueKind.String) throw new InvalidOperationException("Unknown OpenCode configuration option.");
            agent = pair.Value.GetString()!;
        }
        if (request.PlanMode == "on") agent = "plan";
        else if (request.PlanMode == "off" && agent == "plan") agent = "build";
        if (!_agents.Contains(agent, StringComparer.Ordinal)) throw new InvalidOperationException("The selected OpenCode agent is not available.");
        var permissions = request.PermissionMode ?? _settings.PermissionMode;
        if (permissions is not ("manual" or "auto")) throw new InvalidOperationException("Unknown OpenCode permission mode.");
        if (request.FastMode is not (null or "off")) throw new InvalidOperationException("OpenCode does not advertise a fast mode.");
        _settings.Model = model; _settings.Effort = effort; _settings.EffortOptions = variants;
        _settings.PermissionMode = permissions; _agent = agent; _settings.PlanMode = agent == "plan" ? "on" : "off";
        UpdateAgentOption();
    }

    private void UpdateAgentOption()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("id", "agent"); writer.WriteString("name", "Agent"); writer.WriteString("type", "select"); writer.WriteString("currentValue", _agent);
            writer.WriteStartArray("options");
            foreach (var agent in _agents) { writer.WriteStartObject(); writer.WriteString("value", agent); writer.WriteString("name", agent); writer.WriteEndObject(); }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
        _settings.ConfigOptions = [document.RootElement.Clone()];
    }

    private void WritePermissions(Utf8JsonWriter writer)
    {
        writer.WriteStartArray("permission");
        void Rule(string permission, string pattern, string action)
        {
            writer.WriteStartObject(); writer.WriteString("permission", permission); writer.WriteString("pattern", pattern); writer.WriteString("action", action); writer.WriteEndObject();
        }
        Rule("*", "*", _settings.PermissionMode == "auto" ? "allow" : "ask");
        if (_settings.PermissionMode != "auto")
        {
            Rule("read", "*", "allow"); Rule("read", "*.env", "ask"); Rule("read", "*.env.*", "ask"); Rule("read", "*.env.example", "allow");
            foreach (var permission in new[] { "glob", "grep", "lsp", "skill", "todowrite" }) Rule(permission, "*", "allow");
        }
        // Allow invoking the question tool, not answering it. Native questions
        // always remain pending until a user explicitly supplies an answer.
        Rule("question", "*", "allow");
        writer.WriteEndArray();
    }

    private (string Provider, string Model) SplitModel()
    {
        var value = _settings.Model ?? throw new InvalidOperationException("Select an OpenCode model first.");
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        return (value[..slash], value[(slash + 1)..]);
    }

    private void WriteModel(Utf8JsonWriter writer)
    {
        var (provider, model) = SplitModel();
        writer.WriteStartObject("model"); writer.WriteString("providerID", provider); writer.WriteString("modelID", model); writer.WriteEndObject();
    }
}
