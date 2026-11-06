using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using Ai.Tlbx.MidTerm.AgentHost;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.AgentHost.UnitTests;

public sealed class AppServerControlMcpConfigurationTests
{
    [Fact]
    public void CodexUsesPerThreadNativeConfigAndKeepsCredentialsOutOfArguments()
    {
        using var document = Build((writer, server) => AppServerControlMcpConfiguration.WriteCodex(writer, server));
        var config = document.RootElement.GetProperty("config").GetProperty("mcp_servers").GetProperty("tlbx");
        Assert.Equal(660, config.GetProperty("tool_timeout_sec").GetInt32());
        using var environment = config.GetProperty("env").EnumerateObject();
        Assert.Empty(environment);
        Assert.Equal("--credentials-file", config.GetProperty("args")[2].GetString());
        Assert.DoesNotContain("fixture-bootstrap", document.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AcpUsesSessionScopedStdioAndPrivateCredentialFile(int generation)
    {
        using var document = Build((writer, server) => AppServerControlMcpConfiguration.WriteAcp(writer, server, generation));
        using var servers = document.RootElement.GetProperty("mcpServers").EnumerateArray();
        var server = Assert.Single(servers);
        Assert.Equal("tlbx", server.GetProperty("name").GetString());
        Assert.Equal(generation == 2, server.TryGetProperty("type", out var type));
        if (generation == 2) Assert.Equal("stdio", type.GetString());
        using var environment = server.GetProperty("env").EnumerateArray();
        Assert.Empty(environment);
        Assert.Equal("--credentials-file", server.GetProperty("args")[2].GetString());
        Assert.DoesNotContain("fixture-bootstrap", document.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ClaudeUsesNativeSdkStdioConfigWithPrivateFileAndLongToolTimeout()
    {
        using var document = Build((writer, server) => AppServerControlMcpConfiguration.WriteClaude(writer, server));
        var server = document.RootElement.GetProperty("mcpServers").GetProperty("tlbx");
        Assert.Equal("stdio", server.GetProperty("type").GetString());
        Assert.Equal(660_000, server.GetProperty("timeout").GetInt32());
        Assert.Equal("--credentials-file", server.GetProperty("args")[2].GetString());
        Assert.False(server.TryGetProperty("env", out _));
        Assert.DoesNotContain("fixture-bootstrap", document.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnconfiguredRuntimeKeepsItsExistingNativeRequestShape()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            AppServerControlMcpConfiguration.WriteCodex(writer, null);
            AppServerControlMcpConfiguration.WriteAcp(writer, null, 2);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        Assert.False(document.RootElement.TryGetProperty("config", out _));
        using var servers = document.RootElement.GetProperty("mcpServers").EnumerateArray();
        Assert.Empty(servers);
    }

    [Fact]
    public void CredentialHandoffContainsPrivateValuesAndIsDeletedWithItsRuntime()
    {
        string path;
        using (var credentials = new AppServerControlMcpCredentials(new AppServerControlMcpServer
        {
            Command = Path.Combine(Path.GetTempPath(), "mt.exe"), Arguments = ["mcp", "--stdio"],
            EnvironmentVariables = new(StringComparer.Ordinal)
            {
                ["MT_TOKEN"] = "fixture-cookie", ["TLBX_MCP_BOOTSTRAP"] = "fixture-bootstrap"
            }
        }))
        {
            path = credentials.FilePath;
            using var file = JsonDocument.Parse(File.ReadAllBytes(path));
            Assert.Equal("fixture-cookie", file.RootElement.GetProperty("MT_TOKEN").GetString());
            Assert.Equal("fixture-bootstrap", file.RootElement.GetProperty("TLBX_MCP_BOOTSTRAP").GetString());
            Assert.Empty(credentials.Server.EnvironmentVariables);
            Assert.DoesNotContain("fixture-cookie", string.Join(' ', credentials.Server.Arguments), StringComparison.Ordinal);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            else
            {
                using var identity = WindowsIdentity.GetCurrent();
                var acl = new FileInfo(path).GetAccessControl();
                Assert.True(acl.AreAccessRulesProtected);
                var rule = Assert.Single(acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>());
                Assert.Equal(identity.User, rule.IdentityReference);
                Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            }
        }
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    private static JsonDocument Build(Action<Utf8JsonWriter, AppServerControlMcpServer> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            using var credentials = new AppServerControlMcpCredentials(new AppServerControlMcpServer
            {
                Command = Path.Combine(Path.GetTempPath(), "tlbx-contract", "mt.exe"),
                Arguments = ["mcp", "--stdio"],
                EnvironmentVariables = new(StringComparer.Ordinal) { ["TLBX_MCP_BOOTSTRAP"] = "fixture-bootstrap" }
            });
            write(writer, credentials.Server);
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
    }
}
