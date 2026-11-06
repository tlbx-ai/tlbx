using System.Net.WebSockets;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Ai.Tlbx.MidTerm.Models;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.WebSockets;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using Ai.Tlbx.MidTerm.Models.Auth;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Models.System;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Settings;
namespace Ai.Tlbx.MidTerm.Tests;

public sealed class IntegrationTests : IClassFixture<AuthenticatedAppFixture>, IAsyncLifetime, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private bool _disposed;
    private string _sessionCookie = "";

    public IntegrationTests(AuthenticatedAppFixture factory)
    {
        _factory = factory;
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
    }

    public async ValueTask InitializeAsync()
    {
        using var response = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Password = AuthenticatedAppFixture.Password }, AppJsonContext.Default.LoginRequest, cancellationToken: Xunit.TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        _sessionCookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        _client.DefaultRequestHeaders.Add("Cookie", _sessionCookie);
    }

    [Fact]
    public async Task AnonymousClient_CannotAccessControlApi()
    {
        using var anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false
        });
        using var response = await anonymous.GetAsync("/api/version", Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.CompletedTask;
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Api_GetVersion_ReturnsVersion()
    {
        using var response = await _client.GetAsync("/api/version", Xunit.TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var version = await response.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.NotEmpty(version);
    }

    [Fact]
    public async Task WebSocket_Mux_ReceivesInitFrame()
    {
        using var ws = await ConnectWebSocketAsync("/ws/mux");

        var buffer = new byte[1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await ws.ReceiveAsync(buffer, cts.Token);

        Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
        Assert.True(result.Count >= MuxProtocol.HeaderSize);
        Assert.Equal(0xFF, buffer[0]);
    }

    [Fact]
    public async Task WebSocket_State_ReceivesInitialSessionList()
    {
        using var ws = await ConnectWebSocketAsync("/ws/state");

        // Session metadata and release notes can span multiple receive buffers.
        var json = await ReceiveTextMessageAsync(ws, TimeSpan.FromSeconds(5));

        var state = System.Text.Json.JsonSerializer.Deserialize<StateUpdate>(json, AppJsonContext.Default.StateUpdate);
        Assert.NotNull(state);
        Assert.NotNull(state.Sessions);
        Assert.NotNull(state.Sessions.Sessions);
    }

    [Fact]
    public async Task WebSocket_State_ForwardsLiveTerminalNotifications()
    {
        using var scope = _factory.Services.CreateScope();
        var telemetry = scope.ServiceProvider.GetRequiredService<SessionTelemetryService>();
        using var ws = await ConnectWebSocketAsync("/ws/state");

        _ = await ReceiveTextMessageAsync(ws, TimeSpan.FromSeconds(5));
        await DrainAvailableTextMessagesAsync(ws, TimeSpan.FromMilliseconds(100));

        telemetry.RecordOutput("session-notify", Encoding.UTF8.GetBytes("\x1b]9;Agent turn complete\a"));

        var json = await ReceiveTextMessageAsync(ws, TimeSpan.FromSeconds(5));
        var message = System.Text.Json.JsonSerializer.Deserialize(
            json,
            AppJsonContext.Default.TerminalNotificationMessage);
        Assert.NotNull(message);
        Assert.Equal("terminal-notification", message.Type);
        Assert.Equal("session-notify", message.SessionId);
        Assert.Equal("osc9", message.Protocol);
        Assert.Equal("Agent turn complete", message.Body);
    }

    [Fact]
    public async Task Api_Notify_ForwardsForcedNotificationToStateWebSocket()
    {
        using var scope = _factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<TtyHostSessionManager>();
        const string sessionId = "notify-s1";
        SeedSession(manager, new SessionInfo
        {
            Id = sessionId,
            Pid = 42,
            HostPid = 43,
            ShellType = "Pwsh",
            CreatedAt = DateTime.UtcNow,
            IsRunning = true,
            CurrentDirectory = @"Q:\repos\Jpa"
        }, order: 0);

        try
        {
            using var ws = await ConnectWebSocketAsync("/ws/state");
            _ = await ReceiveTextMessageAsync(ws, TimeSpan.FromSeconds(5));
            await DrainAvailableTextMessagesAsync(ws, TimeSpan.FromMilliseconds(100));

            using var response = await _client.PostAsJsonAsync(
                "/api/notifications",
                new TerminalNotificationRequest
                {
                    SessionId = sessionId,
                    Title = "tlbx",
                    Body = "Ad hoc test",
                    Priority = NotificationPrioritySetting.Important
                },
                AppJsonContext.Default.TerminalNotificationRequest, cancellationToken: Xunit.TestContext.Current.CancellationToken);

            Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
            var json = await ReceiveTextMessageAsync(ws, TimeSpan.FromSeconds(5));
            var message = System.Text.Json.JsonSerializer.Deserialize(
                json,
                AppJsonContext.Default.TerminalNotificationMessage);
            Assert.NotNull(message);
            Assert.Equal("cli", message.Protocol);
            Assert.Equal("Ad hoc test", message.Body);
            Assert.True(message.Force);
            Assert.Equal(NotificationPrioritySetting.Important, message.Priority);
            Assert.False(message.NativeHandled);
        }
        finally
        {
            RemoveSeedSession(manager, sessionId);
        }
    }

    [Fact]
    public async Task WebSocket_State_InitialPayload_IncludesSupervisorForCodexSessions()
    {
        using var scope = _factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<TtyHostSessionManager>();
        const string sessionId = "codex-s1";
        var longName = new string('x', 9000) + "終";
        SeedSession(manager, new SessionInfo
        {
            Id = sessionId,
            Name = longName,
            Pid = 42,
            HostPid = 43,
            ShellType = "Pwsh",
            CreatedAt = DateTime.UtcNow,
            IsRunning = true,
            ForegroundPid = 4242,
            ForegroundName = "node",
            ForegroundCommandLine = @"node C:\Users\johan\AppData\Roaming\npm\node_modules\@openai\codex\bin\codex.js --yolo",
            CurrentDirectory = @"Q:\repos\Jpa"
        }, order: 0);

        try
        {
            using var ws = await ConnectWebSocketAsync("/ws/state");

            var json = await ReceiveTextMessageAsync(ws, TimeSpan.FromSeconds(5));
            var state = System.Text.Json.JsonSerializer.Deserialize<StateUpdate>(json, AppJsonContext.Default.StateUpdate);

            var session = Assert.Single(state!.Sessions!.Sessions, s => s.Id == sessionId);
            Assert.Equal(longName, session.Name);
            Assert.NotNull(session.Supervisor);
            Assert.Equal("codex", session.Supervisor!.Profile);
        }
        finally
        {
            RemoveSeedSession(manager, sessionId);
        }
    }

    [Fact]
    public async Task WebSocket_State_CoalescesInFlightBurstUpdates()
    {
        using var scope = _factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<TtyHostSessionManager>();
        using var ws = await ConnectWebSocketAsync("/ws/state");

        var initialJson = await ReceiveTextMessageAsync(ws, TimeSpan.FromSeconds(5));
        var initialState = System.Text.Json.JsonSerializer.Deserialize<StateUpdate>(
            initialJson,
            AppJsonContext.Default.StateUpdate);
        Assert.NotNull(initialState?.Sessions);

        await DrainAvailableTextMessagesAsync(ws, TimeSpan.FromMilliseconds(100));

        var registry = GetField<object>(manager, "_registry");
        var notifyStateChange = registry.GetType().GetMethod(
            "NotifyStateChange",
            BindingFlags.Instance | BindingFlags.Public)!;

        for (var i = 0; i < 20; i++)
        {
            notifyStateChange.Invoke(registry, null);
        }

        var messages = new List<string>
        {
            await ReceiveTextMessageAsync(ws, TimeSpan.FromSeconds(5))
        };

        while (await TryReceiveTextMessageAsync(ws, TimeSpan.FromMilliseconds(150)) is { } message)
        {
            messages.Add(message);
        }

        Assert.InRange(messages.Count, 1, 3);
        foreach (var message in messages)
        {
            var state = System.Text.Json.JsonSerializer.Deserialize<StateUpdate>(
                message,
                AppJsonContext.Default.StateUpdate);
            Assert.NotNull(state?.Sessions);
        }
    }

    private async Task<WebSocket> ConnectWebSocketAsync(string path)
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request => request.Headers.Cookie = _sessionCookie;
        var uri = new Uri(_factory.Server.BaseAddress, path);
        var wsUri = new UriBuilder(uri) { Scheme = uri.Scheme == "https" ? "wss" : "ws" }.Uri;

        return await wsClient.ConnectAsync(wsUri, CancellationToken.None);
    }

    private static async Task<string> ReceiveTextMessageAsync(WebSocket ws, TimeSpan timeout)
    {
        var message = await TryReceiveTextMessageAsync(ws, timeout);
        Assert.NotNull(message);
        return message;
    }

    private static async Task DrainAvailableTextMessagesAsync(WebSocket ws, TimeSpan timeout)
    {
        while (await TryReceiveTextMessageAsync(ws, timeout) is not null)
        {
        }
    }

    private static async Task<string?> TryReceiveTextMessageAsync(WebSocket ws, TimeSpan timeout)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    Assert.Equal(0, message.Length);
                    return null;
                }
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                    return Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Equal(0, message.Length);
            return null;
        }
    }

    private static void SeedSession(TtyHostSessionManager manager, SessionInfo session, int order)
    {
        var registry = GetField<object>(manager, "_registry");
        var cache = GetProperty<ConcurrentDictionary<string, SessionInfo>>(registry, "SessionCache");
        var orders = GetProperty<ConcurrentDictionary<string, int>>(registry, "SessionOrder");
        cache[session.Id] = session;
        orders[session.Id] = order;
    }

    private static void RemoveSeedSession(TtyHostSessionManager manager, string sessionId)
    {
        var registry = GetField<object>(manager, "_registry");
        var cache = GetProperty<ConcurrentDictionary<string, SessionInfo>>(registry, "SessionCache");
        var orders = GetProperty<ConcurrentDictionary<string, int>>(registry, "SessionOrder");
        cache.TryRemove(sessionId, out _);
        orders.TryRemove(sessionId, out _);
    }

    private static T GetField<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (T)field.GetValue(instance)!;
    }

    private static T GetProperty<T>(object instance, string name)
    {
        var property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        return (T)property.GetValue(instance)!;
    }
}
