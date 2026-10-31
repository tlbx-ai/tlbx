using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Services.Sessions;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>Private host IPC handoff; provider configuration receives only an owner-protected file path.</summary>
internal static class TlbxMcpRuntimeConfiguration
{
    internal static void Configure(IServiceProvider services, int port, AuthService auth, X509Certificate2? certificate)
    {
        services.GetRequiredService<SessionAppServerControlHostRuntimeService>().ConfigureMcp((sessionId, hostPid) =>
        {
            if (certificate is null) return null;
            var command = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot locate the tlbx MCP executable.");
            var arguments = new List<string>();
            if (string.Equals(Path.GetFileNameWithoutExtension(command), "dotnet", StringComparison.OrdinalIgnoreCase))
                arguments.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
            arguments.AddRange(["mcp", "--stdio"]);
            return new AppServerControlMcpServer
            {
                Command = command,
                Arguments = arguments,
                EnvironmentVariables = new(StringComparer.Ordinal)
                {
                    ["MT_BASE_URL"] = new UriBuilder("https", "localhost", port).Uri.GetLeftPart(UriPartial.Authority),
                    ["MT_TOKEN"] = auth.CreateSessionToken(),
                    ["TLBX_MCP_CERT_SHA256"] = certificate.GetCertHashString(HashAlgorithmName.SHA256),
                    ["TLBX_MCP_BOOTSTRAP"] = services.GetRequiredService<TlbxMcpClientContextService>()
                        .IssueLaunchCapability(sessionId, hostPid)
                }
            };
        });
    }
}
