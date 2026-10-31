using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Models.Hub;
using Ai.Tlbx.MidTerm.Services.Hub;
using Ai.Tlbx.MidTerm.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class HubControlPlaneProtocolTests
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task HubForwardsExplicitAndDefaultSnapshotLimitsOverPinnedAuthenticatedHttps()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-hub-control-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: true));
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
            // Import the private key for Schannel's server credential support.
            using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
            var observed = new ConcurrentQueue<string>();
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)));
            await using var remote = builder.Build();
            remote.Use(async (context, next) =>
            {
                if (context.Request.Headers.Authorization != "Bearer fixture-hub-key")
                { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
                await next(context);
            });
            remote.MapGet("/api/bootstrap", () => Results.Text("{}", "application/json"));
            remote.MapGet("/api/control-plane", (HttpContext context) =>
            {
                observed.Enqueue(context.Request.QueryString.Value!);
                return Results.Text("""{"workItems":[{"id":"remote-fixture"}],"sessionStatuses":[],"checkpoints":[]}""", "application/json");
            });
            var ct = TestContext.Current.CancellationToken;
            await remote.StartAsync(ct);
            var remoteAddress = remote.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var service = new HubService(new SettingsService(directory));
            var machine = service.UpsertMachine(null, new HubMachineUpsertRequest
                { Name = "Wire fixture", BaseUrl = remoteAddress, Enabled = true, ApiKey = "fixture-hub-key" });
            // Hub pins use colon-separated bytes, matching the product's TOFU
            // fingerprint. GetCertHashString returns a different compact form.
            var fingerprint = string.Join(':', certificate.GetCertHash(HashAlgorithmName.SHA256)
                .Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
            service.PinFingerprint(machine.Id, fingerprint);

            var localBuilder = WebApplication.CreateSlimBuilder();
            localBuilder.Logging.ClearProviders();
            localBuilder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
            await using var local = localBuilder.Build();
            var diagnostics = new ConcurrentQueue<string>();
            local.Use(async (context, next) =>
            {
                try { await next(context); }
                catch (Exception error)
                {
                    var detail = (error.GetType().Name + ": " + error.Message)
                        .Replace("fixture-hub-key", "[redacted]", StringComparison.Ordinal);
                    diagnostics.Enqueue(detail[..Math.Min(detail.Length, 2048)]);
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                }
            });
            HubEndpoints.MapHubEndpoints(local, service);
            await local.StartAsync(ct);
            var localAddress = local.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            foreach (var query in new[] { "?workLimit=7&checkpointLimit=9", "" })
            {
                using var response = await Http.GetAsync(localAddress + "/api/hub/machines/" + machine.Id + "/control-plane" + query, ct);
                Assert.True(response.StatusCode == HttpStatusCode.OK,
                    "Hub response: " + response.StatusCode + "; fixture handler: " + string.Join(" | ", diagnostics));
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                Assert.Equal("remote-fixture", body.RootElement.GetProperty("workItems")[0].GetProperty("id").GetString());
            }
            Assert.Equal(new[] { "?workLimit=7&checkpointLimit=9", "?workLimit=100&checkpointLimit=30" }, observed.ToArray());
            await local.StopAsync(ct);
            await remote.StopAsync(ct);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
