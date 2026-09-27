using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    [InlineData("http://localhost:2000")]
    [InlineData("http://remote.example")]
    [InlineData("https://user:password@remote.example")]
    public void RemoteCredentials_RequireHttpsWithoutUrlCredentials(string url)
    {
        Assert.Throws<ArgumentException>(() => HubService.ValidateRemoteUri(url));
    }

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

        hubService.PinFingerprint(created.Id, server.Fingerprint);
        var session = await hubService.CreateSessionAsync(created.Id, request: null);

        Assert.Equal("remote-session-1", session.Id);
        Assert.True(server.LoginAttempts >= 1);
        Assert.True(server.InvalidApiKeyAttempts >= 1);
        Assert.True(server.CreateSessionUsedCookieAuth);
        Assert.False(server.CreateSessionUsedAuthorizationHeader);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UntrustedOrMismatchedCertificate_ReceivesNoCredentials(bool wrongPin)
    {
        await using var server = await TestHubServer.StartAsync(requirePassword: true);
        var hub = CreateHubService();
        var machine = hub.UpsertMachine(null, new HubMachineUpsertRequest
        {
            Name = "untrusted", BaseUrl = server.BaseUrl, Enabled = true,
            ApiKey = "private-key", Password = TestHubServer.ValidPassword
        });
        if (wrongPin) hub.PinFingerprint(machine.Id, string.Join(':', Enumerable.Repeat("00", 32)));
        await Assert.ThrowsAnyAsync<Exception>(() => hub.CreateSessionAsync(machine.Id, request: null));
        Assert.Equal(0, server.LoginAttempts);
        Assert.Equal(0, server.InvalidApiKeyAttempts);
    }

    [Fact]
    public async Task LoginRedirect_DoesNotForwardPassword()
    {
        await using var server = await TestHubServer.StartAsync(requirePassword: true);
        server.RedirectLogin = true;
        var hub = CreateHubService();
        var machine = hub.UpsertMachine(null, new HubMachineUpsertRequest
        {
            Name = "redirect", BaseUrl = server.BaseUrl, Enabled = true,
            Password = TestHubServer.ValidPassword
        });
        hub.PinFingerprint(machine.Id, server.Fingerprint);
        await Assert.ThrowsAnyAsync<Exception>(() => hub.CreateSessionAsync(machine.Id, request: null));
        Assert.Equal(1, server.LoginAttempts);
        Assert.False(server.RedirectReached);
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
        private readonly X509Certificate2 _certificate;
        public string Fingerprint => string.Join(':', SHA256.HashData(_certificate.RawData).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

        private TestHubServer(WebApplication app, string baseUrl, bool requirePassword, X509Certificate2 certificate)
        {
            _app = app;
            _certificate = certificate;
            BaseUrl = baseUrl;
            RequirePassword = requirePassword;
        }

        public string BaseUrl { get; }
        public bool RequirePassword { get; }
        public string Hostname => "remote-macbook";
        public int LoginAttempts { get; private set; }
        public bool RedirectLogin { get; set; }
        public bool RedirectReached { get; private set; }
        public int InvalidApiKeyAttempts { get; private set; }
        public bool CreateSessionUsedCookieAuth { get; private set; }
        public bool CreateSessionUsedAuthorizationHeader { get; private set; }

        public static async Task<TestHubServer> StartAsync(bool requirePassword)
        {
            var builder = WebApplication.CreateBuilder();
            var port = ReservePort();
            var url = string.Create(CultureInfo.InvariantCulture, $"https://127.0.0.1:{port}");
            using var key = RSA.Create(2048);
            using var generated = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), password: null);
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate)));

            var app = builder.Build();
            var server = new TestHubServer(app, url, requirePassword, certificate);
            server.MapEndpoints();
            await app.StartAsync(app.Lifetime.ApplicationStopping);
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync(_app.Lifetime.ApplicationStopping);
            await _app.DisposeAsync();
            _certificate.Dispose();
        }

        private void MapEndpoints()
        {
            _app.MapPost("/capture-password", () => { RedirectReached = true; return Results.Ok(); });
            _app.MapPost("/api/auth/login", async (HttpContext context) =>
            {
                LoginAttempts++;
                if (RedirectLogin) return Results.Redirect("/capture-password", preserveMethod: true);
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
