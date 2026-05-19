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
using Ai.Tlbx.MidTerm.Models.Certificates;
using Ai.Tlbx.MidTerm.Models.Files;
using Ai.Tlbx.MidTerm.Models.History;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Models.System;
using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Models.WebPreview;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Services.Sessions;
namespace Ai.Tlbx.MidTerm.Tests;

public sealed class IntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private bool _disposed;

    public IntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
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
        using var response = await _client.GetAsync("/api/version");

        response.EnsureSuccessStatusCode();
        var version = await response.Content.ReadAsStringAsync();

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

        var buffer = new byte[8192];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await ws.ReceiveAsync(buffer, cts.Token);

        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);

        var state = System.Text.Json.JsonSerializer.Deserialize<StateUpdate>(json, AppJsonContext.Default.StateUpdate);
        Assert.NotNull(state);
        Assert.NotNull(state.Sessions);
        Assert.NotNull(state.Sessions.Sessions);
    }

    [Fact]
    public async Task WebSocket_State_InitialPayload_IncludesSupervisorForCodexSessions()
    {
        using var scope = _factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<TtyHostSessionManager>();
        const string sessionId = "codex-s1";
        SeedSession(manager, new SessionInfo
        {
            Id = sessionId,
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

            var buffer = new byte[8192];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await ws.ReceiveAsync(buffer, cts.Token);

            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var state = System.Text.Json.JsonSerializer.Deserialize<StateUpdate>(json, AppJsonContext.Default.StateUpdate);

            var session = Assert.Single(state!.Sessions!.Sessions, s => s.Id == sessionId);
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

    [Fact]
    public async Task WebPreview_CookieBridge_SetGetDelete_Works()
    {
        const string sessionId = "session-a";
        const string previewName = "default";
        using var targetRes = await _client.PutAsJsonAsync("/api/webpreview/target", new WebPreviewTargetRequest
        {
            SessionId = sessionId,
            PreviewName = previewName,
            Url = "https://example.com"
        }, AppJsonContext.Default.WebPreviewTargetRequest);
        targetRes.EnsureSuccessStatusCode();

        var previewQuery = $"?sessionId={Uri.EscapeDataString(sessionId)}&previewName={Uri.EscapeDataString(previewName)}";
        using var setRes = await _client.PostAsJsonAsync($"/api/webpreview/cookies{previewQuery}", new WebPreviewCookieSetRequest
        {
            Raw = "theme=dark; Path=/"
        }, AppJsonContext.Default.WebPreviewCookieSetRequest);
        setRes.EnsureSuccessStatusCode();

        using var getRes = await _client.GetAsync($"/api/webpreview/cookies{previewQuery}");
        getRes.EnsureSuccessStatusCode();
        var cookies = await getRes.Content.ReadFromJsonAsync(
            AppJsonContext.Default.WebPreviewCookiesResponse);
        Assert.NotNull(cookies);
        Assert.Contains("theme=dark", cookies.Header, StringComparison.Ordinal);

        using var delRes = await _client.DeleteAsync($"/api/webpreview/cookies{previewQuery}&name=theme");
        delRes.EnsureSuccessStatusCode();

        using var afterDeleteRes = await _client.GetAsync($"/api/webpreview/cookies{previewQuery}");
        afterDeleteRes.EnsureSuccessStatusCode();
        var afterDelete = await afterDeleteRes.Content.ReadFromJsonAsync(
            AppJsonContext.Default.WebPreviewCookiesResponse);
        Assert.NotNull(afterDelete);
        Assert.DoesNotContain("theme=dark", afterDelete.Header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebPreview_HardReload_ClearsCookieJar()
    {
        const string sessionId = "session-a";
        const string previewName = "default";
        using var targetRes = await _client.PutAsJsonAsync("/api/webpreview/target", new WebPreviewTargetRequest
        {
            SessionId = sessionId,
            PreviewName = previewName,
            Url = "https://example.com"
        }, AppJsonContext.Default.WebPreviewTargetRequest);
        targetRes.EnsureSuccessStatusCode();

        var previewQuery = $"?sessionId={Uri.EscapeDataString(sessionId)}&previewName={Uri.EscapeDataString(previewName)}";
        using var setRes = await _client.PostAsJsonAsync($"/api/webpreview/cookies{previewQuery}", new WebPreviewCookieSetRequest
        {
            Raw = "session=abc123; Path=/"
        }, AppJsonContext.Default.WebPreviewCookieSetRequest);
        setRes.EnsureSuccessStatusCode();

        using var reloadRes = await _client.PostAsJsonAsync("/api/webpreview/reload", new WebPreviewReloadRequest
        {
            SessionId = sessionId,
            PreviewName = previewName,
            Mode = "hard"
        }, AppJsonContext.Default.WebPreviewReloadRequest);
        reloadRes.EnsureSuccessStatusCode();

        using var getRes = await _client.GetAsync($"/api/webpreview/cookies{previewQuery}");
        getRes.EnsureSuccessStatusCode();
        var cookies = await getRes.Content.ReadFromJsonAsync(
            AppJsonContext.Default.WebPreviewCookiesResponse);
        Assert.NotNull(cookies);
        Assert.True(string.IsNullOrEmpty(cookies.Header));
    }

    [Fact]
    public async Task WebPreview_CookieBridge_HidesHttpOnlyCookies()
    {
        const string sessionId = "session-a";
        const string previewName = "default";
        using var targetRes = await _client.PutAsJsonAsync("/api/webpreview/target", new WebPreviewTargetRequest
        {
            SessionId = sessionId,
            PreviewName = previewName,
            Url = "https://example.com"
        }, AppJsonContext.Default.WebPreviewTargetRequest);
        targetRes.EnsureSuccessStatusCode();
        var target = await targetRes.Content.ReadFromJsonAsync(AppJsonContext.Default.WebPreviewTargetResponse);
        Assert.NotNull(target);

        var previewQuery = $"?sessionId={Uri.EscapeDataString(sessionId)}&previewName={Uri.EscapeDataString(previewName)}";
        using var setRes = await _client.PostAsJsonAsync($"/api/webpreview/cookies{previewQuery}", new WebPreviewCookieSetRequest
        {
            Raw = "session=abc123; Path=/; HttpOnly"
        }, AppJsonContext.Default.WebPreviewCookieSetRequest);
        setRes.EnsureSuccessStatusCode();

        using var bridgeRequest = new HttpRequestMessage(HttpMethod.Get, $"/webpreview/{target!.RouteKey}/_cookies");
        bridgeRequest.Headers.Referrer = new Uri($"https://localhost/webpreview/{target.RouteKey}/");
        using var bridgeRes = await _client.SendAsync(bridgeRequest);
        bridgeRes.EnsureSuccessStatusCode();
        var bridgeCookies = await bridgeRes.Content.ReadFromJsonAsync(
            AppJsonContext.Default.WebPreviewCookiesResponse);

        Assert.NotNull(bridgeCookies);
        Assert.DoesNotContain("session=abc123", bridgeCookies.Header ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrowserPreviewClient_Create_ReturnsPreviewIdentity()
    {
        using var response = await _client.PostAsJsonAsync("/api/browser/preview-client", new BrowserPreviewClientRequest
        {
            SessionId = "session-123"
        }, AppJsonContext.Default.BrowserPreviewClientRequest);

        response.EnsureSuccessStatusCode();
        var previewClient = await response.Content.ReadFromJsonAsync(
            AppJsonContext.Default.BrowserPreviewClientResponse);

        Assert.NotNull(previewClient);
        Assert.Equal("session-123", previewClient.SessionId);
        Assert.False(string.IsNullOrWhiteSpace(previewClient.PreviewId));
        Assert.False(string.IsNullOrWhiteSpace(previewClient.PreviewToken));
    }

    private async Task<WebSocket> ConnectWebSocketAsync(string path)
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
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
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var result = await ws.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType != WebSocketMessageType.Text)
            {
                return null;
            }

            return Encoding.UTF8.GetString(buffer, 0, result.Count);
        }
        catch (OperationCanceledException)
        {
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
