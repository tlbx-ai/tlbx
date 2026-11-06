using System.Security.Cryptography;
using System.Text.Json;
using Ai.Tlbx.Builds.Server;
using Xunit;
namespace Ai.Tlbx.Builds.Server.Tests;

public sealed class RealArchiveProtocolTests
{
    public sealed record Package(string Tag, string Rid, string Archive, string Sbom);
    [Fact]
    public async Task ExistingVendorArchivesPassTheProductionVerifierWithoutChangingTheirBytes()
    {
        var inventoryPath = Environment.GetEnvironmentVariable("BUILDS_REAL_ARCHIVES");
        if (string.IsNullOrEmpty(inventoryPath)) Assert.Skip("Set BUILDS_REAL_ARCHIVES to an external inventory of original release archive/SPDX pairs.");
        var inventory = JsonSerializer.Deserialize<Package[]>(await File.ReadAllTextAsync(inventoryPath!), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.NotEmpty(inventory);
        var scratchRoot = Path.Combine(Path.GetDirectoryName(inventoryPath!)!, "real-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchRoot);
        try
        {
            var verifier = new ArchiveVerifier(new ReleaseSignatureVerifier());
            var results = new List<object>();
            foreach (var package in inventory)
            {
                var scratch = Path.Combine(scratchRoot, package.Tag + "-" + package.Rid);
                var original = await verifier.VerifyAsync(package.Archive, package.Sbom, scratch, package.Rid, ReleaseIdentity.Parse(package.Tag), TestContext.Current.CancellationToken);
                Assert.NotEmpty(original);
                Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(scratch, "version.json")));
                await using var archive = File.OpenRead(package.Archive);
                await using var sbom = File.OpenRead(package.Sbom);
                results.Add(new { package.Tag, package.Rid, archiveSha256 = Convert.ToHexString(await SHA256.HashDataAsync(archive)), sbomSha256 = Convert.ToHexString(await SHA256.HashDataAsync(sbom)), manifestBytes = original.Length, verified = true });
                Directory.Delete(scratch, true);
            }
            await File.WriteAllTextAsync(inventoryPath + ".verified.json", JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { Directory.Delete(scratchRoot, true); }
    }
}
