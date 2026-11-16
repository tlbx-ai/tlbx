using System.Collections.Concurrent;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

/// <summary>Display-only server identity. Never retains arguments, headers or credential environment.</summary>
internal sealed class AppServerControlMcpToolRegistry
{
    private readonly ConcurrentDictionary<string, string> _addresses = new(StringComparer.Ordinal);
    private string? _nativeAddress;

    internal void Configure(AppServerControlMcpServer? server)
    {
        _addresses.Clear();
        _nativeAddress = null;
        if (server is not null) ConfigureNative(server);
    }

    internal void ConfigureNative(AppServerControlMcpServer server)
    {
        Register("tlbx", server.DisplayAddress ?? server.Command);
        _nativeAddress = _addresses.GetValueOrDefault("tlbx");
    }

    internal string? Resolve(string serverName) =>
        serverName == "tlbx" && _nativeAddress is not null ? _nativeAddress : _addresses.GetValueOrDefault(serverName);

    internal void Register(string? name, string? address)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 160 || string.IsNullOrWhiteSpace(address)) return;
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            // Native Codex exposes only the HTTP origin. Keep every provider equally
            // safe: no userinfo, bearer query, fragment or credential-bearing path.
            address = new UriBuilder(uri) { UserName = "", Password = "" }.Uri.GetLeftPart(UriPartial.Authority);
        }
        if (address.Length > 2_048 || address.Any(char.IsControl)) return;
        _addresses[name] = address;
    }

    internal void Observe(JsonElement servers)
    {
        if (servers.ValueKind != JsonValueKind.Array) return;
        using var entries = servers.EnumerateArray();
        while (entries.MoveNext())
        {
            var server = entries.Current;
            if (server.ValueKind != JsonValueKind.Object) continue;
            Register(Text(server, "name"), Text(server, "httpOrigin") ?? Text(server, "address"));
        }
    }

    private static string? Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
}
