using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Ai.Tlbx.MidTerm.Models.Auth;
using Ai.Tlbx.MidTerm.Models.Certificates;
using Ai.Tlbx.MidTerm.Models.Hub;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Models.System;
using Ai.Tlbx.MidTerm.Models.Update;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Hub;
using Ai.Tlbx.MidTerm.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class HubServiceTests : IAsyncDisposable
{
    private readonly string _settingsDir = Path.Combine(
        Path.GetTempPath(),
        "midterm-hub-tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("mt-session=auth", "mt-session=auth; mt-client-id=hub-instance")]
    [InlineData("mt-client-id=old; mt-session=auth", "mt-session=auth; mt-client-id=hub-instance")]
    public void UpsertCookie_PreservesAuthenticationAndReplacesForwardedBrowser(
        string existing,
        string expected)
    {
        Assert.Equal(expected, HubService.UpsertCookie(existing, "mt-client-id", "hub-instance"));
    }

    [Fact]
    public async Task GetMachineStateAsync_UpdatesPlaceholderNameFromRemoteHostname()
    {
        await using var server = await TestHubServer.StartAsync(requirePassword: false);
        var hubService = CreateHubService();

        var created = hubService.UpsertMachine(null, new HubMachineUpsertRequest
        {
            Name = "",
            BaseUrl = server.BaseUrl,
            Enabled = true
        });

        Assert.Equal("127.0.0.1", created.Name);

        var state = await hubService.GetMachineStateAsync(created.Id);

        Assert.Equal(server.Hostname, state.Machine.Name);
        Assert.Equal(server.Hostname, hubService.GetMachine(created.Id)?.Name);
    }

    [Fact]
    public async Task CreateSessionAsync_FallsBackToPassword_WhenApiKeyIsRejected()
    {
        await using var server = await TestHubServer.StartAsync(requirePassword: true);
        var hubService = CreateHubService();

        var created = hubService.UpsertMachine(null, new HubMachineUpsertRequest
        {
            Name = "",
            BaseUrl = server.BaseUrl,
            Enabled = true,
            ApiKey = "invalid-key",
            Password = TestHubServer.ValidPassword
        });

        var session = await hubService.CreateSessionAsync(created.Id, request: null);

        Assert.Equal("remote-session-1", session.Id);
        Assert.True(server.LoginAttempts >= 1);
        Assert.True(server.InvalidApiKeyAttempts >= 1);
        Assert.True(server.CreateSessionUsedCookieAuth);
        Assert.False(server.CreateSessionUsedAuthorizationHeader);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_settingsDir))
            {
                Directory.Delete(_settingsDir, recursive: true);
            }
        }
        catch
        {
        }

        return ValueTask.CompletedTask;
    }

    private HubService CreateHubService()
    {
        Directory.CreateDirectory(_settingsDir);
        return new HubService(new SettingsService(_settingsDir));
    }

    private sealed class TestHubServer : IAsyncDisposable
    {
        public const string ValidPassword = "correct horse battery staple";
        private const string SessionCookieValue = "hub-test-cookie";
        private const string ValidApiKey = "valid-key";

        private readonly WebApplication _app;

        private TestHubServer(WebApplication app, string baseUrl, bool requirePassword)
        {
            _app = app;
            BaseUrl = baseUrl;
            RequirePassword = requirePassword;
        }

        public string BaseUrl { get; }
        public bool RequirePassword { get; }
        public string Hostname => "remote-macbook";
        public int LoginAttempts { get; private set; }
        public int InvalidApiKeyAttempts { get; private set; }
        public bool CreateSessionUsedCookieAuth { get; private set; }
        public bool CreateSessionUsedAuthorizationHeader { get; private set; }

        public static async Task<TestHubServer> StartAsync(bool requirePassword)
        {
            var builder = WebApplication.CreateBuilder();
            var port = ReservePort();
            var url = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}");
            builder.WebHost.UseUrls(url);

            var app = builder.Build();
            var server = new TestHubServer(app, url, requirePassword);
            server.MapEndpoints();
            await app.StartAsync(app.Lifetime.ApplicationStopping);
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync(_app.Lifetime.ApplicationStopping);
            await _app.DisposeAsync();
        }

        private void MapEndpoints()
        {
            _app.MapPost("/api/auth/login", async (HttpContext context) =>
            {
                LoginAttempts++;
                var request = await context.Request.ReadFromJsonAsync(
                    AppJsonContext.Default.LoginRequest,
                    context.RequestAborted);
                if (request?.Password != ValidPassword)
                {
                    return Results.Json(
                        new AuthResponse { Success = false, Error = "Invalid password" },
                        AppJsonContext.Default.AuthResponse,
                        statusCode: (int)HttpStatusCode.Unauthorized);
                }

                context.Response.Cookies.Append(
                    AuthService.SessionCookieName,
                    SessionCookieValue,
                    new CookieOptions
                    {
                        HttpOnly = true,
                        Path = "/"
                    });
                return Results.Json(new AuthResponse { Success = true }, AppJsonContext.Default.AuthResponse);
            });

            _app.MapGet("/api/bootstrap", (HttpContext context) =>
            {
                if (!IsAuthorized(context))
                {
                    return Results.Unauthorized();
                }

                return Results.Json(new BootstrapResponse
                {
                    Hostname = Hostname,
                    Version = "8.7.60-dev"
                }, AppJsonContext.Default.BootstrapResponse);
            });

            _app.MapGet("/api/sessions", (HttpContext context) =>
            {
                if (!IsAuthorized(context))
                {
                    return Results.Unauthorized();
                }

                return Results.Json(new SessionListDto
                {
                    Sessions = []
                }, AppJsonContext.Default.SessionListDto);
            });

            _app.MapPost("/api/sessions", (HttpContext context) =>
            {
                if (!IsAuthorized(context))
                {
                    return Results.Unauthorized();
                }

                CreateSessionUsedCookieAuth = HasSessionCookie(context);
                CreateSessionUsedAuthorizationHeader = context.Request.Headers.ContainsKey("Authorization");
                return Results.Json(new SessionInfoDto
                {
                    Id = "remote-session-1",
                    Cols = 120,
                    Rows = 30,
                    CreatedAt = DateTime.UtcNow,
                    IsRunning = true,
                    ShellType = "bash"
                }, AppJsonContext.Default.SessionInfoDto);
            });

            _app.MapGet("/api/update/check", (HttpContext context) =>
            {
                if (!IsAuthorized(context))
                {
                    return Results.Unauthorized();
                }

                return Results.Json(new UpdateInfo
                {
                    Available = false,
                    CurrentVersion = "8.7.60-dev",
                    LatestVersion = "8.7.60-dev"
                }, AppJsonContext.Default.UpdateInfo);
            });

            _app.MapGet("/api/certificate/share-packet", (HttpContext context) =>
            {
                if (!IsAuthorized(context))
                {
                    return Results.Unauthorized();
                }

                return Results.Json(new SharePacketInfo
                {
                    Certificate = new CertificateDownloadInfo
                    {
                        FingerprintFormatted = "AA:BB:CC"
                    }
                }, AppJsonContext.Default.SharePacketInfo);
            });
        }

        private bool IsAuthorized(HttpContext context)
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrWhiteSpace(authorization))
            {
                if (string.Equals(authorization, $"Bearer {ValidApiKey}", StringComparison.Ordinal))
                {
                    return true;
                }

                InvalidApiKeyAttempts++;
                return false;
            }

            if (!RequirePassword)
            {
                return true;
            }

            return HasSessionCookie(context);
        }

        private static bool HasSessionCookie(HttpContext context)
        {
            return string.Equals(
                context.Request.Cookies[AuthService.SessionCookieName],
                SessionCookieValue,
                StringComparison.Ordinal);
        }

        private static int ReservePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
