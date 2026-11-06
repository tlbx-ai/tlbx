using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using Ai.Tlbx.MidTerm.Models.Auth;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Security;
using Ai.Tlbx.MidTerm.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class AuthPasswordProtocolTests
{
    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    [SuppressMessage("Usage", "IDISP014:Use a single instance of HttpClient", Justification = "Each isolated authentication fixture owns one client for its complete request sequence.")]
    public async Task PasswordChangeEnforcesNewBoundsAndPreservesExistingShortPasswordLogin(int length, bool accepted)
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("The isolated settings fixture uses Windows DPAPI.");
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-password-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // A real persisted hash predates the new-password rule. Login must
            // continue to accept it while changes enforce the current bounds.
            const string existingPassword = "old-pw";
            var settings = new SettingsService(directory);
            var values = settings.Load();
            values.PasswordHash = AuthService.HashPasswordStatic(existingPassword);
            settings.Save(values);
            var auth = new AuthService(settings, new ApiKeyService(settings));
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            await using var app = builder.Build();
            AuthEndpoints.MapAuthEndpoints(app, settings, auth);
            await app.StartAsync(ct);
            var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            using var http = new HttpClient();
            using var login = await http.PostAsJsonAsync(address + "/api/auth/login", new LoginRequest { Password = existingPassword }, ct);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            Assert.Contains(login.Headers.GetValues("Set-Cookie"), value => value.StartsWith(AuthService.SessionCookieName + "=", StringComparison.Ordinal));

            var password = new string('a', length);
            using var change = await http.PostAsJsonAsync(address + "/api/auth/change-password",
                new ChangePasswordRequest { CurrentPassword = existingPassword, NewPassword = password }, ct);
            Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, change.StatusCode);

            // Reload through the actual secret store, not the in-memory object.
            var storedHash = new SettingsService(directory).Load().PasswordHash;
            Assert.True(auth.VerifyPassword(accepted ? password : existingPassword, storedHash));
            Assert.False(auth.VerifyPassword(accepted ? existingPassword : password, storedHash));
            using var updatedLogin = await http.PostAsJsonAsync(address + "/api/auth/login",
                new LoginRequest { Password = accepted ? password : existingPassword }, ct);
            Assert.Equal(HttpStatusCode.OK, updatedLogin.StatusCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
