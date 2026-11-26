using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class AcpAppServerControlAgentRuntime
{
    private readonly List<AcpAdvertisedAuthMethod> _authMethods = [];

    private async Task AuthenticateAcpAsync(JsonElement initializeResult, CancellationToken ct)
    {
        _authMethods.Clear();
        if (Traverse(initializeResult, "authMethods") is { ValueKind: JsonValueKind.Array } methods)
        {
            using var enumerator = methods.EnumerateArray();
            while (enumerator.MoveNext())
            {
                var method = enumerator.Current;
                var id = GetString(method, "methodId") ?? GetString(method, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var type = GetString(method, "type");
                var name = GetString(method, "name") ?? id;
                var description = GetString(method, "description");
                _authMethods.Add(new AcpAdvertisedAuthMethod(id, type, name, description, method.Clone()));
            }
        }

        if (string.IsNullOrWhiteSpace(_authenticationMethodId))
        {
            return;
        }

        if (_authMethods.Count == 0)
        {
            throw new InvalidOperationException($"{_agentName} did not advertise authentication methods.");
        }

        var selected = _authMethods.FirstOrDefault(m => string.Equals(m.MethodId, _authenticationMethodId, StringComparison.Ordinal));
        if (selected is null)
        {
            throw new InvalidOperationException($"{_agentName} does not support the configured authentication method '{_authenticationMethodId}'.");
        }

        if (string.Equals(selected.Type, "terminal", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{_agentName} authentication method '{selected.Name}' ({selected.MethodId}) is an interactive terminal method and cannot be invoked via auth/login; run the agent CLI interactively in a terminal to sign in.");
        }

        if (_protocolGeneration == 2 && selected.Type is not (null or "agent"))
        {
            EmitRuntimeMessage(
                "runtime.notice",
                $"{_agentName} advertised custom ACP auth method type '{selected.Type}' for '{selected.MethodId}'.",
                selected.Raw.GetRawText(),
                timelineVisible: false);
        }

        var rpcMethod = _protocolGeneration == 2 ? "auth/login" : "authenticate";
        await SendRequestAsync(rpcMethod, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("methodId", _authenticationMethodId);
            writer.WriteEndObject();
        }, AttachRequestTimeout, ct).ConfigureAwait(false);
    }

    private async Task<HostCommandOutcome> LogoutAcpAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        var rpcMethod = _protocolGeneration == 2 ? "auth/logout" : "logout";
        await SendRequestAsync(rpcMethod, writer =>
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }, AttachRequestTimeout, ct).ConfigureAwait(false);
        return Accepted(command.CommandId, command.SessionId);
    }

    private sealed record AcpAdvertisedAuthMethod(
        string MethodId,
        string? Type,
        string Name,
        string? Description,
        JsonElement Raw);
}
