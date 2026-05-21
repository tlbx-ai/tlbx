using Ai.Tlbx.MidTerm.Common.Ipc;
using System.Reflection;

namespace Ai.Tlbx.MidTerm.AgentHost;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
        {
            PrintHelp();
            return 0;
        }

        if (args.Contains("--version", StringComparer.Ordinal) || args.Contains("-v", StringComparer.Ordinal))
        {
            Console.WriteLine(GetVersion());
            return 0;
        }

        var syntheticProvider = ReadOption(args, "--synthetic");
        var instanceId = ReadOption(args, "--instance-id");
        var ownerToken = ReadOption(args, "--owner-token");
        var sessionId = ReadOption(args, "--session-id");

        if (args.Contains("--ipc", StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(instanceId) ||
                string.IsNullOrWhiteSpace(ownerToken) ||
                string.IsNullOrWhiteSpace(sessionId))
            {
                Console.Error.WriteLine("mtagenthost --ipc requires --instance-id, --owner-token, and --session-id.");
                return 1;
            }

            var endpoint = AppServerControlHostEndpoint.GetSessionEndpoint(instanceId, sessionId, Environment.ProcessId);
            await using var ipcServer = new AppServerControlAgentHostServer(syntheticProvider, instanceId, ownerToken);
            await ipcServer.RunIpcAsync(endpoint).ConfigureAwait(false);
            return 0;
        }

        if (!args.Contains("--stdio", StringComparer.Ordinal))
        {
            Console.Error.WriteLine("mtagenthost requires --stdio or --ipc.");
            return 1;
        }

        await using var server = new AppServerControlAgentHostServer(syntheticProvider);
        await server.RunStdioAsync().ConfigureAwait(false);
        return 0;
    }

    private static string? ReadOption(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            mtagenthost - MidTerm external agent runtime host

            Usage:
              mtagenthost --stdio
              mtagenthost --stdio --synthetic <provider>
              mtagenthost --ipc --session-id <id> --instance-id <id> --owner-token <token>
              mtagenthost --version
              mtagenthost --help

            Current scope:
              - stdio JSON transport
              - owned IPC transport for persistent MidTerm reconnect
              - real Codex, Claude, and Grok runtimes
              - synthetic provider mode for protocol/integration testing
            """);
    }

    private static string GetVersion()
    {
        var version = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            version = typeof(Program).Assembly.GetName().Version?.ToString();
        }

        return $"mtagenthost {version ?? "0.0.0"}";
    }
}
