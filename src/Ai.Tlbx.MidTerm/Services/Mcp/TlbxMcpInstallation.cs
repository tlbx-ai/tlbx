using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>Explicit, project-scoped setup for agents launched in an ordinary terminal.</summary>
internal static partial class TlbxMcpInstallation
{
    private const string Begin = "# BEGIN tlbx managed MCP";
    private const string End = "# END tlbx managed MCP";

    internal static int Run(string client)
    {
        if (client is not ("codex" or "claude" or "generic"))
            throw new ArgumentException("TLBX_MCP_INSTALL: Use mt mcp bootstrap for any client, or mt mcp install codex|claude for direct project setup.");
        var options = McpStdioOptions.Parse(["--stdio"]);
        var command = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the tlbx MCP adapter.");
        string[] prefix = string.Equals(Path.GetFileNameWithoutExtension(command), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? [Path.GetFullPath(Environment.GetCommandLineArgs()[0])] : [];
        var storage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tlbx", "mcp");
        if (client == "generic")
        {
            Console.WriteLine(Bootstrap(Environment.CurrentDirectory, storage, options, command, prefix).ToJsonString());
            return 0;
        }
        var path = Install(client, Environment.CurrentDirectory, storage, options, command, prefix);
        Console.WriteLine("Configured tlbx MCP for " + client + " in " + path);
        Console.WriteLine("Start a new agent session or resume this conversation after restarting the client. Trust the project / approve its MCP server if prompted.");
        Console.WriteLine("Then discover native context_get and call it. Configuration alone does not prove tools are exposed; a shell MCP probe is not model tool access.");
        return 0;
    }

    internal static string Install(string client, string workspace, string storage, McpStdioOptions options, string command, string[] prefix)
    {
        if (client is not ("codex" or "claude")) throw new ArgumentException("Unsupported MCP client.");
        workspace = Path.GetFullPath(workspace);
        var credentialsPath = GetCredentialsPath(workspace, storage, options);
        string[] arguments = [.. prefix, "mcp", "--stdio", "--credentials-file", credentialsPath];
        var configPath = client == "codex" ? Path.Combine(workspace, ".codex", "config.toml") : Path.Combine(workspace, ".mcp.json");
        var previous = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
        var content = client == "codex" ? CodexConfiguration(previous, command, arguments) : ClaudeConfiguration(previous, command, arguments, credentialsPath);
        // Validate existing configuration before writing any secrets or changing a client.
        WriteCredentials(Path.GetDirectoryName(credentialsPath)!, credentialsPath, options);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        WriteAtomically(configPath, content);
        return configPath;
    }

    internal static JsonObject Bootstrap(string workspace, string storage, McpStdioOptions options, string command, string[] prefix)
    {
        var credentialsPath = GetCredentialsPath(workspace, storage, options);
        WriteCredentials(Path.GetDirectoryName(credentialsPath)!, credentialsPath, options);
        var arguments = new JsonArray();
        foreach (var argument in prefix.Concat(["mcp", "--stdio", "--credentials-file", credentialsPath]))
            arguments.Add((JsonNode?)JsonValue.Create(argument));
        var environment = new JsonObject();
        if (Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } codexHome)
            environment["CODEX_HOME"] = codexHome;
        return new JsonObject
        {
            ["name"] = "tlbx", ["transport"] = "stdio", ["command"] = command,
            ["args"] = arguments, ["env"] = environment,
            ["startupTimeoutSeconds"] = 30, ["toolTimeoutSeconds"] = 660
        };
    }

