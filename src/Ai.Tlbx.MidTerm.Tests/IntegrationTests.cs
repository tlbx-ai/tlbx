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

    [Theory]
    [InlineData("/ws/state", "command")]
    [InlineData("/ws/app-server-control", "request")]
    public async Task WebSocket_FragmentedUtf8CommandsRecoverAfterMalformedJson(string path, string kind)
    {
        using var ws = await ConnectWebSocketAsync(path);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await ws.SendAsync("{bad"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, timeout.Token);

        var id = "fragment-😀-" + new string('ü', 20000);
        var json = $$"""{"type":"{{kind}}","id":"{{id}}","action":"unused.fragment.contract","sessionId":"missing-fragment-contract-session"}""";
        var bytes = Encoding.UTF8.GetBytes(json);
        var split = Encoding.UTF8.GetByteCount(json.AsSpan(0, json.IndexOf("😀", StringComparison.Ordinal))) + 1;
        await ws.SendAsync(bytes.AsMemory(0, split), WebSocketMessageType.Text, false, timeout.Token);
        await ws.SendAsync(bytes.AsMemory(split, 1), WebSocketMessageType.Text, false, timeout.Token);
        await ws.SendAsync(bytes.AsMemory(split + 1), WebSocketMessageType.Text, true, timeout.Token);
        var reply = await ReadReplyAsync(id);
        if (kind == "command") Assert.False(reply.GetProperty("success").GetBoolean());
        else Assert.Equal("error", reply.GetProperty("type").GetString());

        const string nextId = "after-buffer-reuse";
        var next = Encoding.UTF8.GetBytes($$"""{"type":"{{kind}}","id":"{{nextId}}","action":"unused.fragment.contract","sessionId":"missing-fragment-contract-session"}""");
        await ws.SendAsync(next.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        _ = await ReadReplyAsync(nextId);

        async Task<System.Text.Json.JsonElement> ReadReplyAsync(string expectedId)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                var response = await ReceiveTextMessageAsync(ws, remaining);
                using var document = System.Text.Json.JsonDocument.Parse(response);
                if (document.RootElement.TryGetProperty("id", out var value) && value.GetString() == expectedId)
                    return document.RootElement.Clone();
            }
            throw new TimeoutException("Expected command reply was not received.");
        }
    }

    [Fact]
    public async Task GitWebSocket_FragmentedUtf8AndMalformedJsonKeepNextSubscriptionIntact()
    {
        using var ws = await ConnectWebSocketAsync("/ws/git");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await ws.SendAsync("{bad"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        var sessionId = "fragment-😀-" + new string('ü', 20000);
        await SendSplitUnicodeMessageAsync(ws, $$"""{"type":"subscribe","sessionId":"{{sessionId}}"}""", timeout.Token);
        await ReadSubscriptionAsync(sessionId);
        await ws.SendAsync("{\"type\":\"subscribe\",\"sessionId\":\"after-reuse\"}"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        await ReadSubscriptionAsync("after-reuse");

        async Task ReadSubscriptionAsync(string expected)
        {
            while (!timeout.IsCancellationRequested)
            {
                var reply = await ReceiveTextMessageAsync(ws, TimeSpan.FromSeconds(10));
                using var doc = System.Text.Json.JsonDocument.Parse(reply);
                if (doc.RootElement.TryGetProperty("sessionId", out var id) && id.GetString() == expected)
                {
                    Assert.Equal("repos", doc.RootElement.GetProperty("type").GetString());
                    return;
                }
            }
            timeout.Token.ThrowIfCancellationRequested();
        }
    }

    [Fact]
    public async Task BrowserWebSocket_FragmentedUtf8StateRecoversAfterMalformedJson()
    {
        var preview = "utf8-" + Guid.NewGuid().ToString("N");
        using var ws = await ConnectWebSocketAsync("/ws/browser?previewId=" + preview);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var service = _factory.Services.GetRequiredService<Ai.Tlbx.MidTerm.Services.Browser.BrowserCommandService>();
        await ws.SendAsync("{bad"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        var payload = "fragment-😀-" + new string('ü', 40000);
        await SendSplitUnicodeMessageAsync(ws, $$"""{"type":"browser-state","result":"{{payload}}","visible":true,"focus":true,"topLevel":true}""", timeout.Token);
        await WaitForStateAsync(true);
        await ws.SendAsync("{\"type\":\"browser-state\",\"visible\":false,\"focus\":false,\"topLevel\":false}"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        await WaitForStateAsync(false);

        async Task WaitForStateAsync(bool expected)
        {
            while (!timeout.IsCancellationRequested)
            {
                var client = service.GetStatus(null, previewId: preview).Clients.SingleOrDefault();
                if (client is not null && client.IsVisible == expected && client.HasFocus == expected && client.IsTopLevel == expected) return;
                await Task.Delay(20, timeout.Token);
            }
            timeout.Token.ThrowIfCancellationRequested();
        }
    }

    private static async Task SendSplitUnicodeMessageAsync(WebSocket socket, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var split = Encoding.UTF8.GetByteCount(json.AsSpan(0, json.IndexOf("😀", StringComparison.Ordinal))) + 1;
        await socket.SendAsync(bytes.AsMemory(0, split), WebSocketMessageType.Text, false, ct);
        await socket.SendAsync(bytes.AsMemory(split, 1), WebSocketMessageType.Text, false, ct);
        await socket.SendAsync(bytes.AsMemory(split + 1), WebSocketMessageType.Text, true, ct);
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
