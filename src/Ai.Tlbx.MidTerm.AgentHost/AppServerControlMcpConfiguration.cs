using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

/// <summary>Writes native per-conversation MCP configuration; never edits provider user settings.</summary>
internal static class AppServerControlMcpConfiguration
{
    internal static void WriteCodex(Utf8JsonWriter writer, AppServerControlMcpServer? server)
    {
        if (server is null) return;
        Validate(server);
        writer.WritePropertyName("config");
        writer.WriteStartObject();
        writer.WritePropertyName("mcp_servers");
        writer.WriteStartObject();
        writer.WritePropertyName("tlbx");
        writer.WriteStartObject();
        WriteCommand(writer, server);
        writer.WriteNumber("startup_timeout_sec", 30);
        writer.WriteNumber("tool_timeout_sec", 660);
        writer.WritePropertyName("env");
        writer.WriteStartObject();
        foreach (var (name, value) in server.EnvironmentVariables) writer.WriteString(name, value);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    internal static void WriteAcp(Utf8JsonWriter writer, AppServerControlMcpServer? server, int protocolGeneration)
    {
        writer.WritePropertyName("mcpServers");
        writer.WriteStartArray();
        if (server is not null)
        {
            Validate(server);
            writer.WriteStartObject();
            if (protocolGeneration == 2) writer.WriteString("type", "stdio");
            writer.WriteString("name", "tlbx");
            WriteCommand(writer, server);
            writer.WritePropertyName("env");
            writer.WriteStartArray();
            foreach (var (name, value) in server.EnvironmentVariables)
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteString("value", value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    internal static void WriteClaude(Utf8JsonWriter writer, AppServerControlMcpServer? server)
    {
        if (server is null) return;
        Validate(server);
        writer.WritePropertyName("mcpServers");
        writer.WriteStartObject();
        writer.WritePropertyName("tlbx");
        writer.WriteStartObject();
        writer.WriteString("type", "stdio");
        WriteCommand(writer, server);
        writer.WriteNumber("timeout", 660_000);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteCommand(Utf8JsonWriter writer, AppServerControlMcpServer server)
    {
        writer.WriteString("command", server.Command);
        writer.WritePropertyName("args");
        writer.WriteStartArray();
        foreach (var argument in server.Arguments) writer.WriteStringValue(argument);
        writer.WriteEndArray();
    }

    private static void Validate(AppServerControlMcpServer server)
    {
        if (string.IsNullOrWhiteSpace(server.Command) || !Path.IsPathFullyQualified(server.Command))
            throw new InvalidOperationException("The trusted tlbx MCP executable must be an absolute path.");
    }
}