    private static string GetCredentialsPath(string workspace, string storage, McpStdioOptions options)
    {
        if (!string.IsNullOrEmpty(options.BootstrapToken))
            throw new InvalidOperationException("TLBX_MCP_INSTALL: Install from an ordinary terminal; controller runtime credentials cannot be persisted.");
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(workspace) + "\n" + options.BaseUrl)));
        return Path.Combine(storage, identity, "credentials.json");
    }

    internal static string CodexConfiguration(string previous, string command, string[] arguments)
    {
        var begin = previous.IndexOf(Begin, StringComparison.Ordinal);
        if (begin >= 0)
        {
            var end = previous.IndexOf(End, begin, StringComparison.Ordinal);
            if (end < 0 || previous.IndexOf(Begin, begin + Begin.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException("TLBX_MCP_INSTALL: Incomplete managed MCP block; repair it before installing.");
            previous = previous.Remove(begin, end + End.Length - begin);
        }
        if (TlbxTable().IsMatch(previous))
            throw new InvalidOperationException("TLBX_MCP_INSTALL: This project's Codex config already defines tlbx. Preserve that configuration and resolve the existing entry before installing.");
        var block = Begin + "\n[mcp_servers.tlbx]\ncommand = " + Quote(command) + "\nargs = [" + string.Join(", ", arguments.Select(Quote)) + "]\n" +
            "required = true\nstartup_timeout_sec = 30\ntool_timeout_sec = 660\nenv_vars = [\"CODEX_HOME\"]\n" + End + "\n";
        return previous.TrimEnd('\r', '\n') + (previous.Length == 0 ? "" : "\n\n") + block;
    }

    private static string ClaudeConfiguration(string previous, string command, string[] arguments, string credentialsPath)
    {
        var root = previous.Length == 0 ? new JsonObject() : JsonNode.Parse(previous) as JsonObject
            ?? throw new InvalidOperationException("TLBX_MCP_INSTALL: Expected an MCP configuration object.");
        if (root["mcpServers"] is null) root["mcpServers"] = new JsonObject();
        if (root["mcpServers"] is not JsonObject servers) throw new InvalidOperationException("TLBX_MCP_INSTALL: Expected mcpServers to be an object.");
        if (servers["tlbx"] is { } existing && existing["args"] is not JsonArray)
            throw new InvalidOperationException("TLBX_MCP_INSTALL: Existing Claude tlbx entry is not managed by this installer.");
        if (servers["tlbx"] is { } configured && !configured["args"]!.AsArray().Any(value => value?.GetValue<string>() == credentialsPath))
            throw new InvalidOperationException("TLBX_MCP_INSTALL: Preserve the existing Claude tlbx entry; resolve it before installing.");
        var args = new JsonArray();
        foreach (var argument in arguments) args.Add((JsonNode?)JsonValue.Create(argument));
        servers["tlbx"] = new JsonObject { ["type"] = "stdio", ["command"] = command, ["args"] = args };
        return root.ToJsonString() + "\n";
    }

    private static void WriteCredentials(string directory, string path, McpStdioOptions options)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User ?? throw new InvalidOperationException("Cannot identify MCP credential owner.");
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.SetOwner(owner);
            acl.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            if (Directory.Exists(directory))
            {
                // Never narrow or replace another user's existing directory or follow a link.
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("TLBX_MCP_INSTALL: Credential directory must not be a link.");
                if (Directory.GetFileSystemEntries(directory).Length > 0) _ = McpStdioOptions.ReadCredentialsFile(path);
                new DirectoryInfo(directory).SetAccessControl(acl);
            }
            else new DirectoryInfo(directory).Create(acl);
        }
        else
        {
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("TLBX_MCP_INSTALL: Credential directory must not be a link.");
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["MT_BASE_URL"] = options.BaseUrl };
        if (!string.IsNullOrEmpty(options.ApiKey)) values["MT_API_KEY"] = options.ApiKey;
        else if (!string.IsNullOrEmpty(options.CookieSeed)) values["MT_TOKEN"] = options.CookieSeed;
        if (!string.IsNullOrEmpty(options.CertificateSha256)) values["TLBX_MCP_CERT_SHA256"] = options.CertificateSha256;
        // Terminal setup cannot persist a controller's runtime launch capability.
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, fileOptions))
                JsonSerializer.Serialize(stream, values, McpClientJsonContext.Default.DictionaryStringString);
            File.Move(temporary, path, true);
            _ = McpStdioOptions.ReadCredentialsFile(path);
        }
        finally { File.Delete(temporary); }
    }

    private static string Quote(string value) => JsonValue.Create(value)!.ToJsonString();

    private static void WriteAtomically(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, content, new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { File.Delete(temporary); }
    }

    [GeneratedRegex("(?m)^\\s*\\[\\s*mcp_servers\\.(?:tlbx|\"tlbx\"|'tlbx')(?:\\s*\\]|\\.)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TlbxTable();

    internal static void WriteSetupScripts(string directory, int port, string token, string? fingerprint, string command, string[] prefix)
    {
        var url = new UriBuilder("https", "localhost", port).Uri.GetLeftPart(UriPartial.Authority);
        static string Ps(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
        static string Sh(string text) => "'" + text.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
        var shellCommand = OperatingSystem.IsWindows() ? command.Replace('\\', '/') : command;
        var shellPrefix = OperatingSystem.IsWindows() ? prefix.Select(value => value.Replace('\\', '/')).ToArray() : prefix;
        var ps = "param([ValidateSet('codex','claude','generic')][string]$Client = 'generic')\n$ErrorActionPreference = 'Stop'\n" +
            "if (-not $env:MT_BASE_URL) { $env:MT_BASE_URL = if ($env:MT_PORT) { 'https://localhost:' + $env:MT_PORT } else { " + Ps(url) + " } }\n" +
            "if ($env:MT_BASE_URL.TrimEnd('/') -eq " + Ps(url) + ") { $env:MT_TOKEN = " + Ps(token) + "; $env:TLBX_MCP_CERT_SHA256 = " + Ps(fingerprint ?? "") + " }\n" +
            "$mcpArguments = if ($Client -eq 'generic') { @('mcp','bootstrap') } else { @('mcp','install',$Client.ToLowerInvariant()) }\n" +
            "& " + Ps(command) + " " + string.Join(" ", prefix.Select(Ps)) + " @mcpArguments\nexit $LASTEXITCODE\n";
        var sh = "#!/bin/sh\nset -eu\n" +
            "if [ -z \"${MT_BASE_URL:-}\" ]; then MT_BASE_URL=\"https://localhost:${MT_PORT:-" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}\"; export MT_BASE_URL; fi\n" +
            "if [ \"${MT_BASE_URL%/}\" = " + Sh(url) + " ]; then export MT_TOKEN=" + Sh(token) + " TLBX_MCP_CERT_SHA256=" + Sh(fingerprint ?? "") + "; fi\n" +
            "if [ \"${1:-generic}\" = generic ]; then set -- bootstrap; else set -- install \"$1\"; fi\n" +
            "exec " + Sh(shellCommand) + " " + string.Join(" ", shellPrefix.Select(Sh)) + " mcp \"$@\"\n";
        WriteAtomically(Path.Combine(directory, "install-mcp.ps1"), ps);
        WriteAtomically(Path.Combine(directory, "install-mcp.sh"), sh);
    }
}
