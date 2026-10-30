using System.Text.Json;
using Ai.Tlbx.MidTerm.AgentHost;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.AgentHost.UnitTests;

public sealed class AppServerControlMcpRuntimeScopeTests
{
    [Fact]
    public void FirstBootstrapScopeSurvivesStartupAndInitialProviderThreadDiscovery()
    {
        var scope = new AppServerControlMcpRuntimeScope();
        var advertised = RoundTripHello(scope).McpScopeIdentity;
        scope.Observe(SessionState("starting"));
        scope.Observe(Thread("provider-thread-a"));
        scope.Observe(SessionState("ready"));
        Assert.Equal(advertised, RoundTripHello(scope).McpScopeIdentity);
    }

    [Fact]
    public void BackendDeathWhileWebIsDisconnectedChangesReconnectHelloBeforeNextStartup()
    {
        var scope = new AppServerControlMcpRuntimeScope();
        scope.Observe(SessionState("starting"));
        scope.Observe(Thread("provider-thread-a"));
        var beforeDisconnect = RoundTripHello(scope).McpScopeIdentity;
        // Host events continue without a connected Web client.
        scope.Observe(SessionState("error"));
        var reconnect = RoundTripHello(scope).McpScopeIdentity;
        Assert.NotEqual(beforeDisconnect, reconnect, StringComparer.Ordinal);
        scope.Observe(SessionState("starting"));
        scope.Observe(Thread("provider-thread-a"));
        Assert.Equal(reconnect, RoundTripHello(scope).McpScopeIdentity);
    }

    [Fact]
    public void ReplacingProviderThreadInvalidatesScopeEvenWhenHostPidIsUnchanged()
    {
        var scope = new AppServerControlMcpRuntimeScope();
        scope.Observe(Thread("provider-thread-a"));
        var before = scope.Identity;
        scope.Observe(Thread("provider-thread-b"));
        Assert.NotEqual(before, scope.Identity, StringComparer.Ordinal);
        var after = scope.Identity;
        scope.Observe(Thread("provider-thread-b"));
        Assert.Equal(after, scope.Identity);
    }

    private static AppServerControlProviderEvent SessionState(string state) => new()
    {
        SessionState = new AppServerControlProviderSessionStatePayload { State = state }
    };

    private static AppServerControlProviderEvent Thread(string id) => new()
    {
        ThreadState = new AppServerControlProviderThreadStatePayload { ProviderThreadId = id }
    };

    private static AppServerControlHostHello RoundTripHello(AppServerControlMcpRuntimeScope scope)
    {
        var json = JsonSerializer.Serialize(new AppServerControlHostHello { McpScopeIdentity = scope.Identity },
            AppServerControlHostJsonContext.Default.AppServerControlHostHello);
        return JsonSerializer.Deserialize(json, AppServerControlHostJsonContext.Default.AppServerControlHostHello)!;
    }
}
