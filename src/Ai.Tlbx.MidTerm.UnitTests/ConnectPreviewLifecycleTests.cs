using System.Net;
using System.Diagnostics.CodeAnalysis;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Connect;
using Ai.Tlbx.MidTerm.Services.Hosting;
using Ai.Tlbx.MidTerm.Services.Security;
using Ai.Tlbx.MidTerm.Settings;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class ConnectPreviewLifecycleTests
{
    [Fact]
    [SuppressMessage("Usage", "IDISP014:Use a single instance of HttpClient",
        Justification = "The fixture owns one main-surface client and actual route clients owned by the tunnel.")]
    public async Task ClosedPreviewClientsAreDisposedWithoutExhaustingTheActiveRouteLimit()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("This managed host fixture uses the Windows settings/DPAPI build.");
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-connect-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new SettingsService(directory);
            var auth = new AuthService(settings, new ApiKeyService(settings));
            var registry = new BrowserPreviewRegistry();
            using var http = new HttpClient();
            await using var tunnel = new ConnectTunnel(new ServerBindingInfo(2100, "127.0.0.1"),
                new BrowserPreviewOriginService(2100, 2101, true), registry, auth, http,
                (_, _) => Task.CompletedTask, ConnectKeySecurityTests.CreateAuthenticatedPeer(), TestContext.Current.CancellationToken);

            ConnectPreviewClient? previous = null;
            for (var i = 0; i < 40; i++)
            {
                var route = i.ToString("x20", System.Globalization.CultureInfo.InvariantCulture);
                registry.Create("session", "named", route);
                var client = tunnel.ResolveDestination(Preview(route)).Preview!;
                Assert.Same(client, tunnel.ResolveDestination(Preview(route)).Preview);
                Assert.Empty(client.Cookies.GetCookies(new Uri("https://127.0.0.1:2101")));
                client.Cookies.Add(new Uri("https://127.0.0.1:2101"), new Cookie("route", route));
                if (previous is not null)
                    await Assert.ThrowsAsync<ObjectDisposedException>(() => previous.Http.GetAsync("https://127.0.0.1:2101", TestContext.Current.CancellationToken));
                Assert.Equal(1, registry.Remove("session", "named"));
                previous = client;
            }

            var routes = Enumerable.Range(100, 33).Select(i => i.ToString("x20", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            foreach (var route in routes) registry.Create("session", route, route);
            foreach (var route in routes.Take(32)) Assert.NotNull(tunnel.ResolveDestination(Preview(route)).Preview);
            Assert.Throws<InvalidDataException>(() => tunnel.ResolveDestination(Preview(routes[32])));
            Assert.Equal(1, registry.Remove("session", routes[0]));
            Assert.NotNull(tunnel.ResolveDestination(Preview(routes[32])).Preview);
            Assert.Throws<InvalidDataException>(() => tunnel.ResolveDestination(Preview("ffffffffffffffffffff")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static ConnectEnvelope Preview(string route) => new()
    {
        Surface = "preview", PreviewRouteKey = route, Path = "/webpreview/" + route + "/"
    };
}
