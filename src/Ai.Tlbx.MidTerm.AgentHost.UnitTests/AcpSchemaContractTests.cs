using System.Text.Json;
using Xunit;

namespace Ai.Tlbx.MidTerm.AgentHost.UnitTests;

/// <summary>
/// Exhaustive contract tests against the pinned official Agent Client Protocol (ACP)
/// v1 and v2 stable and unstable JSON schemas under docs/acp-spec/.
/// Any new, renamed, or removed upstream method, capability, or session-update variant
/// fails these tests immediately so protocol drift cannot go unnoticed.
/// </summary>
public sealed class AcpSchemaContractTests
{
    private enum SupportStatus
    {
        Implemented,
        IntentionalOmit,
        PlannedCampaign2,
        PlannedCampaign3,
        PlannedCampaign4,
        PlannedCampaign5
    }

    private static string FindSpecRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "docs", "acp-spec");
            if (File.Exists(Path.Combine(candidate, "acp-spec-pin.json")))
            {
                return candidate;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate docs/acp-spec/acp-spec-pin.json from test output directory.");
    }

    private static JsonDocument LoadSpec(string relativePath)
    {
        var path = Path.Combine(FindSpecRoot(), relativePath);
        Assert.True(File.Exists(path), $"Missing pinned ACP spec artifact: {path}");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static HashSet<string> ReadMethodValues(JsonDocument meta, string section)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!meta.RootElement.TryGetProperty(section, out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        using var enumerator = obj.EnumerateObject();
        while (enumerator.MoveNext())
        {
            var prop = enumerator.Current;
            if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { Length: > 0 } method)
            {
                result.Add(method);
            }
        }

        return result;
    }

    private static HashSet<string> ReadUnionConsts(JsonDocument schema, string defName, string unionKeyword, string propertyName)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var def = schema.RootElement.GetProperty("$defs").GetProperty(defName);
        using var enumerator = def.GetProperty(unionKeyword).EnumerateArray();
        while (enumerator.MoveNext())
        {
            var variant = enumerator.Current;
            if (variant.TryGetProperty("properties", out var props) &&
                props.TryGetProperty(propertyName, out var field) &&
                field.TryGetProperty("const", out var constVal) &&
                constVal.ValueKind == JsonValueKind.String &&
                constVal.GetString() is { Length: > 0 } value)
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static HashSet<string> ReadObjectProperties(JsonDocument schema, string defName)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var props = schema.RootElement.GetProperty("$defs").GetProperty(defName).GetProperty("properties");
        using var enumerator = props.EnumerateObject();
        while (enumerator.MoveNext())
        {
            result.Add(enumerator.Current.Name);
        }

        return result;
    }

    private static void AssertExactMatrixCoverage(
        string label,
        IReadOnlySet<string> actual,
        IReadOnlyDictionary<string, SupportStatus> expected)
    {
        var missingFromMatrix = actual.Except(expected.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var staleInMatrix = expected.Keys.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Assert.True(
            missingFromMatrix.Count == 0 && staleInMatrix.Count == 0,
            $"{label} drift detected. Unclassified upstream items: [{string.Join(", ", missingFromMatrix)}]. Stale local entries: [{string.Join(", ", staleInMatrix)}].");
    }

    [Fact]
    public void PinnedSpecMetadataIsValidAndPresent()
    {
        using var pin = LoadSpec("acp-spec-pin.json");
        Assert.Equal("agentclientprotocol/agent-client-protocol", pin.RootElement.GetProperty("repository").GetString());
        Assert.StartsWith("schema-v1.", pin.RootElement.GetProperty("v1Tag").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("schema-v2.", pin.RootElement.GetProperty("v2Tag").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void V1AndV2StableAndUnstableMethodsMatchContractMatrix()
    {
        var v1AgentMethods = new Dictionary<string, SupportStatus>(StringComparer.Ordinal)
        {
            ["initialize"] = SupportStatus.Implemented,
            ["authenticate"] = SupportStatus.Implemented,
            ["logout"] = SupportStatus.Implemented,
            ["session/new"] = SupportStatus.Implemented,
            ["session/load"] = SupportStatus.Implemented,
            ["session/resume"] = SupportStatus.Implemented,
            ["session/list"] = SupportStatus.PlannedCampaign3,
            ["session/close"] = SupportStatus.PlannedCampaign3,
            ["session/delete"] = SupportStatus.PlannedCampaign3,
            ["session/set_mode"] = SupportStatus.Implemented,
            ["session/set_config_option"] = SupportStatus.Implemented,
            ["session/prompt"] = SupportStatus.Implemented,
            ["session/cancel"] = SupportStatus.Implemented,
            // Unstable
            ["providers/list"] = SupportStatus.PlannedCampaign5,
            ["providers/set"] = SupportStatus.PlannedCampaign5,
            ["providers/disable"] = SupportStatus.PlannedCampaign5,
            ["mcp/message"] = SupportStatus.PlannedCampaign5,
            ["session/fork"] = SupportStatus.PlannedCampaign5,
            ["session/archive"] = SupportStatus.PlannedCampaign5,
            ["session/unarchive"] = SupportStatus.PlannedCampaign5,
            ["session/status"] = SupportStatus.PlannedCampaign5,
            ["session/options"] = SupportStatus.PlannedCampaign5,
            ["session/set_title"] = SupportStatus.PlannedCampaign5,
            ["auth/status"] = SupportStatus.PlannedCampaign5
        };

        var v1ClientMethods = new Dictionary<string, SupportStatus>(StringComparer.Ordinal)
        {
            ["session/request_permission"] = SupportStatus.Implemented,
            ["session/update"] = SupportStatus.Implemented,
            ["fs/read_text_file"] = SupportStatus.IntentionalOmit,
            ["fs/write_text_file"] = SupportStatus.IntentionalOmit,
            ["terminal/create"] = SupportStatus.IntentionalOmit,
            ["terminal/output"] = SupportStatus.IntentionalOmit,
            ["terminal/release"] = SupportStatus.IntentionalOmit,
            ["terminal/wait_for_exit"] = SupportStatus.IntentionalOmit,
            ["terminal/kill"] = SupportStatus.IntentionalOmit,
            ["elicitation/create"] = SupportStatus.PlannedCampaign3,
            ["elicitation/complete"] = SupportStatus.PlannedCampaign3,
            // Unstable
            ["mcp/message"] = SupportStatus.PlannedCampaign5,
            ["auth/status_update"] = SupportStatus.PlannedCampaign5
        };

        var v2AgentMethods = new Dictionary<string, SupportStatus>(StringComparer.Ordinal)
        {
            ["initialize"] = SupportStatus.Implemented,
            ["auth/login"] = SupportStatus.Implemented,
            ["auth/logout"] = SupportStatus.Implemented,
            ["session/new"] = SupportStatus.Implemented,
            ["session/resume"] = SupportStatus.Implemented,
            ["session/list"] = SupportStatus.PlannedCampaign3,
            ["session/close"] = SupportStatus.PlannedCampaign3,
            ["session/delete"] = SupportStatus.PlannedCampaign3,
            ["session/set_config_option"] = SupportStatus.Implemented,
            ["session/prompt"] = SupportStatus.Implemented,
            ["session/cancel"] = SupportStatus.Implemented,
            ["tool_call/cancel"] = SupportStatus.Implemented,
            ["terminal/cancel"] = SupportStatus.Implemented,
            // Unstable
            ["providers/list"] = SupportStatus.PlannedCampaign5,
            ["providers/set"] = SupportStatus.PlannedCampaign5,
            ["providers/disable"] = SupportStatus.PlannedCampaign5,
            ["mcp/message"] = SupportStatus.PlannedCampaign5,
            ["session/fork"] = SupportStatus.PlannedCampaign5,
            ["session/archive"] = SupportStatus.PlannedCampaign5,
            ["session/unarchive"] = SupportStatus.PlannedCampaign5,
            ["session/status"] = SupportStatus.PlannedCampaign5,
            ["session/options"] = SupportStatus.PlannedCampaign5,
            ["session/set_title"] = SupportStatus.PlannedCampaign5,
            ["auth/status"] = SupportStatus.PlannedCampaign5
        };

        var v2ClientMethods = new Dictionary<string, SupportStatus>(StringComparer.Ordinal)
        {
            ["session/request_permission"] = SupportStatus.Implemented,
            ["session/update"] = SupportStatus.Implemented,
            ["elicitation/create"] = SupportStatus.PlannedCampaign3,
            ["elicitation/complete"] = SupportStatus.PlannedCampaign3,
            // Unstable
            ["mcp/message"] = SupportStatus.PlannedCampaign5,
            ["auth/status_update"] = SupportStatus.PlannedCampaign5
        };

        var protocolMethods = new Dictionary<string, SupportStatus>(StringComparer.Ordinal)
        {
            ["$/cancel_request"] = SupportStatus.Implemented
        };

        var proxyMethods = new Dictionary<string, SupportStatus>(StringComparer.Ordinal)
        {
            ["proxy/initialize"] = SupportStatus.IntentionalOmit,
            ["proxy/successor"] = SupportStatus.IntentionalOmit
        };

        using var v1MetaUnstable = LoadSpec(Path.Combine("v1", "meta.unstable.json"));
        using var v2MetaUnstable = LoadSpec(Path.Combine("v2", "meta.unstable.json"));

        AssertExactMatrixCoverage("v1 agentMethods", ReadMethodValues(v1MetaUnstable, "agentMethods"), v1AgentMethods);
        AssertExactMatrixCoverage("v1 clientMethods", ReadMethodValues(v1MetaUnstable, "clientMethods"), v1ClientMethods);
        AssertExactMatrixCoverage("v1 protocolMethods", ReadMethodValues(v1MetaUnstable, "protocolMethods"), protocolMethods);
        AssertExactMatrixCoverage("v1 proxyMethods", ReadMethodValues(v1MetaUnstable, "proxyMethods"), proxyMethods);

        AssertExactMatrixCoverage("v2 agentMethods", ReadMethodValues(v2MetaUnstable, "agentMethods"), v2AgentMethods);
        AssertExactMatrixCoverage("v2 clientMethods", ReadMethodValues(v2MetaUnstable, "clientMethods"), v2ClientMethods);
        AssertExactMatrixCoverage("v2 protocolMethods", ReadMethodValues(v2MetaUnstable, "protocolMethods"), protocolMethods);
        AssertExactMatrixCoverage("v2 proxyMethods", ReadMethodValues(v2MetaUnstable, "proxyMethods"), proxyMethods);
    }

    [Fact]
    public void V1AndV2SessionUpdatesAndContentBlocksMatchContractMatrix()
    {
        var v1Updates = new Dictionary<string, SupportStatus>(StringComparer.Ordinal)
        {
            ["user_message_chunk"] = SupportStatus.Implemented,
            ["agent_message_chunk"] = SupportStatus.Implemented,
            ["agent_thought_chunk"] = SupportStatus.Implemented,
            ["tool_call"] = SupportStatus.Implemented,
            ["tool_call_update"] = SupportStatus.Implemented,
            ["plan"] = SupportStatus.Implemented,
            ["plan_update"] = SupportStatus.Implemented,
            ["plan_removed"] = SupportStatus.Implemented,
            ["available_commands_update"] = SupportStatus.Implemented,
            ["current_mode_update"] = SupportStatus.Implemented,
            ["config_option_update"] = SupportStatus.Implemented,
            ["session_info_update"] = SupportStatus.PlannedCampaign3,
            ["usage_update"] = SupportStatus.Implemented,
            ["notice"] = SupportStatus.Implemented,
            ["compaction_update"] = SupportStatus.Implemented,
            ["compaction_summary_chunk"] = SupportStatus.Implemented,
            ["prompt_suggestions_update"] = SupportStatus.PlannedCampaign5,
            ["cursor_update"] = SupportStatus.PlannedCampaign5,
            ["subagent_update"] = SupportStatus.PlannedCampaign4,
            ["session_message"] = SupportStatus.PlannedCampaign4,
            ["session_message_chunk"] = SupportStatus.PlannedCampaign4
        };

        var v2Updates = new Dictionary<string, SupportStatus>(StringComparer.Ordinal)
        {
            ["user_message_chunk"] = SupportStatus.Implemented,
            ["user_message"] = SupportStatus.Implemented,
            ["agent_message_chunk"] = SupportStatus.Implemented,
            ["agent_message"] = SupportStatus.Implemented,
            ["agent_thought_chunk"] = SupportStatus.Implemented,
            ["agent_thought"] = SupportStatus.Implemented,
            ["state_update"] = SupportStatus.Implemented,
            ["tool_call_content_chunk"] = SupportStatus.Implemented,
            ["tool_call_update"] = SupportStatus.Implemented,
            ["terminal_update"] = SupportStatus.Implemented,
            ["terminal_output_chunk"] = SupportStatus.Implemented,
            ["plan_update"] = SupportStatus.Implemented,
            ["plan_removed"] = SupportStatus.Implemented,
            ["available_commands_update"] = SupportStatus.Implemented,
            ["config_option_update"] = SupportStatus.Implemented,
            ["session_info_update"] = SupportStatus.PlannedCampaign3,
            ["usage_update"] = SupportStatus.Implemented,
            ["notice"] = SupportStatus.Implemented,
            ["compaction_update"] = SupportStatus.Implemented,
            ["compaction_summary_chunk"] = SupportStatus.Implemented,
            ["replay_complete"] = SupportStatus.Implemented,
            ["prompt_suggestions_update"] = SupportStatus.PlannedCampaign5,
            ["cursor_update"] = SupportStatus.PlannedCampaign5,
            ["subagent_update"] = SupportStatus.PlannedCampaign4,
            ["session_message"] = SupportStatus.PlannedCampaign4,
            ["session_message_chunk"] = SupportStatus.PlannedCampaign4
        };

        var v1ContentBlocks = new Dictionary<string, SupportStatus>(StringComparer.Ordinal)
        {
            ["text"] = SupportStatus.Implemented,
            ["image"] = SupportStatus.Implemented,
            ["audio"] = SupportStatus.Implemented,
            ["resource_link"] = SupportStatus.Implemented,
            ["resource"] = SupportStatus.Implemented
        };

        var v2ContentBlocks = new Dictionary<string, SupportStatus>(v1ContentBlocks, StringComparer.Ordinal)
        {
            ["skill"] = SupportStatus.Implemented
        };

        var configCategories = new Dictionary<string, SupportStatus>(StringComparer.Ordinal)
        {
            ["mode"] = SupportStatus.Implemented,
            ["model"] = SupportStatus.Implemented,
            ["model_config"] = SupportStatus.Implemented,
            ["thought_level"] = SupportStatus.Implemented,
            ["speed"] = SupportStatus.PlannedCampaign5
        };

        var v2PlanVariants = new Dictionary<string, SupportStatus>(StringComparer.Ordinal)
        {
            ["items"] = SupportStatus.Implemented,
            ["markdown"] = SupportStatus.Implemented
        };

        var v2SessionCapabilities = new Dictionary<string, SupportStatus>(StringComparer.Ordinal)
        {
            ["prompt"] = SupportStatus.Implemented,
            ["mcp"] = SupportStatus.Implemented,
            ["delete"] = SupportStatus.PlannedCampaign3,
            ["additionalDirectories"] = SupportStatus.PlannedCampaign3,
            ["systemPrompt"] = SupportStatus.PlannedCampaign3,
            ["fork"] = SupportStatus.PlannedCampaign5,
            ["archive"] = SupportStatus.PlannedCampaign5,
            ["status"] = SupportStatus.PlannedCampaign5,
            ["options"] = SupportStatus.PlannedCampaign5,
            ["title"] = SupportStatus.PlannedCampaign5,
            ["_meta"] = SupportStatus.Implemented
        };

        using var v1Schema = LoadSpec(Path.Combine("v1", "schema.unstable.json"));
        using var v2Schema = LoadSpec(Path.Combine("v2", "schema.unstable.json"));

        AssertExactMatrixCoverage("v1 SessionUpdate", ReadUnionConsts(v1Schema, "SessionUpdate", "oneOf", "sessionUpdate"), v1Updates);
        AssertExactMatrixCoverage("v2 SessionUpdate", ReadUnionConsts(v2Schema, "SessionUpdate", "anyOf", "sessionUpdate"), v2Updates);
        AssertExactMatrixCoverage("v1 ContentBlock", ReadUnionConsts(v1Schema, "ContentBlock", "oneOf", "type"), v1ContentBlocks);
        AssertExactMatrixCoverage("v2 ContentBlock", ReadUnionConsts(v2Schema, "ContentBlock", "anyOf", "type"), v2ContentBlocks);
        AssertExactMatrixCoverage("v2 SessionConfigOptionCategory", ReadEnumOrConstAnyOf(v2Schema, "SessionConfigOptionCategory"), configCategories);
        AssertExactMatrixCoverage("v2 PlanUpdateContent", ReadUnionConsts(v2Schema, "PlanUpdateContent", "anyOf", "type"), v2PlanVariants);
        AssertExactMatrixCoverage("v2 SessionCapabilities", ReadObjectProperties(v2Schema, "SessionCapabilities"), v2SessionCapabilities);
    }

    private static HashSet<string> ReadEnumOrConstAnyOf(JsonDocument schema, string defName)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var def = schema.RootElement.GetProperty("$defs").GetProperty(defName);
        using var enumerator = def.GetProperty("anyOf").EnumerateArray();
        while (enumerator.MoveNext())
        {
            var variant = enumerator.Current;
            if (variant.TryGetProperty("const", out var constVal) &&
                constVal.ValueKind == JsonValueKind.String &&
                constVal.GetString() is { Length: > 0 } value)
            {
                result.Add(value);
            }
        }

        return result;
    }
}
