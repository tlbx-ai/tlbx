using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Services.Mcp;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TlbxMcpInstallationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tlbx-mcp-install-" + Guid.NewGuid().ToString("N"));
    private static McpStdioOptions Options => new() { Origin = new Uri("https://localhost:2103/"), BaseUrl = "https://localhost:2103", CookieSeed = "fixture-secret", CertificateSha256 = new string('A', 64) };

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public void SetupPreservesOtherServersAndIsRepeatableWithPrivateCredentials(string client)
    {
        var workspace = Path.Combine(_root, "project with spaces ä");
        Directory.CreateDirectory(Path.Combine(workspace, ".codex"));
        var path = client == "codex" ? Path.Combine(workspace, ".codex", "config.toml") : Path.Combine(workspace, ".mcp.json");
        var initial = client == "codex" ? "model = \"example\"\n[mcp_servers.other]\ncommand = \"keep-me\"\n" : "{\"extra\":42,\"mcpServers\":{\"other\":{\"command\":\"keep-me\"}}}";
        File.WriteAllText(path, initial);
        var installed = TlbxMcpInstallation.Install(client, workspace, Path.Combine(_root, "private"), Options, "adapter path", []);
        Assert.Equal(path, installed);
        var first = File.ReadAllText(path);
        Assert.Contains("keep-me", first, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret", first, StringComparison.Ordinal);
        var credentialsPath = Directory.GetFiles(Path.Combine(_root, "private"), "credentials.json", SearchOption.AllDirectories).Single();
        var credentials = McpStdioOptions.ReadCredentialsFile(credentialsPath); // Also enforces actual ACL/mode, not a mocked permission.
        Assert.Equal("fixture-secret", credentials["MT_TOKEN"]);
        Assert.Equal(new string('A', 64), credentials["TLBX_MCP_CERT_SHA256"]);
        Assert.False(credentials.ContainsKey("TLBX_MCP_BOOTSTRAP"));
        TlbxMcpInstallation.Install(client, workspace, Path.Combine(_root, "private"), Options, "adapter path", []);
        Assert.Equal(first, File.ReadAllText(path));
        if (client == "codex")
        {
            Assert.Contains("required = true", first, StringComparison.Ordinal);
            Assert.Contains("env_vars = [\"CODEX_HOME\"]", first, StringComparison.Ordinal);
        }
        else Assert.Equal(42, JsonNode.Parse(first)!["extra"]!.GetValue<int>());
    }

    [Fact]
    public void GenericBootstrapPreservesClientConfigurationAndExportsLiteralArgumentsWithoutSecrets()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".codex"));
        var codexConfig = Path.Combine(_root, ".codex", "config.toml");
        var otherConfig = Path.Combine(_root, "opencode.json");
        File.WriteAllText(codexConfig, "existing codex configuration");
        File.WriteAllText(otherConfig, "existing other-client configuration");
        var descriptor = TlbxMcpInstallation.Bootstrap(_root, Path.Combine(_root, "private"), Options, "adapter with spaces", ["literal ä path.dll"]);
        Assert.Equal("existing codex configuration", File.ReadAllText(codexConfig));
        Assert.Equal("existing other-client configuration", File.ReadAllText(otherConfig));
        Assert.Equal("adapter with spaces", descriptor["command"]!.GetValue<string>());
        var arguments = descriptor["args"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "literal ä path.dll", "mcp", "--stdio", "--credentials-file" }, arguments[..^1], StringComparer.Ordinal);
        Assert.DoesNotContain("fixture-secret", descriptor.ToJsonString(), StringComparison.Ordinal);
        var credentials = McpStdioOptions.ReadCredentialsFile(arguments[^1]);
        Assert.Equal("fixture-secret", credentials["MT_TOKEN"]);
        Assert.Equal(Options.CertificateSha256, credentials["TLBX_MCP_CERT_SHA256"]);
        var again = TlbxMcpInstallation.Bootstrap(_root, Path.Combine(_root, "private"), Options, "adapter with spaces", ["literal ä path.dll"]);
        Assert.Equal(descriptor.ToJsonString(), again.ToJsonString());
    }

    [Theory]
    [InlineData("codex", "[mcp_servers.tlbx]\ncommand = \"custom\"\n")]
    [InlineData("codex", "[mcp_servers.\"tlbx\"]\ncommand = \"custom\"\n")]
    [InlineData("claude", "{\"mcpServers\":{\"tlbx\":{\"type\":\"http\",\"url\":\"https://custom/\"}}}")]
    public void CustomRegistrationIsPreservedWithoutWritingCredentials(string client, string content)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".codex"));
        var path = client == "codex" ? Path.Combine(_root, ".codex", "config.toml") : Path.Combine(_root, ".mcp.json");
        File.WriteAllText(path, content);
        Assert.Throws<InvalidOperationException>(() => TlbxMcpInstallation.Install(client, _root, Path.Combine(_root, "private"), Options, "adapter", []));
        Assert.Equal(content, File.ReadAllText(path));
        Assert.False(Directory.Exists(Path.Combine(_root, "private")));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task GeneratedSetupRunsWithoutCliAndPreservesOwningInstance(bool bash, bool otherInstance, bool generic)
    {
        Directory.CreateDirectory(_root);
        var shell = bash && OperatingSystem.IsWindows() ? @"C:\Program Files\Git\bin\bash.exe" : bash ? "/bin/sh" : "pwsh";
        var probe = Path.Combine(_root, "probe with spaces.ps1");
        var result = Path.Combine(_root, "result.json");
        File.WriteAllText(probe, "@{args=@($args);url=$env:MT_BASE_URL;token=$env:MT_TOKEN;pin=$env:TLBX_MCP_CERT_SHA256} | ConvertTo-Json -Compress | Set-Content -LiteralPath $env:TLBX_INSTALL_TEST_RESULT");
        var pwsh = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe") : "pwsh";
        TlbxMcpInstallation.WriteSetupScripts(_root, 2103, "fixture-secret", new string('A', 64), pwsh, ["-NoProfile", "-File", probe]);
        var start = new ProcessStartInfo(shell) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (!bash) { start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-File"); }
        start.ArgumentList.Add(Path.Combine(_root, bash ? "install-mcp.sh" : "install-mcp.ps1").Replace('\\', '/'));
        if (!generic) start.ArgumentList.Add("codex");
        start.Environment["TLBX_INSTALL_TEST_RESULT"] = result;
        start.Environment["MT_BASE_URL"] = otherInstance ? "https://localhost:2000" : "https://localhost:2103";
        start.Environment["MT_TOKEN"] = "inherited-token";
        start.Environment["TLBX_MCP_CERT_SHA256"] = "inherited-pin";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try { await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await output + await error);
        var observed = JsonNode.Parse(File.ReadAllText(result))!;
        Assert.Equal(otherInstance ? "https://localhost:2000" : "https://localhost:2103", observed["url"]!.GetValue<string>());
        Assert.Equal(otherInstance ? "inherited-token" : "fixture-secret", observed["token"]!.GetValue<string>());
        Assert.Equal(otherInstance ? "inherited-pin" : new string('A', 64), observed["pin"]!.GetValue<string>());
        Assert.Equal(generic ? new[] { "mcp", "bootstrap" } : ["mcp", "install", "codex"], observed["args"]!.AsArray().Select(value => value!.GetValue<string>()), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    [InlineData("generic")]
    public async Task NativeEntryPointInstallsWithoutReflectionMetadata(string client)
    {
        Directory.CreateDirectory(_root);
        var assembly = typeof(Ai.Tlbx.MidTerm.Services.TlbxDirectory).Assembly.Location;
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = _root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in client == "generic" ? new[] { assembly, "mcp", "bootstrap" } : [assembly, "mcp", "install", client]) start.ArgumentList.Add(argument);
        start.Environment["MT_BASE_URL"] = "https://localhost:2103";
        start.Environment["MT_TOKEN"] = "fixture-secret";
        start.Environment.Remove("MT_API_KEY");
        start.Environment.Remove("TLBX_MCP_BOOTSTRAP");
        start.Environment["TLBX_MCP_CERT_SHA256"] = new string('A', 64);
        // Keep test credentials isolated even though production stores them outside the project.
        start.Environment["LOCALAPPDATA"] = Path.Combine(_root, "local-app-data");
        start.Environment["XDG_DATA_HOME"] = Path.Combine(_root, "local-app-data");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try { await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await output + await error);
        JsonArray arguments;
        if (client == "generic")
        {
            var descriptor = JsonNode.Parse(await output)!;
            Assert.Equal("tlbx", descriptor["name"]!.GetValue<string>());
            Assert.Equal("stdio", descriptor["transport"]!.GetValue<string>());
            Assert.Equal("dotnet", Path.GetFileNameWithoutExtension(descriptor["command"]!.GetValue<string>()));
            Assert.Equal(30, descriptor["startupTimeoutSeconds"]!.GetValue<int>());
            Assert.Equal(660, descriptor["toolTimeoutSeconds"]!.GetValue<int>());
            Assert.DoesNotContain("fixture-secret", await output, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(_root, ".codex", "config.toml")));
            Assert.False(File.Exists(Path.Combine(_root, ".mcp.json")));
            arguments = descriptor["args"]!.AsArray();
        }
        else
        {
            var configPath = client == "codex" ? Path.Combine(_root, ".codex", "config.toml") : Path.Combine(_root, ".mcp.json");
            Assert.True(File.Exists(configPath));
            var config = File.ReadAllText(configPath);
            arguments = client == "claude" ? JsonNode.Parse(config)!["mcpServers"]!["tlbx"]!["args"]!.AsArray()
                : JsonNode.Parse(config.Split('\n').Single(line => line.StartsWith("args = ", StringComparison.Ordinal))["args = ".Length..])!.AsArray();
        }
        var credentialsPath = arguments.Last()!.GetValue<string>();
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_root + "\nhttps://localhost:2103")));
        Assert.EndsWith(Path.Combine(identity, "credentials.json"), credentialsPath, StringComparison.Ordinal);
        Assert.Equal("fixture-secret", McpStdioOptions.ReadCredentialsFile(credentialsPath)["MT_TOKEN"]);
        File.Delete(credentialsPath);
        Directory.Delete(Path.GetDirectoryName(credentialsPath)!);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
